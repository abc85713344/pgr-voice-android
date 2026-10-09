using Android.OS;
using PgrVoice.AndroidApp.Platform;
using PgrVoice.AndroidApp.Ui;
using PgrVoice.Following;
using System.Text.Json;

namespace PgrVoice.AndroidApp;

public sealed partial class AppSession
{
    readonly TapFollowRequestGate clickRequests = new();
    TapFollowOverlay? clickRegion;
    AdvanceTapTarget? clickTarget;
    string? clickDisplayKey;
    long clickServiceToken;
    CancellationTokenSource? clickCancellation;
    Action? clickWatchdog;
    public bool ClickFollowRunning => clickServiceToken != 0 && clickRegion != null;

    void CancelClickFollow()
    {
        ClearBranchAutoReturn();
        bool hadSession = clickServiceToken != 0;
        clickServiceToken = 0;
        clickRequests.Invalidate();
        if (clickWatchdog != null) { main.RemoveCallbacks(clickWatchdog); clickWatchdog = null; }
        var region = clickRegion; clickRegion = null; region?.Dispose();
        clickTarget = null; clickDisplayKey = null;
        var cancellation = clickCancellation; clickCancellation = null;
        cancellation?.Cancel(); cancellation?.Dispose();
        if (hadSession) GameAdvanceAccessibilityService.CancelSession("点按跟随已停止。");
    }

    public void StopClickFollow(string reason = "点按跟随已停止；小按钮仍可手动播放。", bool showNotice = false)
    {
        CancelSourceChoiceWait(); CancelConfirmedBranchWait();
        CancelBranchFollow();
        CancelClickFollow();
        audio.Stop(); Engine?.PauseForBrowse(); Status = reason; Notify();
        if (showNotice) overlay.ShowNotice(reason);
    }

    void StartClickFollow(bool preserveAudio = false)
    {
        if (Listening.IsPlaying) Listening.Pause();
        if (!preserveAudio) Invalidate();
        if (!preserveAudio) audio.Stop();
        if (Engine is not { Current.Kind: "line" } engine || !engine.ConfirmCurrentPosition())
        { Status = "先用台词列表或小按钮对齐游戏当前句，再开启点按跟随。"; Notify(); return; }
        var state = GameAdvanceAccessibilityService.GetForegroundAvailability();
        if (uiVisible || !state.CanTap)
        { Status = uiVisible ? "回到游戏，从悬浮控制开启点按跟随。" : state.Reason; overlay.ShowNotice(Status); Notify(); return; }
        AutoGamePackage = state.ForegroundPackage ?? "";
        if (!overlay.CanShow || state.DisplayKey == null ||
            Settings.AdvanceTargetFor(state.DisplayKey) is not { } target || !MatchesTarget(target, state))
        { Status = "先点“下一句区域”，框出游戏中用于推进对白的位置，再开启点按跟随。"; overlay.ShowNotice(Status); Notify(); return; }
        if (Settings.BranchAutoFollow && SourceChoicePolicy.TryAt(engine, out var currentOption) && currentOption != null &&
            BeginSourceChoiceWait(engine, currentOption, target, state.DisplayKey, FollowResumeMode.ClickFollow)) return;
        long token = GameAdvanceAccessibilityService.BeginSession(AutoGamePackage, state.DisplayWidth, state.DisplayHeight, state.Rotation);
        if (token == 0) { Status = GameAdvanceAccessibilityService.LastFailureReason; Notify(); return; }
        ocrEpoch++; manualOcr = false; pendingFrame.Clear(); ocrSchedule.Reset();
        clickServiceToken = token; clickTarget = target; clickDisplayKey = state.DisplayKey;
        clickCancellation = new();
        clickRegion = new(context, state.DisplayWidth, state.DisplayHeight,
            new ScreenRegion(target.Left, target.Top, target.Width, target.Height), OnClickFollowTap);
        try { clickRegion.Show(); }
        catch { CancelClickFollow(); throw; }
        overlay.SetExpanded(false);
        Status = "点按跟随：等字幕完整后轻点一次，再等下一句；不要快速连点。";
        Notify();
        clickWatchdog = () =>
        {
            if (!ClickFollowRunning || clickServiceToken != token) return;
            if (!CheckClickFollowEnvironment()) return;
            if (clickWatchdog != null) main.PostDelayed(clickWatchdog, 400);
        };
        main.PostDelayed(clickWatchdog, 400);
        Diagnostics.Log("点按跟随开启", engine.CurrentId ?? "");
    }

    bool CheckClickFollowEnvironment()
    {
        if (!ClickFollowRunning) return false;
        var state = GameAdvanceAccessibilityService.GetAvailability(AutoGamePackage);
        if (uiVisible || !state.CanTap || clickTarget == null || state.DisplayKey != clickDisplayKey || !MatchesTarget(clickTarget, state))
        {
            StopClickFollow(uiVisible ? "离开游戏后点按跟随已停止。" : !state.CanTap ? state.Reason : "屏幕已变化，请重新设置点按区域。", false);
            return false;
        }
        return true;
    }

    void OnClickFollowTap(float x, float y) => Post(() =>
    {
        if (!CheckClickFollowEnvironment() || Engine is not { Current.Kind: "line", Mode: RunMode.Following } engine)
        { if (ClickFollowRunning) StopClickFollow("当前是菜单或未确认段落，请手动选择后再开启点按跟随。", true); return; }
        if (overlay.ContainsPoint(x, y)) { StopClickFollow("点按区域被配音面板挡住，请挪开面板。", true); return; }
        var target = clickTarget!;
        if (x < target.Left * target.DisplayWidth || y < target.Top * target.DisplayHeight ||
            x >= (target.Left + target.Width) * target.DisplayWidth || y >= (target.Top + target.Height) * target.DisplayHeight) return;
        if (!clickRequests.TryBegin(SystemClock.ElapsedRealtime(), out var ticket)) return;
        string node = engine.Current!.Id;
        var plan = CaptureClickAdvance(engine);
        var region = clickRegion!;
        region.Suspend();
        // 先移除本次接收轻点的窗口，再经系统执行同坐标一次触碰；不循环补点。
        long token = clickServiceToken;
        var cancellation = clickCancellation!.Token;
        main.PostDelayed(() => Post(() =>
        {
            if (!clickRequests.IsCurrent(ticket) || !CheckClickFollowEnvironment()) return;
            _ = CompleteClickFollowAsync(engine, node, plan, ticket, token, x, y, cancellation);
        }), 80);
    });

    async Task CompleteClickFollowAsync(PlaybackEngine engine, string node, ClickAdvancePlan plan, TapFollowRequestTicket ticket,
        long token, float x, float y, CancellationToken cancellation)
    {
        try
        {
            var result = await GameAdvanceAccessibilityService.TapAsync(token, x, y, cancellation);
            Post(() =>
            {
                if (!clickRequests.IsCurrent(ticket) || !ClickFollowRunning) return;
                if (!CheckClickFollowEnvironment()) return;
                if (!ReferenceEquals(Engine, engine) || engine.CurrentId != node || engine.Mode != RunMode.Following ||
                    !ClickAdvanceUnchanged(engine, plan))
                { StopClickFollow("配音位置已变化，请核对游戏后重新开启。", true); return; }
                if (!clickRequests.TryComplete(ticket, result.Completed, out bool advance)) return;
                if (!advance) { StopClickFollow(result.Reason + " 请核对当前位置，不会补点。", true); return; }
                // 游戏只接收刚才用户的一次触碰。已核选项等待时，真实导航仍停在前句。
                if (plan.SourceChoice != null && clickTarget is { } sourceTarget && clickDisplayKey is { } sourceKey)
                {
                    if (!SourceChoicePolicy.TryResolveOffer(engine, plan.SourceChoice, out var offer, out var reason) || offer == null)
                    { StopClickAtBoundary(reason); return; }
                    BeginSourceChoiceWait(engine, offer, sourceTarget, sourceKey, FollowResumeMode.ClickFollow); return;
                }
                // 克隆已证这些 Next 只经过无对白 return/merge；不得再给游戏补点，也不自动选择。
                for (int i = 0; i < plan.Steps; i++) engine.Next(true);
                Diagnostics.Log("点按跟随推进", $"{node} → {engine.CurrentId}");
                if (TryOfferCommonAutoResume(engine)) return;
                if (engine.Mode != RunMode.Following || engine.Current?.Kind != "line")
                {
                    if (!Settings.BranchAutoFollow && engine.Mode == RunMode.Choice && engine.CurrentId is { } menuId &&
                        clickTarget is { } branchTarget && clickDisplayKey is { } branchKey &&
                        TryWaitForCommonAfterBranch(engine, menuId, branchTarget, branchKey, false)) return;
                    if (Settings.BranchAutoFollow && !BranchAutoReturnValid(engine) && engine.Mode == RunMode.Choice &&
                        clickTarget is { } confirmTarget && clickDisplayKey is { } confirmKey &&
                        ConfirmedBranchPolicy.Create(engine).Cards.Count > 0 &&
                        BeginConfirmedBranchWait(engine, confirmTarget, confirmKey, FollowResumeMode.ClickFollow)) return;
                    if (engine.Mode == RunMode.Choice && TryBeginBranchFollow(engine)) return;
                    StopClickAtBoundary(engine.Mode == RunMode.Choice ? Settings.BranchAutoFollow ? "已到分支，请在游戏和配音中选择相同选项。" :
                        "分支期间配音已暂停；此处没有可靠的共同续接句，请自行继续游戏，之后手动定位恢复。" :
                        engine.Mode == RunMode.End ? "本段已结束，点按跟随已停止。" : "已到段落边界，请手动核对续接位置。");
                    return;
                }
                clickRegion?.Resume(); Notify();
            });
        }
        catch (Exception ex) { Post(() => { if (clickRequests.IsCurrent(ticket)) StopClickFollow("点按未完成：" + ex.Message, true); }); }
    }

    sealed record ClickAdvancePlan(string Navigation, string Graph, string? TargetId, int Steps, SourceChoiceOffer? SourceChoice);

    static string ClickNavigation(PlaybackEngine engine) => JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options);
    static string ClickGraph(PlaybackEngine engine) => JsonSerializer.Serialize(
        engine.Pack.Nodes.Where(n => n.SectionId == engine.Current!.SectionId).OrderBy(n => n.Id, StringComparer.Ordinal), Json.Options);

    ClickAdvancePlan CaptureClickAdvance(PlaybackEngine engine)
    {
        SourceChoiceOffer? source = null;
        if (Settings.BranchAutoFollow && !BranchAutoReturnValid(engine)) SourceChoicePolicy.TryNext(engine, out source, out _);
        Node? next = null; int steps = 1;
        // 旧 auto 临时转点按时保留它已明确建立的回 auto 确认，不把普通点按建立成自动播放。
        if (!BranchAutoReturnValid(engine) && TryVerifiedClickNext(engine, out var candidate, out int provenSteps))
        { next = candidate; steps = provenSteps; }
        return new(ClickNavigation(engine), ClickGraph(engine), next?.Id, steps, source);
    }

    bool ClickAdvanceUnchanged(PlaybackEngine engine, ClickAdvancePlan plan)
    {
        if (ClickNavigation(engine) != plan.Navigation || ClickGraph(engine) != plan.Graph) return false;
        if (plan.SourceChoice != null && !Settings.BranchAutoFollow) return false;
        return plan.TargetId == null || TryVerifiedClickNext(engine, out var next, out int steps) &&
            next?.Id == plan.TargetId && steps == plan.Steps;
    }

    static bool TryVerifiedClickNext(PlaybackEngine engine, out Node? next, out int steps)
    {
        next = null; steps = 0;
        if (!VerifiedClickRoute(engine) || !DefaultBranchPolicy.TryNext(engine, out var candidate, out int count, includeChoice: true) || candidate == null)
            return false;
        var seen = new HashSet<string>(); string? id = engine.Current!.NextId;
        while (id != candidate.Id)
        {
            if (id == null || !seen.Add(id) || !engine.Pack.ById.TryGetValue(id, out var control) ||
                control.Kind is not ("return" or "merge") || control.SetFacts.Count != 0 ||
                !string.IsNullOrEmpty(control.CompleteRoute) && (control.Kind != "return" || control.CompleteRoute != control.PathId)) return false;
            id = control.NextId;
        }
        var probe = new PlaybackEngine(engine.Pack);
        if (!probe.ImportNavigation(engine.ExportNavigation()) || !probe.ConfirmCurrentPosition()) return false;
        for (int i = 0; i < count; i++) probe.Next();
        if (candidate.Kind == "line" ? !VerifiedClickRoute(probe) : ConfirmedBranchPolicy.Create(probe).Cards.Count == 0) return false;
        next = candidate; steps = count; return true;
    }

    void StopClickAtBoundary(string reason)
    {
        bool ended = Engine?.Mode == RunMode.End;
        StopClickFollow(reason);
        if (ended) overlay.ShowNotice(reason);
        else overlay.ShowBrowsePage(OverlayBrowsePage.Lines);
    }

    void ManualOverlayStep(Action<PlaybackEngine> action)
    {
        if (TryAdjustAutoPlayback(action)) return;
        bool resume = ClickFollowRunning && !clickRequests.IsInFlight;
        Command(action, !resume);
        if (resume && Engine is { Mode: RunMode.Following, Current.Kind: "line" }) StartClickFollow(preserveAudio: true);
    }
}
