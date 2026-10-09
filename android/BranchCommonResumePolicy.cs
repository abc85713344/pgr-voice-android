namespace PgrVoice.AndroidApp;

/// <summary>只读核对两条完整路线之后同一个共同句，不代表游戏已经到达该句。</summary>
public static class BranchCommonResumePolicy
{
    public static bool TryGetCommonLine(PlaybackEngine engine, string menuId, out Node? candidate, out string reason)
    {
        candidate = null;
        reason = "这处分支没有完整核实的共同续接句，请手动定位。";
        var pack = engine.Pack;
        var snapshot = engine.ExportNavigation();
        if (pack.SchemaVersion != 3 || snapshot.Current.SingleLine || engine.ReviewRoute != null ||
            engine.Current is not { } source || engine.Mode == RunMode.Original ||
            !pack.ById.TryGetValue(menuId, out var menu) || menu.Archived || menu.Kind != "choice" ||
            menu.MenuType != "exclusive" || menu.Options.Count != 2 || menu.PathId.Length != 0 ||
            menu.SectionId != source.SectionId || menu.SetFacts.Count != 0 || !string.IsNullOrEmpty(menu.CompleteRoute)) return false;

        var atMenu = new PlaybackEngine(pack);
        if (!atMenu.ImportNavigation(snapshot)) return false;
        if (source.Id == menuId)
        {
            if (engine.Mode != RunMode.Choice || !engine.Allowed(menu)) return false;
            atMenu.Commit(menuId);
        }
        else
        {
            if (source.Kind != "line" || source.NextId != menuId || !atMenu.ConfirmCurrentPosition() ||
                !atMenu.EvaluateCommonAutoPlayNext().CurrentIsCommon) return false;
            atMenu.Next(true);
            if (atMenu.CurrentId != menuId || atMenu.Mode != RunMode.Choice) return false;
        }
        string? mergeId = menu.Options[0].MergeId;
        if (mergeId == null || !pack.ById.TryGetValue(mergeId, out var merge) || merge.Archived ||
            merge.Kind != "merge" || merge.SectionId != menu.SectionId || merge.PathId.Length != 0 ||
            merge.SetFacts.Count != 0 || !string.IsNullOrEmpty(merge.CompleteRoute) || merge.NextId == null) return false;
        if (menu.Options.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != 2 ||
            menu.Options.Select(o => o.PathId).Distinct(StringComparer.Ordinal).Count() != 2) return false;

        foreach (var option in menu.Options)
        {
            if (!option.BodyVerified || !option.ExitVerified || option.BodyEvidence.Count == 0 || option.ExitEvidence.Count == 0 ||
                option.BodyEvidence.Any(string.IsNullOrWhiteSpace) || option.ExitEvidence.Any(string.IsNullOrWhiteSpace) ||
                option.Requires.Count != 0 || option.Excludes.Count != 0 || option.MergeId != mergeId || option.ReturnId != mergeId ||
                string.IsNullOrWhiteSpace(option.Id) || string.IsNullOrWhiteSpace(option.PathId) ||
                !DirectVerifiedExit(pack, menu, option, mergeId)) return false;

            // 使用真实导航规则分别走两支；不向真实引擎提交选项，也不订阅克隆的播放事件。
            var route = new PlaybackEngine(pack);
            if (!route.ImportNavigation(atMenu.ExportNavigation())) return false;
            route.Commit(menuId);
            route.SelectBranch(route.AvailableOptions.FindIndex(o => o.Id == option.Id));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (route.Current is { Kind: "line" } line && route.Mode == RunMode.Following)
            {
                if (!seen.Add(line.Id) || line.PathId != option.PathId || !option.SegmentIds.Contains(line.Id)) return false;
                route.Next(true);
            }
            if (route.CurrentId != mergeId || route.Mode != RunMode.Merge) return false;
            route.Next(true);
            if (route.Current is not { Kind: "line" } common || common.Archived || common.SectionId != menu.SectionId ||
                common.PathId.Length != 0 || !route.EvaluateCommonAutoPlayNext().CurrentIsCommon ||
                candidate != null && candidate.Id != common.Id) return false;
            candidate = common;
        }
        reason = "";
        return candidate != null;
    }

    static bool DirectVerifiedExit(Pack pack, Node menu, ChoiceOption option, string mergeId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? id = option.TargetId;
        while (id != null && id != mergeId)
        {
            if (!seen.Add(id) || !option.SegmentIds.Contains(id) || !pack.ById.TryGetValue(id, out var node) || node.Archived ||
                node.SectionId != menu.SectionId || node.PathId != option.PathId || node.Kind is not ("line" or "return") ||
                node.Options.Count != 0 || node.SetFacts.Count != 0 ||
                !string.IsNullOrEmpty(node.CompleteRoute) && node.CompleteRoute != option.PathId) return false;
            id = node.NextId;
        }
        return id == mergeId && seen.Count > 0;
    }
}
