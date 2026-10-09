using Android.OS;
using PgrVoice.AndroidApp.Platform;
using System.Text.Json;

namespace PgrVoice.AndroidApp;

public sealed partial class AppSession
{
    sealed record BranchCommonWait(PlaybackEngine Engine, Node Source, string MenuId, Node Candidate,
        long Epoch, long AudioGeneration, string Navigation, string GamePackage, AdvanceTapTarget Target,
        string DisplayKey, ScreenDisplayState Display, long CaptureSession, bool CaptureActive, bool ResumeAuto);
    BranchCommonWait? branchCommonWait;
    Action? branchCommonWatchdog;
    long branchCommonServiceToken;

    void CancelBranchCommonWait()
    {
        bool hadWait = branchCommonWait != null;
        branchCommonWait = null;
        bool hadSession = branchCommonServiceToken != 0;
        branchCommonServiceToken = 0;
        if (branchCommonWatchdog != null) main.RemoveCallbacks(branchCommonWatchdog);
        branchCommonWatchdog = null;
        if (hadSession) GameAdvanceAccessibilityService.CancelSession("共同线等待已取消。");
        if (hadWait) overlay.ClearManualBranchSelection();
    }

    bool BranchCommonWaitEnvironmentValid(BranchCommonWait wait)
    {
        if (Settings.BranchAutoFollow || uiVisible || !overlay.CanShow || !ReferenceEquals(Engine, wait.Engine) ||
            !ReferenceEquals(wait.Engine.Current, wait.Source) || ocrEpoch != wait.Epoch || playGeneration != wait.AudioGeneration ||
            JsonSerializer.Serialize(wait.Engine.ExportNavigation(), Json.Options) != wait.Navigation ||
            AutoGamePackage != wait.GamePackage || Settings.AdvanceTargetFor(wait.DisplayKey) != wait.Target ||
            Screen.DisplayState != wait.Display || Screen.SessionId != wait.CaptureSession || Screen.CaptureActive != wait.CaptureActive) return false;
        var state = GameAdvanceAccessibilityService.GetAvailability(wait.GamePackage);
        return state.CanTap && state.DisplayKey == wait.DisplayKey && MatchesTarget(wait.Target, state);
    }

    bool TryWaitForCommonAfterBranch(PlaybackEngine engine, string menuId, AdvanceTapTarget target, string displayKey, bool resumeAuto)
    {
        if (Settings.BranchAutoFollow || !BranchCommonResumePolicy.TryGetCommonLine(engine, menuId, out var candidate, out _) ||
            candidate == null || engine.Current is not { } source) return false;
        // 真实位置保留在原句/菜单，克隆得到的候选只用于提示；取消所有推进后才建立等待票据。
        Invalidate(); audio.Stop(); engine.PauseForBrowse();
        var wait = new BranchCommonWait(engine, source, menuId, candidate, ocrEpoch, playGeneration,
            JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options), AutoGamePackage, target, displayKey,
            Screen.DisplayState, Screen.SessionId, Screen.CaptureActive, resumeAuto);
        if (!BranchCommonWaitEnvironmentValid(wait))
        { Status = "游戏、屏幕或区域已变化，分支期间已暂停，请手动定位共同线。"; Notify(); return true; }
        // 等待可以持续整个分支，不设 OCR/音频超时；此 token 只监听切出，不发触碰。
        branchCommonServiceToken = GameAdvanceAccessibilityService.BeginSession(wait.GamePackage,
            target.DisplayWidth, target.DisplayHeight, target.Rotation);
        if (branchCommonServiceToken == 0)
        { Status = GameAdvanceAccessibilityService.LastFailureReason; Notify(); return true; }
        branchCommonWait = wait;
        overlay.SetCaptureHidden(false); overlay.SetExpanded(false);
        Status = "分支期间已暂停配音和跟随。你可以自行继续游戏，等共同线台词出现后再确认。"; Notify();
        branchCommonWatchdog = () =>
        {
            if (!ReferenceEquals(branchCommonWait, wait)) return;
            if (!BranchCommonWaitEnvironmentValid(wait))
            { CancelBranchCommonWait(); Status = "游戏、屏幕或当前位置已变化，共同线等待已取消。"; Notify(); return; }
            if (branchCommonWatchdog != null) main.PostDelayed(branchCommonWatchdog, 400);
        };
        main.PostDelayed(branchCommonWatchdog, 400);
        string mode = resumeAuto ? "自动播放" : "点按跟随";
        overlay.ShowManualBranchSelection(BrowseSectionTitle(engine, candidate.SectionId),
            new[] { BrowseSpeaker(candidate) + "：" + candidate.Text + "\n确认后恢复" + mode },
            _ => main.PostDelayed(() => Post(() => ConfirmBranchCommonWait(wait)), 180),
            () => Post(() =>
            {
                if (!ReferenceEquals(branchCommonWait, wait)) return;
                CancelBranchCommonWait(); Status = "已关闭共同线提示，配音保持暂停。"; Notify();
            }), title: "分支期间暂停配音", instruction: "等游戏出现下面这句，再确认继续。", buttonPrefix: "游戏已显示这句：");
        Diagnostics.Log("分支暂停等待共同线", menuId + " → " + candidate.Id + "；恢复" + mode);
        return true;
    }

    void ConfirmBranchCommonWait(BranchCommonWait wait)
    {
        if (!ReferenceEquals(branchCommonWait, wait)) return;
        CancelBranchCommonWait();
        if (!BranchCommonWaitEnvironmentValid(wait) ||
            !BranchCommonResumePolicy.TryGetCommonLine(wait.Engine, wait.MenuId, out var candidate, out _) ||
            !ReferenceEquals(candidate, wait.Candidate))
        { Status = "确认期间位置或路线已变化，请手动核对游戏当前句。"; Notify(); return; }
        if (!CanAutoPlayLine(wait.Engine.Pack, wait.Candidate))
        { Status = "这句没有可用配音，仍保持暂停，请手动定位有配音的共同句。"; Notify(); return; }
        Invalidate(); audio.Stop();
        if (wait.ResumeAuto)
        {
            if (!PrepareAutoPlaybackEnvironment()) return;
            autoPlay.Start(wait.Candidate.Id, SystemClock.ElapsedRealtime());
            if (!autoPlay.PreparePlayback(autoPlay.Epoch, wait.Candidate.Id, SystemClock.ElapsedRealtime()))
            { PauseAutoPlayback("本次恢复已失效，请重新核对当前句。"); return; }
            StartAutoPlaybackTimer();
        }
        // 玩家明确确认当前句后只定位播放一次；不实际经历任何一条被跳过的分支。
        if (!wait.Engine.ConfirmGameLine(wait.Candidate.Id))
        { if (wait.ResumeAuto) PauseAutoPlayback(wait.Engine.NavigationError); else { Status = wait.Engine.NavigationError; Notify(); } return; }
        SectionId = wait.Candidate.SectionId;
        if (!wait.ResumeAuto) StartClickFollow(preserveAudio: true);
        overlay.SetExpanded(false); overlay.SetCaptureHidden(false);
        Diagnostics.Log("共同线人工恢复", wait.Candidate.Id + (wait.ResumeAuto ? "；自动播放" : "；点按跟随"));
        ContentChanged?.Invoke(); Notify();
    }
}
