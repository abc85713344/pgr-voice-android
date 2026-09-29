using Android.OS;
using PgrVoice.AndroidApp.Platform;
using PgrVoice.AndroidApp.Ui;
using PgrVoice.Following;

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
        var state = GameAdvanceAccessibilityService.GetAvailability(AutoGamePackage);
        if (uiVisible || !state.CanTap)
        { Status = uiVisible ? "回到游戏，从悬浮控制开启点按跟随。" : state.Reason; overlay.ShowNotice(Status); Notify(); return; }
        if (!overlay.CanShow || state.DisplayKey == null ||
            !Settings.AdvanceTargets.TryGetValue(state.DisplayKey, out var target) || !MatchesTarget(target, state))
        { Status = "先点“下一句区域”，框出游戏中用于推进对白的位置，再开启点按跟随。"; overlay.ShowNotice(Status); Notify(); return; }
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
        var region = clickRegion!;
        region.Suspend();
        // 先移除本次接收轻点的窗口，再经系统执行同坐标一次触碰；不循环补点。
        long token = clickServiceToken;
        var cancellation = clickCancellation!.Token;
        main.PostDelayed(() => Post(() =>
        {
            if (!clickRequests.IsCurrent(ticket) || !CheckClickFollowEnvironment()) return;
            _ = CompleteClickFollowAsync(engine, node, ticket, token, x, y, cancellation);
        }), 80);
    });

    async Task CompleteClickFollowAsync(PlaybackEngine engine, string node, TapFollowRequestTicket ticket,
        long token, float x, float y, CancellationToken cancellation)
    {
        try
        {
            var result = await GameAdvanceAccessibilityService.TapAsync(token, x, y, cancellation);
            Post(() =>
            {
                if (!clickRequests.IsCurrent(ticket) || !ClickFollowRunning) return;
                if (!CheckClickFollowEnvironment()) return;
                if (!ReferenceEquals(Engine, engine) || engine.CurrentId != node || engine.Mode != RunMode.Following)
                { StopClickFollow("配音位置已变化，请核对游戏后重新开启。", true); return; }
                if (!clickRequests.TryComplete(ticket, result.Completed, out bool advance)) return;
                if (!advance) { StopClickFollow(result.Reason + " 请核对当前位置，不会补点。", true); return; }
                // 这是用户明确轻点后的手动推进，沿用 PC 和小按钮的路线边界规则。
                engine.Next(true);
                Diagnostics.Log("点按跟随推进", $"{node} → {engine.CurrentId}");
                if (engine.Mode != RunMode.Following || engine.Current?.Kind != "line")
                {
                    StopClickFollow(engine.Mode == RunMode.Choice ? "已到分支，请在游戏和配音中选择相同选项。" :
                        engine.Mode == RunMode.End ? "本段已结束，点按跟随已停止。" : "已到段落边界，请手动核对续接位置。", true);
                    return;
                }
                clickRegion?.Resume(); Notify();
            });
        }
        catch (Exception ex) { Post(() => { if (clickRequests.IsCurrent(ticket)) StopClickFollow("点按未完成：" + ex.Message, true); }); }
    }

    void ManualOverlayStep(Action<PlaybackEngine> action)
    {
        bool resume = ClickFollowRunning && !clickRequests.IsInFlight;
        Command(action, !resume);
        if (resume && Engine is { Mode: RunMode.Following, Current.Kind: "line" }) StartClickFollow(preserveAudio: true);
    }
}
