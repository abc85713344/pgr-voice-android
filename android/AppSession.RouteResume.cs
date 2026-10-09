namespace PgrVoice.AndroidApp;

public sealed partial class AppSession
{
    // 开始/重新定位共用路线准入；运行期的默认分支令牌仍由每次明确确认重新建立。
    bool CanStartConfirmedAutoRoute(PlaybackEngine engine, out string reason)
    {
        if (!AutomaticBranchRoutesEnabled && DefaultBranchDisplayPolicy.TryGetVisibleBranch(engine, out _))
        {
            reason = "当前仍在分支剧情。开启分支页的默认分支自动播放后，可核对当前句继续；也可使用点按跟随。";
            return false;
        }
        var common = engine.EvaluateCommonAutoPlayNext();
        if (common.CurrentIsCommon) { reason = ""; return true; }
        if (engine.Mode == RunMode.Following && engine.Current is { Kind: "line", Archived: false } line &&
            engine.Allowed(line) && engine.ReviewRoute == null && !engine.ExportNavigation().Current.SingleLine &&
            BranchRouteContext.IsVerifiedCommon(engine))
        { reason = ""; return true; }
        if (AutomaticBranchRoutesEnabled && BranchRouteContext.TryResumeAuto(engine, out _))
        { reason = ""; return true; }
        reason = common.Code == "branch"
            ? AutomaticBranchRoutesEnabled
                ? "当前分支的正文、选择或条件尚不能确认，请按游戏画面重新核对台词。"
                : "当前仍在分支剧情。开启分支页的默认分支自动播放后，可核对当前句继续；也可使用点按跟随。"
            : common.Reason;
        return false;
    }

    bool BindConfirmedAutoRoute(PlaybackEngine validated, PlaybackEngine owner, Node node)
    {
        if (!CanStartConfirmedAutoRoute(validated, out var reason))
        { PauseAutoPlayback(reason); return false; }
        if (!AutomaticBranchRoutesEnabled || !BranchRouteContext.TryResumeAuto(validated, out var route) || route == null)
            return true;
        defaultAutoEngine = owner; defaultAutoNode = node.Id;
        defaultAutoVisited.Clear(); defaultAutoVisited.Add(node.Id);
        // 当前选择可能不同于未来菜单的默认偏好；只恢复玩家已经确认的这一条路线。
        if (DefaultBranchDisplayPolicy.TryGetDisplayAnchor(validated, route.Number, out var first, out var anchor) &&
            ReferenceEquals(first, node) && anchor != null)
        {
            if (!CanAutoPlayLine(owner.Pack, anchor))
            { PauseAutoPlayback("选项之后的游戏台词缺少配音，已暂停，尚未点击游戏。"); return false; }
            defaultPendingDisplayAnchor = new(owner, node, anchor, route.Number, autoPlay.Epoch);
        }
        return true;
    }
}
