using Android.OS;
using PgrVoice.AndroidApp.Platform;
using System.Text.Json;

namespace PgrVoice.AndroidApp;

public sealed partial class AppSession
{
    sealed record BranchBrowseSource(BranchResumeIntent Intent, string[] DefaultVisited);
    sealed record BranchBrowseContinuation(BranchBrowseSource Source, Node Menu, long Epoch, long AudioGeneration,
        string Navigation, ScreenDisplayState Display, long CaptureSession, bool CaptureActive)
    {
        public volatile bool Invalidated;
    }
    BranchBrowseContinuation? branchBrowseContinuation;
    (PlaybackEngine Engine, Node Menu, string Reason)? branchBrowseStopped;
    long branchBrowseServiceToken;
    Action? branchBrowseWatchdog;

    void CancelBranchBrowseContinuation()
    {
        var previous = branchBrowseContinuation; branchBrowseContinuation = null;
        branchBrowseStopped = null;
        if (previous != null) ResetOverlayBrowseRequests();
        if (previous != null) previous.Invalidated = true;
        if (branchBrowseWatchdog != null) main.RemoveCallbacks(branchBrowseWatchdog);
        branchBrowseWatchdog = null;
        bool hadSession = branchBrowseServiceToken != 0; branchBrowseServiceToken = 0;
        if (hadSession) GameAdvanceAccessibilityService.CancelSession("分支页续接已取消。");
    }

    BranchBrowseSource? CaptureBranchBrowseSource()
    {
        if (Engine is not { Current.Kind: "choice", Mode: RunMode.Choice } engine) return null;
        if (branchBrowseContinuation is { } browsing && BranchBrowseContinuationValid(browsing)) return browsing.Source;
        if (manualBranchConfirmation is { } manual && ManualBranchEnvironmentValid(manual))
            return new(new(engine, manual.Menu.Id, manual.GamePackage, manual.Target, manual.DisplayKey,
                SystemClock.ElapsedRealtime(), manual.ResumeAutoAfterCommon), Array.Empty<string>());
        if (branchFollow is { Invalidated: false } branch && CheckBranchFollow(branch))
            return new(branch.Intent, branch.PriorDefaultLines);
        if (ClickFollowRunning && !clickRequests.IsInFlight && clickTarget is { } target && clickDisplayKey is { } key &&
            CheckClickFollowEnvironment() && BranchFollowPolicy.IsEligibleManualMenu(engine, engine.CurrentId!, out _))
            return new(new(engine, engine.CurrentId!, AutoGamePackage, target, key, SystemClock.ElapsedRealtime(),
                BranchAutoReturnValid(engine)), Array.Empty<string>());
        return null;
    }

    bool BranchBrowseContinuationValid(BranchBrowseContinuation request)
    {
        var intent = request.Source.Intent;
        return !request.Invalidated && ReferenceEquals(branchBrowseContinuation, request) &&
            BranchIntentEnvironmentValid(intent) && ReferenceEquals(Engine?.Current, request.Menu) && Engine!.Mode == RunMode.Choice &&
            ocrEpoch == request.Epoch && playGeneration == request.AudioGeneration && SectionId == request.Menu.SectionId &&
            Screen.DisplayState == request.Display && Screen.SessionId == request.CaptureSession &&
            Screen.CaptureActive == request.CaptureActive &&
            JsonSerializer.Serialize(Engine.ExportNavigation(), Json.Options) == request.Navigation;
    }

    void BeginBranchBrowseContinuation(BranchBrowseSource? source)
    {
        if (source == null || !BranchIntentEnvironmentValid(source.Intent) ||
            Engine is not { Current.Kind: "choice", Mode: RunMode.Choice } engine || engine.CurrentId != source.Intent.MenuId) return;
        var request = new BranchBrowseContinuation(source, engine.Current!, ocrEpoch, playGeneration,
            JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options), Screen.DisplayState, Screen.SessionId, Screen.CaptureActive);
        branchBrowseContinuation = request;
        branchBrowseServiceToken = GameAdvanceAccessibilityService.BeginSession(source.Intent.GamePackage,
            source.Intent.Target.DisplayWidth, source.Intent.Target.DisplayHeight, source.Intent.Target.Rotation);
        if (branchBrowseServiceToken == 0) { CancelBranchBrowseContinuation(); Status = GameAdvanceAccessibilityService.LastFailureReason; return; }
        branchBrowseWatchdog = () =>
        {
            if (!ReferenceEquals(branchBrowseContinuation, request)) return;
            if (!BranchBrowseContinuationValid(request))
            {
                CancelBranchBrowseContinuation();
                Status = "分支续接等待已过期，或游戏、屏幕、位置已变化。请重新核对后开启跟随。";
                branchBrowseStopped = (request.Source.Intent.Engine, request.Menu, Status);
                Notify(); overlay.ShowNotice(Status); return;
            }
            if (branchBrowseWatchdog != null) main.PostDelayed(branchBrowseWatchdog, 400);
        };
        main.PostDelayed(branchBrowseWatchdog, 400);
        Status = source.Intent.DefaultOption > 0 ? "请先在游戏中选择默认分支，再确认同项；确认后继续自动播放。" :
            "已到分支，请先在游戏中选择，再确认并开启点按跟随。";
    }

    bool TryResumeBranchFromBrowse(PlaybackEngine engine, ChoiceOption option)
    {
        if (branchBrowseContinuation is not { } request) return false;
        bool current = BranchBrowseContinuationValid(request) && ReferenceEquals(Engine, engine) &&
            engine.AvailableOptions.Any(o => ReferenceEquals(o, option));
        // 先消费本次权限。重复确认、旧页和异步失焦事件不能启动第二次跟随。
        var source = request.Source;
        CancelBranchBrowseContinuation();
        if (!current) { Status = "本次分支续接已失效，请按游戏画面重新确认后开启跟随。"; Notify(); return true; }
        if (source.Intent.DefaultOption == 0)
        {
            if (engine.Pack.ById.TryGetValue(option.TargetId, out var firstLine) && firstLine.Kind == "line" && !CanAutoPlayLine(engine.Pack, firstLine))
            { Status = "这条分支首句没有可用配音，尚未开启点按跟随。请按游戏当前句手动核对。"; Notify(); return true; }
            if (!BeginManualBranchFollow(engine, source.Intent.Target, source.Intent.DisplayKey,
                source.Intent.ResumeAutoAfterCommon, confirmedOptionId: option.Id))
            { Status = "此分支暂不能接回点按跟随，请按游戏当前句定位。"; Notify(); }
            return true;
        }
        if (!DefaultBranchPolicy.TryChoice(engine, source.Intent.DefaultOption, out var expected, out var first) ||
            expected?.Id != option.Id || first == null || !CanAutoPlayLine(engine.Pack, first))
        {
            // 人工改选仍允许播放，但不能暗中把另一支当作默认路线继续自动点击。
            Command(e => e.SelectBranch(e.AvailableOptions.FindIndex(o => ReferenceEquals(o, option))));
            Status = "已按你的确认选支；这不是当前可自动续播的默认路线，请核对后手动开启点按跟随。"; Notify();
            return true;
        }
        Node? displayAnchor = null;
        if (DefaultBranchDisplayPolicy.TryGetDisplayAnchor(engine, source.Intent.DefaultOption, out var displayFirst, out var mappedAnchor))
        {
            if (displayFirst != first || mappedAnchor == null || !CanAutoPlayLine(engine.Pack, mappedAnchor))
            { Status = "这条分支的游戏首句没有可用配音，请手动核对当前台词。"; Notify(); return true; }
            displayAnchor = mappedAnchor;
        }
        Invalidate(); audio.Stop();
        StartConfirmedDefaultBranch(engine, source.Intent, option, first, displayAnchor, source.DefaultVisited);
        return true;
    }

    string DefaultBranchBrowseDisplayDescription(PlaybackEngine engine, ChoiceOption option)
    {
        if (branchBrowseContinuation is not { } request || !BranchBrowseContinuationValid(request) ||
            request.Source.Intent.DefaultOption == 0 ||
            !DefaultBranchPolicy.TryChoice(engine, request.Source.Intent.DefaultOption, out var expected, out _) || expected?.Id != option.Id ||
            !DefaultBranchDisplayPolicy.TryGetDisplayAnchor(engine, request.Source.Intent.DefaultOption, out _, out var anchor) || anchor == null) return "";
        return "游戏当前应显示：" + anchor.Speaker + "：" + anchor.Text +
            "\n请核对这句后确认。软件会先播放所选选项，再播放当前台词，中间不会点击游戏。";
    }
}
