namespace PgrVoice.AndroidApp;

/// <summary>安卓显式默认分支的有限预览；不改共享导航权限，不代选人物或条件话题。</summary>
public static class DefaultBranchPolicy
{
    public static bool TryChoice(PlaybackEngine engine, int number, out ChoiceOption? option, out Node? first)
    {
        option = null; first = null;
        if (number is not (1 or 2) || engine.Pack.SchemaVersion != 3 || engine.Mode != RunMode.Choice ||
            engine.Current is not { Kind: "choice", MenuType: "exclusive" } menu || menu.Archived ||
            menu.Options.Count < number || !engine.Allowed(menu) || engine.ReviewRoute != null ||
            menu.SetFacts.Count != 0 || !string.IsNullOrEmpty(menu.CompleteRoute) ||
            menu.Options.Any(o => string.IsNullOrWhiteSpace(o.Id) || string.IsNullOrWhiteSpace(o.PathId) ||
                o.Requires.Count != 0 || o.Excludes.Count != 0 || !string.IsNullOrWhiteSpace(o.ConditionNote)) ||
            menu.Options.Select(o => o.Id).Distinct().Count() != menu.Options.Count || menu.Options.Select(o => o.PathId).Distinct().Count() != menu.Options.Count)
            return false;
        var chosen = menu.Options[number - 1];
        if (!chosen.BodyVerified || chosen.BodyEvidence.Count == 0 || chosen.BodyEvidence.Any(string.IsNullOrWhiteSpace) ||
            !engine.AvailableOptions.Any(o => o.Id == chosen.Id) ||
            !engine.Pack.ById.TryGetValue(chosen.TargetId, out var node) || node.Kind != "line" || node.Archived ||
            node.SectionId != menu.SectionId || node.PathId != chosen.PathId || !chosen.SegmentIds.Contains(node.Id) ||
            node.SetFacts.Count != 0 || node.Options.Count != 0 || !string.IsNullOrEmpty(node.CompleteRoute)) return false;
        var probe = new PlaybackEngine(engine.Pack);
        if (!probe.ImportNavigation(engine.ExportNavigation())) return false;
        probe.Commit(menu.Id);
        probe.SelectBranch(probe.AvailableOptions.FindIndex(o => o.Id == chosen.Id));
        if (probe.CurrentId != node.Id || probe.Mode != RunMode.Following || !probe.Allowed(node)) return false;
        option = chosen; first = node; return true;
    }

    public static bool TryMatchFirst(PlaybackEngine engine, int number, IReadOnlyList<OcrBlock> blocks,
        int width, int height, out string? nodeId)
    {
        nodeId = null;
        if (!TryChoice(engine, number, out _, out var first) || first == null) return false;
        string text = Matcher.Normalize(first.Text);
        if (text.Length >= 4)
        {
            if (!BranchFollowPolicy.TryMatchMenuLine(engine, engine.CurrentId!, blocks, width, height, out var found, out _) || found != first.Id) return false;
        }
        else
        {
            // 短首句额外依赖调用方保存的完整菜单与新位置证据；这里不允许纯标点或一字应答。
            string speaker = Matcher.Normalize(first.Speaker);
            var valid = blocks.Where(b => double.IsFinite(b.Score) && b.Score >= .85 && b.Box != null && b.Box.Length >= 4 &&
                b.Box.All(p => p != null && p.Length == 2 && p.All(double.IsFinite) && p[0] >= 0 && p[1] >= 0 && p[0] <= width && p[1] <= height)).ToArray();
            if (text.Length < 2 || !text.Any(char.IsLetterOrDigit) || speaker.Length < 2 ||
                valid.Count(b => Matcher.Normalize(b.Text) == text) != 1 || !valid.Any(b => Matcher.Normalize(b.Text) == speaker) ||
                engine.Pack.Nodes.Count(n => !n.Archived && n.Kind == "line" && n.SectionId == first.SectionId && Matcher.Normalize(n.Text) == text) != 1 ||
                engine.Pack.Nodes.Count(n => !n.Archived && n.Kind == "line" && n.SectionId == first.SectionId &&
                    Matcher.Normalize(n.Text).Length >= 2 && valid.Any(b => Matcher.Normalize(b.Text) == Matcher.Normalize(n.Text))) != 1 ||
                engine.Current!.Options.All(o => valid.Any(b => Matcher.Normalize(b.Text) == Matcher.Normalize(o.Label)))) return false;
        }
        nodeId = first.Id; return true;
    }

    public static BranchFollowMenuMatch? ReadMenu(PlaybackEngine engine, IReadOnlyList<OcrBlock> blocks, int width, int height)
    {
        if (engine.Current is not { Kind: "choice", MenuType: "exclusive" } menu || menu.Options.Count == 0 || menu.Options.Count > 16) return null;
        var hits = new List<BranchFollowOptionHit>();
        foreach (var option in menu.Options)
        {
            string text = Matcher.Normalize(option.Label);
            if (text.Length < 2 || menu.Options.Count(o => Matcher.Normalize(o.Label) == text) != 1) return null;
            var matches = blocks.Where(b => double.IsFinite(b.Score) && b.Score >= .85 && Matcher.Normalize(b.Text) == text &&
                b.Box != null && b.Box.Length >= 4 && b.Box.All(p => p != null && p.Length == 2 && p.All(double.IsFinite))).ToArray();
            if (matches.Length != 1) return null;
            var box = matches[0].Box;
            var bounds = new BranchFollowRect(box.Min(p => p[0]), box.Min(p => p[1]), box.Max(p => p[0]), box.Max(p => p[1]));
            if (bounds.Width < 2 || bounds.Height < 2 || bounds.Left < 0 || bounds.Top < 0 || bounds.Right > width || bounds.Bottom > height) return null;
            if (hits.Any(h => Overlap(h.Bounds, bounds))) return null;
            hits.Add(new(option.Id, option.PathId, option.Label, bounds));
        }
        return new(menu.Id, hits);
    }

    public static bool Overlap(BranchFollowRect a, BranchFollowRect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    public static bool TryNext(PlaybackEngine engine, out Node? next, out int steps, bool includeChoice = false)
    {
        next = null; steps = 0;
        if (engine.Pack.SchemaVersion != 3 || engine.Mode != RunMode.Following ||
            engine.Current is not { Kind: "line" } current || !engine.Allowed(current) ||
            engine.ReviewRoute != null || engine.ExportNavigation().Current.SingleLine) return false;
        var seen = new HashSet<string> { current.Id };
        var previous = current;
        string? id = current.NextId;
        while (id != null && seen.Add(id))
        {
            if (!engine.Pack.ById.TryGetValue(id, out var node) || node.Archived || node.SectionId != current.SectionId ||
                !engine.Allowed(node) || node.SetFacts.Count != 0 ||
                (node.Options.Count != 0 && !(includeChoice && node.Kind == "choice")) ||
                (node.Kind is not ("line" or "return" or "merge") && !(includeChoice && node.Kind == "choice"))) return false;
            if (!VerifiedExit(engine, previous, node)) return false;
            if (node.Kind == "line" || includeChoice && node.Kind == "choice")
            {
                if (!string.IsNullOrEmpty(node.CompleteRoute)) return false;
                var probe = new PlaybackEngine(engine.Pack);
                if (!probe.ImportNavigation(engine.ExportNavigation()) || !probe.ConfirmCurrentPosition()) return false;
                for (int i = 0; i < 8; i++)
                {
                    probe.Next(); steps++;
                    if (probe.CurrentId == node.Id && probe.Mode == (node.Kind == "choice" ? RunMode.Choice : RunMode.Following)) { next = node; return true; }
                    if (probe.Mode != RunMode.Merge || probe.Current?.Kind != "merge") return false;
                }
                return false;
            }
            if (node.Kind == "return" && !VerifiedReturn(engine, node)) return false;
            previous = node; id = node.NextId;
        }
        return false;
    }

    static bool VerifiedReturn(PlaybackEngine engine, Node node)
    {
        var owners = engine.Pack.Nodes.Where(n => !n.Archived).SelectMany(n => n.Options).Where(o => o.PathId == node.PathId).ToArray();
        return owners.Length == 1 && owners[0].BodyVerified && owners[0].ExitVerified &&
            owners[0].ExitEvidence.Count > 0 && owners[0].ExitEvidence.All(e => !string.IsNullOrWhiteSpace(e));
    }

    public static bool IsVerifiedEnd(PlaybackEngine engine)
    {
        if (engine.Current is not { Kind: "line" } current || engine.Mode != RunMode.Following ||
            engine.ReviewRoute != null || engine.ExportNavigation().Current.SingleLine || !engine.Allowed(current)) return false;
        if (current.NextId == null) return engine.EvaluateCommonAutoPlayNext() is { CurrentIsCommon: true, Code: "end" };
        if (!engine.Pack.ById.TryGetValue(current.NextId, out var end) || end.Kind != "end" || end.Archived ||
            end.SectionId != current.SectionId || !engine.Allowed(end) || !VerifiedExit(engine, current, end) ||
            end.SetFacts.Count != 0 || end.Options.Count != 0) return false;
        // 未核出口不能因为边界节点恰好叫end就冒充完整结束。
        if (current.PathId.Length > 0 && !VerifiedReturn(engine, current)) return false;
        var probe = new PlaybackEngine(engine.Pack);
        if (!probe.ImportNavigation(engine.ExportNavigation()) || !probe.ConfirmCurrentPosition()) return false;
        probe.Next();
        return probe.CurrentId == end.Id && probe.Mode == RunMode.End;
    }

    static bool VerifiedExit(PlaybackEngine engine, Node previous, Node next)
    {
        if (previous.PathId.Length == 0 || previous.PathId == next.PathId) return true;
        var owners = engine.Pack.Nodes.Where(n => !n.Archived).SelectMany(n => n.Options).Where(o => o.PathId == previous.PathId).ToArray();
        return owners.Length == 1 && owners[0].BodyVerified && owners[0].ExitVerified &&
            owners[0].ExitEvidence.Count > 0 && owners[0].ExitEvidence.All(e => !string.IsNullOrWhiteSpace(e)) &&
            (owners[0].ReturnId == next.Id || owners[0].MergeId == next.Id || previous.Kind == "return");
    }
}
