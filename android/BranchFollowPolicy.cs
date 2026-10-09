namespace PgrVoice.AndroidApp;

/// <summary>OCR 输入图上的矩形；不表示已获准注入手势。</summary>
public readonly record struct BranchFollowRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public double CenterX => (Left + Right) / 2;
    public double CenterY => (Top + Bottom) / 2;
    public bool Contains(double x, double y) => double.IsFinite(x) && double.IsFinite(y) &&
        x >= Left && x <= Right && y >= Top && y <= Bottom;
}

public sealed record BranchFollowOptionHit(string OptionId, string PathId, string Label, BranchFollowRect Bounds);
public sealed record BranchFollowMenuMatch(string MenuId, IReadOnlyList<BranchFollowOptionHit> Options);

/// <summary>
/// 普通二选一的只读画面证据。只认完整文字，不作模糊猜选；会话代次、两帧共识及手势仍由调用方控制。
/// </summary>
public static class BranchFollowPolicy
{
    const double MinimumConfidence = .85;
    internal sealed record TextEvidence(string Text, BranchFollowRect Bounds);

    public static bool IsEligibleMenu(PlaybackEngine engine, string menuId, out string reason)
        => IsEligibleMenu(engine, menuId, requireOcrLabels: true, out reason);

    /// <summary>玩家确认同名项，不用 OCR 字长门槛；正文和路线边界仍须已经核实。</summary>
    public static bool IsEligibleManualMenu(PlaybackEngine engine, string menuId, out string reason)
        => IsEligibleMenu(engine, menuId, requireOcrLabels: false, out reason);

    /// <summary>人物与话题只由玩家明确指认；不推算条件、不安排选择顺序。</summary>
    public static bool IsEligibleInteractionMenu(PlaybackEngine engine, string menuId, out string reason)
    {
        reason = "";
        if (engine.Pack.SchemaVersion != 3 || engine.Mode != RunMode.Choice || engine.CurrentId != menuId ||
            !engine.Pack.ById.TryGetValue(menuId, out var menu) || menu.Archived ||
            menu.Kind != "choice" || menu.MenuType is not ("interaction" or "topics") ||
            !engine.Allowed(menu) || engine.ReviewRoute != null || menu.Options.Count == 0 ||
            menu.SetFacts.Count != 0 || !string.IsNullOrEmpty(menu.CompleteRoute))
            return Fail("当前不是可确认的人物或话题菜单，请按游戏画面手动定位。", out reason);
        if (menu.Options.Any(o => string.IsNullOrWhiteSpace(o.Id) || string.IsNullOrWhiteSpace(o.PathId) ||
                string.IsNullOrWhiteSpace(o.Label)) ||
            menu.Options.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != menu.Options.Count ||
            menu.Options.Select(o => o.PathId).Distinct(StringComparer.Ordinal).Count() != menu.Options.Count)
            return Fail("人物或话题的路线身份不明确，请手动定位。", out reason);
        return true;
    }

    public static bool TryGetInteractionTarget(PlaybackEngine engine, string menuId, string optionId,
        out Node? target, out string reason)
    {
        target = null;
        if (!IsEligibleInteractionMenu(engine, menuId, out reason)) return false;
        var menu = engine.Pack.ById[menuId];
        var option = engine.AvailableOptions.SingleOrDefault(o => o.Id == optionId);
        if (option == null || !option.BodyVerified || option.BodyEvidence.Count == 0 ||
            option.BodyEvidence.Any(string.IsNullOrWhiteSpace) ||
            !engine.Pack.ById.TryGetValue(option.TargetId, out var first) || first.Archived ||
            first.SectionId != menu.SectionId || first.PathId != option.PathId ||
            !option.SegmentIds.Contains(first.Id) || first.Kind is not ("line" or "choice"))
            return Fail("该选项的正文入口尚未核实，请在台词页按游戏当前句手动定位。", out reason);
        if (first.Kind == "line" && !TryDirectLines(engine.Pack, menu, option, out _))
            return Fail("这段正文含有未核实连接，请手动定位当前句。", out reason);
        // 此调用只探测玩家同项确认的结果，使用核心已有的指认菜单权限，不填任务事实。
        var probe = new PlaybackEngine(engine.Pack);
        if (!probe.ImportNavigation(engine.ExportNavigation()) || !probe.OpenGameMenu(menuId, menu.SectionId))
            return Fail("当前人物菜单已经变化，请重新确认。", out reason);
        int index = probe.AvailableOptions.FindIndex(o => o.Id == optionId);
        probe.SelectBranch(index);
        if (probe.CurrentId != first.Id || !probe.Allowed(first) ||
            (first.Kind == "line" ? probe.Mode != RunMode.Following : probe.Mode != RunMode.Choice))
            return Fail("这条人物路线目前不能可靠续接，请手动核对。", out reason);
        target = first;
        return true;
    }

    public static string InteractionOptionLabel(PlaybackEngine engine, Node menu, ChoiceOption option)
    {
        string label = option.Label;
        var topics = menu.MenuType == "interaction" ? DirectTopicsMenu(engine.Pack, menu, option) : null;
        if (topics != null)
        {
            // 退出交谈不是一个要听完的实质话题；开场 Heard 也不是人物全部完成。
            var repeatable = topics.Options.Where(o => o.BodyVerified && o.ExitVerified && o.ReturnId == topics.Id).ToArray();
            if (repeatable.Length > 0)
                label += $" · 话题 {repeatable.Count(o => engine.Heard.Contains(o.PathId))}/{repeatable.Length}";
            else if (engine.Heard.Contains(option.PathId)) label += " · 已听开场";
        }
        else if (engine.Heard.Contains(option.PathId))
            label += option.ExitVerified ? " · 已聊该段" : " · 已听已核片段";
        if (!option.BodyVerified) label += " · 正文待核实";
        if (!engine.ConditionsKnown(option)) label += " · 按游戏当前状态确认";
        return label;
    }

    public static string? ActorMenuId(PlaybackEngine engine)
    {
        if (engine.Current is not { } current) return null;
        var allowed = engine.InteractionMenus.Select(m => m.MenuId).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var node = current;
        while (seen.Add(node.Id))
        {
            if (node.Kind == "choice" && node.MenuType == "interaction" && node.PathId.Length == 0 &&
                node.MenuNavigationEvidence.Count > 0 && allowed.Contains(node.Id)) return node.Id;
            if (node.PathId.Length == 0) return null;
            var owners = engine.Pack.Nodes.Where(n => !n.Archived && n.Kind == "choice" &&
                n.SectionId == current.SectionId && n.Options.Any(o => o.PathId == node.PathId)).ToArray();
            if (owners.Length != 1) return null;
            node = owners[0];
        }
        return null;
    }

    public static Node? DirectTopicsMenu(Pack pack, Node menu, ChoiceOption option)
    {
        if (!option.BodyVerified) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? id = option.TargetId;
        while (id != null && seen.Add(id) && option.SegmentIds.Contains(id) && pack.ById.TryGetValue(id, out var node) &&
            !node.Archived && node.SectionId == menu.SectionId && node.PathId == option.PathId)
        {
            if (node.Kind == "choice") return node.MenuType == "topics" ? node : null;
            if (node.Kind != "line") return null;
            id = node.NextId;
        }
        return null;
    }

    static bool IsEligibleMenu(PlaybackEngine engine, string menuId, bool requireOcrLabels, out string reason)
    {
        reason = "";
        if (engine.Pack.SchemaVersion != 3 || engine.Mode != RunMode.Choice || engine.CurrentId != menuId ||
            !engine.Pack.ById.TryGetValue(menuId, out var menu) || menu.Archived || menu.Kind != "choice" ||
            !engine.Allowed(menu) || engine.ReviewRoute != null)
            return Fail("分支位置已变化，请按当前游戏画面手动选择。", out reason);
        if (menu.MenuType != "exclusive" || menu.Options.Count != 2 || menu.SetFacts.Count != 0 ||
            !string.IsNullOrEmpty(menu.CompleteRoute))
            return Fail("此处为互动、多话题或复杂分支，请手动选择。", out reason);
        var labels = menu.Options.Select(o => Matcher.Normalize(o.Label)).ToArray();
        if (labels.Any(t => t.Length < (requireOcrLabels ? 2 : 1) || t == "…") || labels.Distinct(StringComparer.Ordinal).Count() != 2 ||
            menu.Options.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != 2 ||
            menu.Options.Select(o => o.PathId).Distinct(StringComparer.Ordinal).Count() != 2)
            return Fail("选项文字或路线不能唯一确认，请手动选择。", out reason);
        foreach (var option in menu.Options)
        {
            if (!option.BodyVerified || option.BodyEvidence.Count == 0 || option.BodyEvidence.Any(string.IsNullOrWhiteSpace) || option.Requires.Count != 0 ||
                option.Excludes.Count != 0 || string.IsNullOrWhiteSpace(option.Id) || string.IsNullOrWhiteSpace(option.PathId))
                return Fail("分支正文或条件尚未完整核实，请手动选择。", out reason);
            if (!TryDirectLines(engine.Pack, menu, option, out _))
                return Fail("分支入口含循环、条件或未核实正文，请手动选择。", out reason);
        }
        return true;
    }

    public static bool TryMatchMenu(PlaybackEngine engine, string menuId, IReadOnlyList<OcrBlock> blocks,
        int imageWidth, int imageHeight, out BranchFollowMenuMatch? match, out string reason)
    {
        match = null;
        if (!IsEligibleMenu(engine, menuId, out reason)) return false;
        var evidence = Evidence(blocks, imageWidth, imageHeight);
        var hits = new List<BranchFollowOptionHit>();
        foreach (var option in engine.Pack.ById[menuId].Options)
        {
            var candidates = evidence.Where(e => e.Text == Matcher.Normalize(option.Label)).ToArray();
            if (candidates.Length != 1)
                return Fail("未能在画面中唯一识别完整的两个选项，请手动选择。", out reason);
            hits.Add(new(option.Id, option.PathId, option.Label, candidates[0].Bounds));
        }
        if (!Separated(hits[0].Bounds, hits[1].Bounds))
            return Fail("两个选项的位置重叠或距离不明确，请手动选择。", out reason);
        match = new(menuId, hits.ToArray());
        return true;
    }

    /// <summary>调用方必须另核不同的新帧和相同会话；本方法只比较证据本身。</summary>
    public static bool AreSameMenu(BranchFollowMenuMatch first, BranchFollowMenuMatch second, double maximumDriftPixels = 12)
    {
        if (!double.IsFinite(maximumDriftPixels) || maximumDriftPixels < 0 || first.MenuId != second.MenuId ||
            first.Options.Count != 2 || second.Options.Count != 2) return false;
        for (int i = 0; i < 2; i++)
        {
            var a = first.Options[i]; var b = second.Options[i];
            if (a.OptionId != b.OptionId || a.PathId != b.PathId || a.Label != b.Label ||
                Math.Abs(a.Bounds.Left - b.Bounds.Left) > maximumDriftPixels ||
                Math.Abs(a.Bounds.Top - b.Bounds.Top) > maximumDriftPixels ||
                Math.Abs(a.Bounds.Right - b.Bounds.Right) > maximumDriftPixels ||
                Math.Abs(a.Bounds.Bottom - b.Bounds.Bottom) > maximumDriftPixels) return false;
        }
        return true;
    }

    public static bool TryMatchSelectedLine(PlaybackEngine engine, string menuId, string optionId,
        IReadOnlyList<OcrBlock> blocks, int imageWidth, int imageHeight, out string? nodeId, out string reason)
    {
        nodeId = null;
        if (!IsEligibleMenu(engine, menuId, out reason)) return false;
        var menu = engine.Pack.ById[menuId];
        var selected = menu.Options.SingleOrDefault(o => o.Id == optionId);
        if (selected == null) return Fail("本次点选的分支已失效，请手动确认。", out reason);
        var evidence = Evidence(blocks, imageWidth, imageHeight);
        if (menu.Options.All(o => evidence.Any(e => e.Text == Matcher.Normalize(o.Label))))
            return Fail("画面仍显示分支选项，等待实际台词出现。", out reason);

        var sectionLines = engine.Pack.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.SectionId == menu.SectionId)
            .Select(n => (Node: n, Text: Matcher.Normalize(n.Text))).ToArray();
        if (sectionLines.Count(n => n.Text.Length >= 4 && evidence.Any(e => e.Text == n.Text)) != 1)
            return Fail("画面台词仍有多个可能位置，请等待稳定的实际字幕。", out reason);
        var hits = new List<string>();
        TryDirectLines(engine.Pack, menu, selected, out var directLines);
        var directIds = directLines.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in sectionLines.Where(n => directIds.Contains(n.Node.Id)))
        {
            // 简短应答、同节同文及部分字幕均不能证明玩家进入了哪条路线。
            if (candidate.Text.Length < 4 || sectionLines.Count(n => n.Text == candidate.Text) != 1 ||
                evidence.Count(e => e.Text == candidate.Text) != 1) continue;
            // 选项文字可能仍在淡出；与选项同文时，还要看到该句实际说话人。
            if (menu.Options.Any(o => Matcher.Normalize(o.Label) == candidate.Text) &&
                (Matcher.Normalize(candidate.Node.Speaker).Length < 2 ||
                 !evidence.Any(e => e.Text == Matcher.Normalize(candidate.Node.Speaker)))) continue;
            hits.Add(candidate.Node.Id);
        }
        if (hits.Count != 1) return Fail("尚未唯一确认点选后的实际台词，请手动确认或等待字幕。", out reason);
        nodeId = hits[0];
        return true;
    }

    /// <summary>用户先在游戏选择后，单次识别下一句；只自动接受当前菜单有限开头内的唯一已核整句。</summary>
    public static bool TryMatchMenuLine(PlaybackEngine engine, string menuId, IReadOnlyList<OcrBlock> blocks,
        int imageWidth, int imageHeight, out string? nodeId, out string reason)
    {
        nodeId = null;
        if (engine.Mode != RunMode.Choice || engine.CurrentId != menuId || engine.ReviewRoute != null ||
            !engine.Pack.ById.TryGetValue(menuId, out var menu) || menu.Archived || menu.Kind != "choice" ||
            menu.MenuType != "exclusive" || !engine.Allowed(menu))
            return Fail("请核对候选台词与当前人物、话题或路线后确认。", out reason);
        var evidence = Evidence(blocks, imageWidth, imageHeight);
        var lines = engine.Pack.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.SectionId == menu.SectionId)
            .Select(n => (Node: n, Text: Matcher.Normalize(n.Text))).ToArray();
        var hits = lines.Where(n => n.Text.Length >= 4 && evidence.Any(e => e.Text == n.Text)).ToArray();
        if (hits.Length != 1 || evidence.Count(e => e.Text == hits[0].Text) != 1)
            return Fail("完整正文尚未唯一匹配，请等文字显示完整再识别，或手动核对候选。", out reason);
        var hit = hits[0];
        var offer = BranchAnchorPolicy.Create(engine, maximumLinesPerOption: 2);
        var cards = offer.Cards.Where(c => c.NodeId == hit.Node.Id && !c.IsAmbiguous).ToArray();
        if (cards.Length != 1 || !BranchAnchorPolicy.TryResolve(engine, offer, cards[0].Id, out var resolved, out _) || resolved?.Id != hit.Node.Id)
            return Fail("这句不在本次已核实的分支开头，请对照游戏手动确认；未知连接仍保持等待。", out reason);
        if (menu.Options.All(o => evidence.Any(e => e.Text == Matcher.Normalize(o.Label))) ||
            menu.Options.Any(o => Matcher.Normalize(o.Label) == hit.Text) &&
            (Matcher.Normalize(hit.Node.Speaker).Length < 2 || !evidence.Any(e => e.Text == Matcher.Normalize(hit.Node.Speaker))))
            return Fail("画面可能仍是选项，请等实际对白出现后再识别。", out reason);
        nodeId = hit.Node.Id; reason = ""; return true;
    }

    /// <summary>仅用于已知裁剪/缩放参数；调用方不得用它猜单应用录屏的屏幕偏移。</summary>
    public static bool TryMapToScreen(BranchFollowRect source, int imageWidth, int imageHeight,
        BranchFollowRect capturedRegion, int screenWidth, int screenHeight, out BranchFollowRect mapped)
    {
        mapped = default;
        if (!Inside(source, imageWidth, imageHeight) || !Inside(capturedRegion, screenWidth, screenHeight)) return false;
        mapped = new(capturedRegion.Left + source.Left * capturedRegion.Width / imageWidth,
            capturedRegion.Top + source.Top * capturedRegion.Height / imageHeight,
            capturedRegion.Left + source.Right * capturedRegion.Width / imageWidth,
            capturedRegion.Top + source.Bottom * capturedRegion.Height / imageHeight);
        return Inside(mapped, screenWidth, screenHeight);
    }

    static bool Fail(string message, out string reason) { reason = message; return false; }

    static bool TryDirectLines(Pack pack, Node menu, ChoiceOption option, out List<Node> lines)
    {
        // 已核正文入口和出口是两件事。只确认第一次边界以前的正文；未知出口继续交给引擎原有停点。
        lines = new();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? id = option.TargetId;
        while (id != null)
        {
            if (!seen.Add(id) || !pack.ById.TryGetValue(id, out var node) || node.Archived || node.SectionId != menu.SectionId)
                return false;
            if (node.Kind is "choice" or "gap" or "return" or "merge" or "end")
                return lines.Count > 0;
            if (node.Kind != "line" || node.PathId != option.PathId || !option.SegmentIds.Contains(id) ||
                node.Options.Count != 0 || node.SetFacts.Count != 0 || !string.IsNullOrEmpty(node.CompleteRoute)) return false;
            lines.Add(node);
            id = node.NextId;
        }
        return lines.Count > 0;
    }

    static bool Inside(BranchFollowRect r, int width, int height) => width > 0 && height > 0 &&
        double.IsFinite(r.Left) && double.IsFinite(r.Top) && double.IsFinite(r.Right) && double.IsFinite(r.Bottom) &&
        r.Width >= 2 && r.Height >= 2 && r.Left >= 0 && r.Top >= 0 && r.Right <= width && r.Bottom <= height;

    static bool Separated(BranchFollowRect a, BranchFollowRect b)
    {
        double gap = Math.Max(2, Math.Min(a.Height, b.Height) * .15);
        return a.Right + gap <= b.Left || b.Right + gap <= a.Left || a.Bottom + gap <= b.Top || b.Bottom + gap <= a.Top;
    }

    internal static List<TextEvidence> Evidence(IReadOnlyList<OcrBlock> blocks, int width, int height)
    {
        var evidence = new List<TextEvidence>();
        if (width <= 0 || height <= 0 || blocks.Count > 64) return evidence;
        foreach (var block in blocks)
        {
            if (block == null || !double.IsFinite(block.Score) || block.Score < MinimumConfidence ||
                block.Box == null || block.Box.Length < 4 || block.Box.Any(p => p == null || p.Length != 2 || p.Any(v => !double.IsFinite(v)))) continue;
            var rect = new BranchFollowRect(block.Box.Min(p => p[0]), block.Box.Min(p => p[1]), block.Box.Max(p => p[0]), block.Box.Max(p => p[1]));
            string text = Matcher.Normalize(block.Text ?? "");
            if (text.Length > 0 && Inside(rect, width, height)) evidence.Add(new(text, rect));
        }
        var ordered = evidence.OrderBy(e => e.Bounds.Top).ThenBy(e => e.Bounds.Left).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            string text = ordered[i].Text;
            var bounds = ordered[i].Bounds;
            for (int j = i + 1; j < Math.Min(i + 4, ordered.Length); j++)
            {
                var before = ordered[j - 1].Bounds; var next = ordered[j].Bounds;
                bool sameRow = Math.Abs(before.CenterY - next.CenterY) <= Math.Min(before.Height, next.Height) * .4 &&
                    next.Left >= before.Right && next.Left - before.Right <= Math.Max(30, before.Height * 3);
                bool wrapped = next.Top >= before.Bottom && next.Top - before.Bottom <= Math.Max(before.Height, next.Height) * 1.2 &&
                    Math.Abs(bounds.Left - next.Left) <= Math.Min(before.Height, next.Height) &&
                    Math.Min(bounds.Right, next.Right) - Math.Max(bounds.Left, next.Left) >= Math.Min(bounds.Width, next.Width) * .5;
                if (!sameRow && !wrapped) break;
                text += ordered[j].Text;
                bounds = new(Math.Min(bounds.Left, next.Left), Math.Min(bounds.Top, next.Top), Math.Max(bounds.Right, next.Right), Math.Max(bounds.Bottom, next.Bottom));
                evidence.Add(new(text, bounds));
            }
        }
        return evidence;
    }
}
