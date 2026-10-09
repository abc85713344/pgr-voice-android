using Android.OS;
using PgrVoice.AndroidApp.Platform;
using System.Text.Json;

namespace PgrVoice.AndroidApp;

public sealed partial class AppSession
{
    bool AutomaticBranchRoutesEnabled => Settings.AutoPlayConfirmBranch || Settings.AutoPlayDefaultBranchEnabled;
    bool DirectDefaultBranchEnabled => Settings.AutoPlayDefaultBranchEnabled && !Settings.AutoPlayConfirmBranch;

    public void SetAutoPlayConfirmBranch(bool enabled)
    {
        if (Settings.AutoPlayConfirmBranch == enabled) return;
        Invalidate(); audio.Stop(); Engine?.PauseForBrowse();
        Settings.AutoPlayConfirmBranch = enabled; SaveSettings();
        Status = enabled ? "已切换为选择后确认对白；确认一次后继续自动播放。" : "已切回原分支处理设置。";
        ContentChanged?.Invoke(); Notify();
    }
    PlaybackEngine? defaultAutoEngine;
    string? defaultAutoNode;
    readonly HashSet<string> defaultAutoVisited = new(StringComparer.Ordinal);
    sealed record DefaultPendingDisplayAnchor(PlaybackEngine Engine, Node First, Node Anchor, int Option, long AutoEpoch)
    {
        public volatile bool Invalidated;
    }
    DefaultPendingDisplayAnchor? defaultPendingDisplayAnchor;
    sealed record DefaultDisplayBranch(PlaybackEngine Engine, Node Node, string PathId, int Number);
    DefaultDisplayBranch? defaultDisplayBranch;

    public void SetAutoPlayDefaultBranchEnabled(bool enabled)
    {
        if (Settings.AutoPlayDefaultBranchEnabled == enabled && (!enabled || !Settings.AutoPlayConfirmBranch)) return;
        Invalidate(); audio.Stop(); Engine?.PauseForBrowse();
        if (enabled) Settings.AutoPlayConfirmBranch = false;
        Settings.AutoPlayDefaultBranchEnabled = enabled; SaveSettings();
        Status = enabled ? "已开启默认分支；自动播放时软件直接按默认路线配音，请在游戏选择相同选项。" : "默认分支自动续播已关闭，原分支设置保持不变。";
        ContentChanged?.Invoke(); Notify();
    }

    public void SetDefaultBranchOption(int number)
    {
        if (number is not (1 or 2) || Settings.DefaultBranchOption == number) return;
        Invalidate(); audio.Stop(); Engine?.PauseForBrowse();
        Settings.DefaultBranchOption = number; SaveSettings();
        Status = "默认路线已改为" + DefaultBranchName(number) + "；重新开始自动播放后生效。";
        ContentChanged?.Invoke(); Notify();
    }

    static string DefaultBranchName(int number) => number == 1 ? "分支一" : "分支二";
    void ClearDefaultAutoPlayback() { defaultAutoEngine = null; defaultAutoNode = null; defaultAutoVisited.Clear(); defaultPendingDisplayAnchor = null; defaultDisplayBranch = null; }
    bool DefaultAutoLinePermitted(PlaybackEngine engine) => AutomaticBranchRoutesEnabled &&
        ReferenceEquals(defaultAutoEngine, engine) && engine.CurrentId == defaultAutoNode &&
        engine.Current is { Kind: "line", Archived: false } line && engine.Mode == RunMode.Following &&
        engine.ReviewRoute == null && !engine.ExportNavigation().Current.SingleLine && engine.Allowed(line);
    bool VerifiedCommonLinePermitted(PlaybackEngine engine) => engine.Current is { Kind: "line", Archived: false } line &&
        engine.Mode == RunMode.Following && engine.ReviewRoute == null &&
        !engine.ExportNavigation().Current.SingleLine && engine.Allowed(line) && BranchRouteContext.IsVerifiedCommon(engine);
    bool AutoLinePermitted(PlaybackEngine engine) => engine.EvaluateCommonAutoPlayNext().CurrentIsCommon ||
        VerifiedCommonLinePermitted(engine) || DefaultAutoLinePermitted(engine);

    CommonAutoPlayDecision EvaluateAutoNext(PlaybackEngine engine)
    {
        bool defaultRoute = DefaultAutoLinePermitted(engine);
        var common = engine.EvaluateCommonAutoPlayNext();
        if (!defaultRoute && (common.CurrentIsCommon || !VerifiedCommonLinePermitted(engine))) return common;
        if (DefaultBranchPolicy.TryNext(engine, out var next, out _) && next != null &&
            (!defaultRoute ? BranchRouteContext.IsVerifiedCommon(engine, next) : !defaultAutoVisited.Contains(next.Id)))
            return new(true, true, next, "default-next", "沿当前已确认的路线继续自动播放。");
        string kind = DefaultBranchPolicy.IsVerifiedEnd(engine) ||
            VerifiedCommonLinePermitted(engine) && engine.Current?.NextId == null ? "end" :
            engine.Current?.NextId is { } id && engine.Pack.ById.TryGetValue(id, out var boundary) && boundary.Kind == "choice" ? "choice" : "unknown";
        return new(true, false, null, kind, kind == "choice" ? "请先在游戏中选择，再确认对应对白。" :
            kind == "end" ? "本段已播放完毕。" : "这条路线的后续连接尚不能自动续播，请按游戏画面手动核对。");
    }

    bool AdvanceAutoLine(PlaybackEngine engine, string nextId)
    {
        bool defaultRoute = DefaultAutoLinePermitted(engine);
        if (!defaultRoute && (engine.EvaluateCommonAutoPlayNext().CurrentIsCommon || !VerifiedCommonLinePermitted(engine)))
            return engine.TryAdvanceAutomatically(nextId);
        if (!DefaultBranchPolicy.TryNext(engine, out var next, out int steps) || next?.Id != nextId ||
            (defaultRoute ? defaultAutoVisited.Contains(nextId) : !BranchRouteContext.IsVerifiedCommon(engine, next))) return false;
        if (defaultRoute) { defaultAutoNode = nextId; defaultAutoVisited.Add(nextId); }
        for (int i = 0; i < steps; i++) engine.Next();
        return engine.CurrentId == nextId && engine.Mode == RunMode.Following;
    }

    public string CurrentGameLineText => OverlayCurrentLineText();

    string OverlayCurrentLineText()
    {
        var engine = Engine;
        if (engine?.Mode == RunMode.Gap)
            return "后续连接待核对，并非剧情已结束；请对照游戏定位当前台词。";
        if (engine?.Current is not { } node) return "请选择当前台词";
        var route = BranchRouteContext.Resolve(engine, node);
        if (node.Kind == "merge")
        {
            if (Settings.AutoPlayConfirmBranch && route is { IsConfirmedBranch: true }) return "剧情衔接处";
            // 汇合只能结束内层选择；仍在父分支时，不能照抄包里的“返回共同线”。
            if (route != null) return route.IsConfirmedBranch
                ? "内层选项已汇合，仍在" + route.Label
                : "内层选项已汇合；" + route.PendingLabel;
            if (!string.IsNullOrEmpty(node.PathId)) return "已到汇合点，所属分支待核对";
            var common = CommonLineAfterBranch(engine);
            if (common == null && BranchRouteContext.IsVerifiedCommon(engine, node) &&
                node.NextId is { } next && engine.Pack.ById.TryGetValue(next, out var candidate) &&
                candidate.Kind == "line" && BranchRouteContext.IsVerifiedCommon(engine, candidate)) common = candidate;
            return common != null ? (Settings.AutoPlayConfirmBranch ? "后续对白：" : "已到共同线：") + common.Text : "已到汇合点，后续路线待核对";
        }
        if (node.Kind != "line") return node.Text;
        if (Settings.AutoPlayConfirmBranch && route is { IsConfirmedBranch: true }) return node.Text;
        if (route != null) return (route.IsConfirmedBranch ? route.Label : route.PendingLabel) + "：" + node.Text;
        // Action472 的源游戏归属比旧包 PathId 更准确；停止/重开也保留同一身份。
        if (!Settings.AutoPlayConfirmBranch && DefaultBranchDisplayPolicy.TryGetVisibleBranch(engine, out int visibleBranch))
            return DefaultBranchName(visibleBranch) + "：" + node.Text;
        return string.IsNullOrEmpty(node.PathId) ? node.Text : "分支归属待核对：" + node.Text;
    }

    bool BeginDefaultBranchWait(PlaybackEngine engine, AdvanceTapTarget target, string displayKey)
    {
        if (!DirectDefaultBranchEnabled || !DefaultBranchPolicy.TryChoice(engine, Settings.DefaultBranchOption, out var option, out var first)) return false;
        var visited = defaultAutoVisited.ToArray();
        var intent = new BranchResumeIntent(engine, engine.CurrentId!, AutoGamePackage, target, displayKey,
            SystemClock.ElapsedRealtime(), true, Settings.DefaultBranchOption);
        Node? anchor = DefaultBranchDisplayPolicy.TryGetDisplayAnchor(engine, intent.DefaultOption, out _, out var mapped) ? mapped : null;
        // 用户明确选择默认路线：软件直接选同一路并配音，游戏选项仍由玩家点击。
        // 不启动选项 OCR，也不创建需要再次确认的分支等待窗口。
        CancelAutoPlayback(); CancelClickFollow(); CancelBranchFollow();
        audio.Stop(); engine.PauseForBrowse();
        if (!BranchIntentEnvironmentValid(intent) || first == null || option == null ||
            !CanAutoPlayLine(engine.Pack, first) || anchor != null && !CanAutoPlayLine(engine.Pack, anchor))
        { Status = "默认分支或配音暂不可用，请核对当前位置。"; Notify(); overlay.ShowNotice(Status); return true; }
        StartConfirmedDefaultBranch(engine, intent, option, first, anchor, visited);
        if (autoPlay.Running && ReferenceEquals(defaultAutoEngine, engine))
            overlay.ShowBranchEntered("进入" + DefaultBranchName(intent.DefaultOption) + "：" + option.Label);
        return true;
    }

    void ShowDefaultBranchWait(BranchFollowAttempt attempt, string reason)
    {
        if (!ReferenceEquals(branchFollow, attempt) || attempt.Intent.DefaultOption == 0 ||
            !attempt.Intent.Engine.Pack.ById.TryGetValue(attempt.DefaultDisplayNode ?? attempt.DefaultFirstNode ?? "", out var first)) return;
        string message = DefaultBranchName(attempt.Intent.DefaultOption) + " · 等待游戏台词\n" +
            first.Speaker + "：" + first.Text + "\n" + reason;
        if (attempt.WaitMessage == message && overlay.CaptureWaitExcludedBottom > 0) return;
        attempt.WaitMessage = message; Status = message;
        overlay.ShowDefaultBranchWait(message, () =>
        {
            if (CheckBranchFollow(attempt)) overlay.ShowBrowsePage(OverlayBrowsePage.Branches);
        });
        Notify();
    }

    void CommitDefaultBranchLine(BranchFollowAttempt attempt, string nodeId)
    {
        var engine = attempt.Intent.Engine;
        if (!CheckBranchFollow(attempt) || !DefaultBranchPolicy.TryChoice(engine, attempt.Intent.DefaultOption, out var option, out var first) ||
            first == null || option == null || nodeId != attempt.DefaultDisplayNode || first.Id != attempt.DefaultFirstNode ||
            option.Id != attempt.SelectedOptionId || !CanAutoPlayLine(engine.Pack, first))
        { if (ReferenceEquals(branchFollow, attempt)) StopBranchFollow("默认分支首句或配音尚不能确认，请手动选择续接台词。"); return; }
        Node? anchor = null;
        if (nodeId != first.Id && (!DefaultBranchDisplayPolicy.TryGetDisplayAnchor(engine, attempt.Intent.DefaultOption, out var mappedFirst, out anchor) ||
            mappedFirst != first || anchor?.Id != nodeId || !CanAutoPlayLine(engine.Pack, anchor)))
        { StopBranchFollow("游戏显示的续接句或配音已变化，请手动核对。"); return; }
        // 当前画面已唯一证明玩家点入默认分支。只提交一次选支，不点击游戏，也不跳过首句。
        CancelBranchFollow();
        if (attempt.Invalidated) return;
        StartConfirmedDefaultBranch(engine, attempt.Intent, option, first, anchor, attempt.PriorDefaultLines);
    }

    void StartConfirmedDefaultBranch(PlaybackEngine engine, BranchResumeIntent intent, ChoiceOption option,
        Node first, Node? anchor, string[] visited)
    {
        if (!BranchIntentEnvironmentValid(intent) || !PrepareAutoPlaybackEnvironment()) return;
        defaultAutoEngine = engine; defaultAutoNode = first.Id;
        defaultAutoVisited.UnionWith(visited);
        if (!defaultAutoVisited.Add(first.Id)) { PauseAutoPlayback("路线返回了已经自动播放的句子，请手动核对，避免循环。" ); return; }
        autoPlay.Start(first.Id, SystemClock.ElapsedRealtime());
        if (!autoPlay.PreparePlayback(autoPlay.Epoch, first.Id, SystemClock.ElapsedRealtime())) { PauseAutoPlayback("默认分支起播已失效。"); return; }
        if (anchor != null) defaultPendingDisplayAnchor = new(engine, first, anchor, intent.DefaultOption, autoPlay.Epoch);
        overlay.SetExpanded(false); overlay.SetCaptureHidden(false); StartAutoPlaybackTimer();
        engine.SelectBranch(engine.AvailableOptions.FindIndex(o => o.Id == option.Id));
        if (engine.CurrentId != first.Id || engine.Mode != RunMode.Following) { PauseAutoPlayback("默认分支位置已变化，请手动核对。" ); return; }
        SectionId = first.SectionId;
        Diagnostics.Log("默认分支自动续播", intent.MenuId + " → " + first.Id);
        ContentChanged?.Invoke(); Notify();
    }

    bool TryPlayPendingDefaultDisplayAnchor(PlaybackEngine engine, long epoch)
    {
        if (defaultPendingDisplayAnchor is not { } pending) return false;
        defaultPendingDisplayAnchor = null;
        if (pending.Invalidated || !ReferenceEquals(engine, pending.Engine) || !ReferenceEquals(engine.Current, pending.First) || epoch != pending.AutoEpoch ||
            !AutomaticBranchRoutesEnabled ||
            !DefaultBranchDisplayPolicy.TryGetDisplayAnchor(engine, pending.Option, out var first, out var anchor) ||
            !ReferenceEquals(first, pending.First) || !ReferenceEquals(anchor, pending.Anchor) ||
            !CanAutoPlayLine(engine.Pack, pending.Anchor) || defaultAutoVisited.Contains(pending.Anchor.Id))
        { PauseAutoPlayback("选项配音后的游戏台词位置已变化，请手动核对；尚未点击游戏。"); return true; }
        // 玩家选项已让游戏显示此句。先补完选项现音，再只推进软件，不再次点击跳过游戏首句。
        defaultAutoNode = pending.Anchor.Id; defaultAutoVisited.Add(pending.Anchor.Id);
        // 已核 Action472 仅属第二项；Action473 才是共同句。只标记本次精确映射的那一句。
        defaultDisplayBranch = pending.Option == 2 ? new(engine, pending.Anchor, pending.First.PathId, 2) : null;
        autoPlay.Start(pending.Anchor.Id, SystemClock.ElapsedRealtime());
        if (!autoPlay.PreparePlayback(autoPlay.Epoch, pending.Anchor.Id, SystemClock.ElapsedRealtime()))
        { PauseAutoPlayback("游戏首句续播请求已失效，请重新核对。"); return true; }
        StartAutoPlaybackTimer();
        if (!engine.ConfirmGameLine(pending.Anchor.Id))
        { PauseAutoPlayback(engine.NavigationError); return true; }
        SectionId = pending.Anchor.SectionId;
        Diagnostics.Log("默认分支显示首句续播", pending.First.Id + " → " + pending.Anchor.Id + "；无游戏点击");
        ContentChanged?.Invoke(); Notify(); return true;
    }
}
