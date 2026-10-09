namespace PgrVoice.AndroidApp;

public sealed partial class AppSession
{
    enum FollowResumeMode { AutoPlayback, ClickFollow }

    bool FollowConfirmationEnabled(FollowResumeMode mode) => mode == FollowResumeMode.ClickFollow
        ? Settings.BranchAutoFollow : Settings.AutoPlayConfirmBranch;

    bool CanResumeConfirmedRoute(PlaybackEngine engine, FollowResumeMode mode, out string reason)
    {
        if (mode == FollowResumeMode.AutoPlayback) return CanStartConfirmedAutoRoute(engine, out reason);
        reason = "当前路线的正文或选择尚不能确认，请按游戏画面重新核对台词。";
        if (!VerifiedClickRoute(engine)) return false;
        reason = ""; return true;
    }

    static bool VerifiedClickRoute(PlaybackEngine engine) => engine.Mode == RunMode.Following &&
        engine.Current is { Kind: "line", Archived: false } line && line.Options.Count == 0 &&
        line.SetFacts.Count == 0 && string.IsNullOrEmpty(line.CompleteRoute) && engine.Allowed(line) &&
        engine.ReviewRoute == null && !engine.ExportNavigation().Current.SingleLine &&
        (engine.EvaluateCommonAutoPlayNext().CurrentIsCommon || BranchRouteContext.IsVerifiedCommon(engine) ||
         BranchRouteContext.TryResumeAuto(engine, out _));

    void ShowFollowDialogueCards(FollowResumeMode mode, string section, string[] labels, Action<int> selected, Action cancelled)
    {
        if (mode == FollowResumeMode.AutoPlayback)
            overlay.ShowConfirmedBranchSelection(section, labels, selected, cancelled);
        else
            overlay.ShowManualBranchSelection(section, labels, selected, cancelled,
                title: "请核对选后的当前台词", instruction: "先在游戏中选好，再点对应台词；确认后播放这句并恢复点按跟随。",
                anchorCards: true, choiceConfirmationLabel: "确认并开启点按跟随", dialogueCards: true);
    }

    bool PrepareConfirmedPlayback(FollowResumeMode mode, PlaybackEngine engine, Node node, NavigationSnapshot? selection = null)
    {
        if (mode == FollowResumeMode.AutoPlayback && !PrepareAutoPlaybackEnvironment()) return false;
        // 与原自动流程相同：环境取得许可后才静音提交这次明确选择。
        if (selection != null && !engine.ImportNavigation(selection))
        { PauseAutoPlayback(engine.NavigationError); return false; }
        if (mode == FollowResumeMode.ClickFollow) return true;
        defaultAutoEngine = engine; defaultAutoNode = node.Id; defaultAutoVisited.Clear(); defaultAutoVisited.Add(node.Id);
        autoPlay.Start(node.Id, Android.OS.SystemClock.ElapsedRealtime());
        if (!autoPlay.PreparePlayback(autoPlay.Epoch, node.Id, Android.OS.SystemClock.ElapsedRealtime()))
        { PauseAutoPlayback("台词续播请求已变化，请重新核对。"); return false; }
        overlay.SetCaptureHidden(false); overlay.SetExpanded(false); StartAutoPlaybackTimer();
        return true;
    }

    void FinishConfirmedPlayback(FollowResumeMode mode)
    {
        if (mode == FollowResumeMode.ClickFollow) StartClickFollow(preserveAudio: true);
    }
}
