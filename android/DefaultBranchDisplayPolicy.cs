namespace PgrVoice.AndroidApp;

/// <summary>
/// 游戏选项和选后显示台词的已核对照；不把配音稿中的选项复述当作游戏必显对白。
/// 仅覆盖 moviezx05005ba 已逐项核实的 Action471/498/510/517（来源见报告）。
/// 正式包、选项现音和听书顺序保持原样；不能据此向其它菜单推测跳句。
/// </summary>
public static class DefaultBranchDisplayPolicy
{
    const string Section = "ch32-71fcf055b68c2cfb1350";
    const string Menu = Section + "-menu-002";
    const string Curve472 = Section + "-6c6d9d04a1bd03b1e845";
    const string Curve473 = Section + "-7001ae5fb9f7f7a86290";
    static readonly string[] FirstIds =
    {
        Section + "-97a85fcbaa1869a649d1",
        Section + "-e873d4c8b074d3e749d8"
    };
    static readonly string[] Labels =
    {
        "曲，九龙的人类，都去哪里了？",
        "曲，九龙其余的人类，都和空中花园一同离开了？"
    };

    public static bool TryGetVisibleBranch(PlaybackEngine engine, out int number)
    {
        number = 0;
        if (engine.CurrentId != Curve472 || engine.ReviewRoute != null || engine.ExportNavigation().Current.SingleLine ||
            !engine.Choices.TryGetValue(Menu, out var chosen) || chosen != Section + "-route-002-01") return false;
        // 只读重建精确映射的原入口，沿用全文、图链和来源契约；不改变当前播放状态。
        var probe = new PlaybackEngine(engine.Pack);
        if (!probe.ImportNavigation(engine.ExportNavigation()) || !probe.ConfirmGameLine(FirstIds[1]) ||
            !TryGetDisplayAnchor(probe, 2, out _, out var anchor) || anchor?.Id != engine.CurrentId) return false;
        number = 2; return true;
    }

    public static bool TryGetDisplayAnchor(PlaybackEngine engine, int number, out Node? first, out Node? anchor)
    {
        if (SourceChoicePolicy.TryGetDisplayAnchor(engine, number, out first, out anchor)) return true;
        if (TryGetNested32DisplayAnchor(engine, number, out first, out anchor)) return true;
        first = anchor = null;
        var pack = engine.Pack;
        if (number is not (1 or 2) || pack.Id != "pgr-ch32" || pack.SchemaVersion != 3 ||
            engine.ReviewRoute != null || engine.ExportNavigation().Current.SingleLine ||
            !pack.ById.TryGetValue(Menu, out var menu) || menu.Archived || menu.Kind != "choice" ||
            menu.MenuType != "exclusive" || menu.SectionId != Section || menu.Options.Count != 2 ||
            menu.SetFacts.Count != 0 || !string.IsNullOrEmpty(menu.CompleteRoute) || !engine.Allowed(menu)) return false;
        for (int i = 0; i < 2; i++)
        {
            string path = Section + "-route-002-0" + i;
            var option = menu.Options[i];
            if (option.Id != path || option.PathId != path || option.TargetId != FirstIds[i] || option.Label != Labels[i] ||
                option.ReturnId != Menu + "-merge" || option.MergeId != Menu + "-merge" ||
                !option.BodyVerified || !option.ExitVerified || option.BodyEvidence.Count == 0 || option.ExitEvidence.Count == 0 ||
                option.BodyEvidence.Any(string.IsNullOrWhiteSpace) || option.ExitEvidence.Any(string.IsNullOrWhiteSpace) ||
                option.Requires.Count != 0 || option.Excludes.Count != 0 || !string.IsNullOrWhiteSpace(option.ConditionNote) ||
                !option.SegmentIds.SequenceEqual(new[] { FirstIds[i], path + "-return" }) ||
                !Line(pack, FirstIds[i], "指挥官", Labels[i], path, path + "-return", out _) ||
                !pack.ById.TryGetValue(path + "-return", out var ret) || ret.Archived || ret.Kind != "return" ||
                ret.SectionId != Section || ret.PathId != path || ret.NextId != Menu + "-merge" ||
                ret.SetFacts.Count != 0 || ret.CompleteRoute != path) return false;
        }
        if (!pack.ById.TryGetValue(Menu + "-merge", out var merge) || merge.Archived || merge.Kind != "merge" ||
            merge.SectionId != Section || merge.PathId.Length != 0 || merge.NextId != Curve472 ||
            merge.SetFacts.Count != 0 || !string.IsNullOrEmpty(merge.CompleteRoute) ||
            !Line(pack, Curve472, "曲", "九龙人民不会逃离这颗星球，我们只留下了必要的文明延续的种子", "", Curve473, out var second) ||
            !Line(pack, Curve473, "曲", "其他的人……都在这里了。", "", Section + "-c084f3346fadb386a32c", out var common)) return false;
        var selected = menu.Options[number - 1];
        if (engine.Mode == RunMode.Choice && ReferenceEquals(engine.Current, menu))
        {
            if (!DefaultBranchPolicy.TryChoice(engine, number, out var choice, out var candidate) || choice?.Id != selected.Id || candidate?.Id != selected.TargetId) return false;
        }
        else if (engine.Mode != RunMode.Following || engine.CurrentId != selected.TargetId ||
            !engine.Choices.TryGetValue(Menu, out var chosen) || chosen != selected.PathId || !engine.Allowed(engine.Current!)) return false;
        first = pack.ById[selected.TargetId];
        anchor = number == 1 ? common : second;
        return anchor != null && engine.Allowed(anchor);
    }

    // 原Movie Action498第二项经499直达500；Action510两项经511直达512；Action517两项经518直达519。
    // 选项只在菜单显示。保留选项配音，接实际首句时不额外点游戏。
    static bool TryGetNested32DisplayAnchor(PlaybackEngine engine, int number, out Node? first, out Node? anchor)
    {
        first = anchor = null;
        if (engine.Pack.Id != "pgr-ch32" || engine.Pack.SchemaVersion != 3 || number is not (1 or 2) ||
            engine.Current?.SectionId != Section || engine.ReviewRoute != null || engine.ExportNavigation().Current.SingleLine) return false;
        string menuId = engine.Mode == RunMode.Choice ? engine.CurrentId! :
            engine.Current.PathId.StartsWith(Section + "-route-004-", StringComparison.Ordinal) ? Section + "-menu-004" :
            engine.Current.PathId.StartsWith(Section + "-route-005-", StringComparison.Ordinal) ? Section + "-menu-005" :
            engine.Current.PathId.StartsWith(Section + "-route-006-", StringComparison.Ordinal) ? Section + "-menu-006" : "";
        bool outer = menuId == Section + "-menu-004";
        bool breath = menuId == Section + "-menu-006";
        if (!outer && !breath && menuId != Section + "-menu-005") return false;
        if (!engine.Pack.ById.TryGetValue(menuId, out var menu) || menu.Options.Count != 2 || menu.Archived ||
            menu.Kind != "choice" || menu.MenuType != "exclusive") return false;
        var option = menu.Options[number - 1];
        string expectedFirst = Section + (outer ? number == 1 ? "-4968d11ac2fcf2b118ce" : "-ef2e53bb35a8c396c964" : breath
            ? number == 1 ? "-dcccc09ffd709d29163f" : "-3c86674339555a718172"
            : number == 1 ? "-452538f890b46a8e8a10" : "-4c93d48d53bbbd670f35");
        string expectedAnchor = Section + (outer ? number == 1 ? "-555e8866715f1e383d49" : "-061fcca4339ed40c00dd" : breath ? "-2b406dfd79a878cd712e" : "-0b9c052ff12cde25a99c");
        string label = outer ? number == 1 ? "进行链接。" : "再等等。" : breath ? number == 1 ? "（深呼吸）" : "不要。" : number == 1 ? "……谢谢。" : "我并不是……";
        if (option.TargetId != expectedFirst || option.Label != label || !option.BodyVerified || !option.ExitVerified ||
            !engine.Pack.ById.TryGetValue(expectedFirst, out var f) || f.Speaker != "指挥官" || f.Text != label || f.PathId != option.PathId ||
            !engine.Pack.ById.TryGetValue(expectedAnchor, out var a) || a.Kind != "line" || a.Archived ||
            a.Speaker != (outer || breath ? "旁白" : "凡妮莎") || a.Text != (outer ? number == 1
                ? "浓黑夜幕拉起，穿着整洁斗篷的粉发构造体出现在了饕风虐雪的荒野。" : "虽然绝对相信阿西莫夫留下来的笔记，但……" :
                breath ? "付出这么多努力，不能让它们毁于一旦。" : "嗤。")) return false;
        var probe = new PlaybackEngine(engine.Pack);
        if (!probe.ImportNavigation(engine.ExportNavigation())) return false;
        if (engine.Mode == RunMode.Choice)
        {
            if (!DefaultBranchPolicy.TryChoice(engine, number, out _, out var chosen) || chosen != f) return false;
            probe.Commit(menuId);
            probe.SelectBranch(probe.AvailableOptions.FindIndex(o => o.Id == option.Id));
        }
        else if (engine.Mode != RunMode.Following || engine.CurrentId != expectedFirst ||
            !engine.Choices.TryGetValue(menuId, out var chosenPath) || chosenPath != option.PathId) return false;
        if (!probe.ConfirmCurrentPosition() || !DefaultBranchPolicy.TryNext(probe, out var next, out _) || next?.Id != expectedAnchor) return false;
        first = f; anchor = a; return true;
    }

    static bool Line(Pack pack, string id, string speaker, string text, string path, string next, out Node? node)
    {
        node = null;
        if (!pack.ById.TryGetValue(id, out var line) || line.Archived || line.Kind != "line" ||
            line.SectionId != Section || line.Speaker != speaker || line.Text != text || line.PathId != path || line.NextId != next ||
            line.SetFacts.Count != 0 || line.Options.Count != 0 || !string.IsNullOrEmpty(line.CompleteRoute)) return false;
        node = line; return true;
    }

    public static bool TryMatchDisplayAnchor(PlaybackEngine engine, int number, IReadOnlyList<OcrBlock> blocks,
        int width, int height, out string? nodeId)
    {
        nodeId = null;
        if (engine.Mode != RunMode.Choice || width <= 0 || height <= 0 ||
            !TryGetDisplayAnchor(engine, number, out _, out var anchor) || anchor == null) return false;
        // 复用原有几何相邻块合并，实际长对白换行时仍要求完整正文，不作子串匹配。
        var evidence = BranchFollowPolicy.Evidence(blocks, width, height);
        string text = Matcher.Normalize(anchor.Text);
        if (evidence.Count(b => b.Text == text) != 1 ||
            !evidence.Any(b => b.Text == Matcher.Normalize(anchor.Speaker)) ||
            engine.Pack.Nodes.Count(n => !n.Archived && n.Kind == "line" && n.SectionId == Section &&
                Matcher.Normalize(n.Text).Length >= 4 && evidence.Any(b => b.Text == Matcher.Normalize(n.Text))) != 1 ||
            engine.Current!.Options.Any(o => evidence.Any(b => b.Text == Matcher.Normalize(o.Label)))) return false;
        nodeId = anchor.Id; return true;
    }
}
