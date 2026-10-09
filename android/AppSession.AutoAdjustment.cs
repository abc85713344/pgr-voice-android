using Android.OS;

namespace PgrVoice.AndroidApp;

public sealed partial class AppSession
{
    // 上下句是玩家明确校准配音位置；取消旧完成回调后，沿用本次自动播放方式。
    bool TryAdjustAutoPlayback(Action<PlaybackEngine> action)
    {
        if (!autoPlay.Running || Engine is not { } engine) return false;
        if (autoPlay.Phase is not (AutoPlaybackPhase.Playing or AutoPlaybackPhase.SilentPause))
        {
            Status = "正在完成本次推进，请稍后再调整上下句。"; Notify(); return true;
        }
        if (!CheckAutoPlaybackEnvironment()) return true;
        bool defaultRoute = DefaultAutoLinePermitted(engine);
        var pendingAnchor = defaultPendingDisplayAnchor;
        var displayedBranch = defaultDisplayBranch;
        var probe = new PlaybackEngine(engine.Pack);
        if (!probe.ImportNavigation(engine.ExportNavigation()) || !probe.ConfirmCurrentPosition()) return false;
        action(probe);
        int merges = 0;
        while (probe.Mode == RunMode.Merge && merges < 8) { probe.Next(); merges++; }
        if (autoTarget is { } sourceTarget && autoDisplay.RegionKey is { } sourceKey &&
            TryWaitAtSourceChoicePreview(engine, probe, sourceTarget, sourceKey)) return true;
        if (probe.Mode == RunMode.Choice && AutomaticBranchRoutesEnabled && autoTarget is { } target && autoDisplay.RegionKey is { } key)
        {
            Invalidate(); audio.Stop(); action(engine);
            for (int i = 0; i < merges && engine.Mode == RunMode.Merge; i++) engine.Next();
            if (engine.CurrentId != probe.CurrentId || !(Settings.AutoPlayConfirmBranch
                ? BeginConfirmedBranchWait(engine, target, key) : BeginDefaultBranchWait(engine, target, key)))
            { Status = "这处分支暂不能使用默认路线，请按游戏画面核对。"; Notify(); }
            return true;
        }
        if (probe.Current is not { Kind: "line" } node || probe.Mode != RunMode.Following ||
            node.SectionId != engine.Current?.SectionId || !probe.Allowed(node) || probe.ReviewRoute != null ||
            probe.ExportNavigation().Current.SingleLine ||
            !CanStartConfirmedAutoRoute(probe, out _))
        {
            // 菜单、原声和未知连接仍按原有手动入口处理，不能借调整越过边界。
            return false;
        }
        if (!CanAutoPlayLine(engine.Pack, node))
        { PauseAutoPlayback("调整目标没有可用配音，已暂停，请核对当前位置。"); return true; }
        bool targetRoute = defaultRoute || AutomaticBranchRoutesEnabled && BranchRouteContext.TryResumeAuto(probe, out _);
        int adjustedOption = BranchRouteContext.Resolve(probe)?.Number ?? 0;
        Node? adjustedAnchor = targetRoute && DefaultBranchDisplayPolicy.TryGetDisplayAnchor(probe, adjustedOption, out var adjustedFirst, out var mappedAnchor)
            && ReferenceEquals(adjustedFirst,node) ? mappedAnchor : null;
        Invalidate(); audio.Stop();
        if (!PrepareAutoPlaybackEnvironment()) return true;
        if (targetRoute)
        {
            defaultAutoEngine = engine; defaultAutoNode = node.Id; defaultAutoVisited.Add(node.Id);
        }
        autoPlay.Start(node.Id, SystemClock.ElapsedRealtime());
        if (!autoPlay.PreparePlayback(autoPlay.Epoch, node.Id, SystemClock.ElapsedRealtime()))
        { PauseAutoPlayback("调整位置未能准备好，请核对当前句。"); return true; }
        if (pendingAnchor != null && !pendingAnchor.Invalidated && ReferenceEquals(pendingAnchor.First, node))
            defaultPendingDisplayAnchor = pendingAnchor with { AutoEpoch = autoPlay.Epoch };
        else if (adjustedAnchor != null)
            defaultPendingDisplayAnchor = new(engine,node,adjustedAnchor,adjustedOption,autoPlay.Epoch);
        if(displayedBranch!=null && ReferenceEquals(displayedBranch.Node,node))defaultDisplayBranch=displayedBranch;
        overlay.ClearAutoPlaybackPaused(); StartAutoPlaybackTimer();
        action(engine);
        for (int i = 0; i < merges && engine.Mode == RunMode.Merge; i++) engine.Next();
        if (engine.CurrentId != node.Id || engine.Mode != RunMode.Following)
        { PauseAutoPlayback("调整后的位置发生变化，请核对当前句。"); return true; }
        SectionId = node.SectionId;
        Diagnostics.Log("自动播放手动调整", node.Id + "；仅调整配音，未点击游戏");
        ContentChanged?.Invoke(); Notify(); return true;
    }
}
