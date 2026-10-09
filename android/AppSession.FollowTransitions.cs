using Android.OS;
using PgrVoice.AndroidApp.Platform;
using System.Text.Json;

namespace PgrVoice.AndroidApp;

public sealed partial class AppSession
{
    sealed record ManualBranchConfirmation(PlaybackEngine Engine, Node Menu, long Epoch, long AudioGeneration,
        string Navigation, string GamePackage, AdvanceTapTarget Target, string DisplayKey,
        ScreenDisplayState Display, long CaptureSession, bool CaptureActive, bool ResumeAutoAfterCommon,
        string[] OptionIds, string? EntryOptionId)
    {
        public volatile bool Invalidated;
    }
    ManualBranchConfirmation? manualBranchConfirmation;
    Action? manualBranchWatchdog;
    long manualBranchServiceToken;

    void CancelManualBranchFollow()
    {
        CancelBranchBrowseContinuation();
        CancelBranchCommonWait();
        manualBranchConfirmation = null;
        bool hadSession = manualBranchServiceToken != 0;
        manualBranchServiceToken = 0;
        if (manualBranchWatchdog != null) main.RemoveCallbacks(manualBranchWatchdog);
        manualBranchWatchdog = null;
        if (hadSession) GameAdvanceAccessibilityService.CancelSession("分支确认已结束。");
        overlay.ClearManualBranchSelection();
    }

    // 在系统事件发生时冻结受影响的会话，主线程晚到的旧取消不能停止后来新建的会话。
    Action CaptureFollowSessionInvalidation(string message)
    {
        var confirmedBranchInvalidation = CaptureConfirmedBranchInvalidation(message);
        var sourceChoiceInvalidation = CaptureSourceChoiceInvalidation(message);
        var manual = manualBranchConfirmation;
        if (manual != null) manual.Invalidated = true;
        var browsing = branchBrowseContinuation;
        if (browsing != null) browsing.Invalidated = true;
        var pendingDisplay = defaultPendingDisplayAnchor;
        if (pendingDisplay != null) pendingDisplay.Invalidated = true;
        var commonWait = branchCommonWait;
        var autoConfirmation = autoStartConfirmation;
        if (autoConfirmation != null) autoConfirmation.Invalidated = true;
        long automaticEpoch = autoPlay.Epoch, automaticToken = autoServiceToken, clickToken = clickServiceToken;
        var branch = branchFollow;
        if (branch != null) branch.Invalidated = true;
        var anchor = branchAnchorRequest;
        return () =>
        {
            confirmedBranchInvalidation();
            sourceChoiceInvalidation();
            if (browsing != null && ReferenceEquals(branchBrowseContinuation, browsing))
            { CancelBranchBrowseContinuation(); Status = message + " 本次分支续接已取消，请重新核对。"; Notify(); overlay.ShowNotice(Status); }
            if (anchor != null && ReferenceEquals(branchAnchorRequest, anchor))
            { Invalidate(); Status = message + " 本次分支定位已取消，请重新识别。"; Notify(); }
            if (commonWait != null && ReferenceEquals(branchCommonWait, commonWait))
            { CancelBranchCommonWait(); Status = message + " 共同线等待已取消，请重新核对。"; Notify(); }
            if (manual != null && ReferenceEquals(manualBranchConfirmation, manual))
            { CancelManualBranchFollow(); Status = message + " 请重新核对分支位置。"; Notify(); }
            if ((autoPlay.Running && autoPlay.Epoch == automaticEpoch && autoServiceToken == automaticToken) ||
                (autoConfirmation != null && ReferenceEquals(autoStartConfirmation, autoConfirmation))) PauseAutoPlayback(message);
            if (clickToken != 0 && ClickFollowRunning && clickServiceToken == clickToken) StopClickFollow(message);
            if (branch != null && ReferenceEquals(branchFollow, branch)) StopBranchFollow(message);
        };
    }

    bool ManualBranchEnvironmentValid(ManualBranchConfirmation confirmation)
    {
        if (confirmation.Invalidated || !Settings.BranchAutoFollow || uiVisible || !overlay.CanShow || !ReferenceEquals(Engine, confirmation.Engine) ||
            !ReferenceEquals(confirmation.Engine.Current, confirmation.Menu) || confirmation.Engine.Mode != RunMode.Choice ||
            !confirmation.Menu.Options.Select(o => o.Id).SequenceEqual(confirmation.OptionIds) ||
            ocrEpoch != confirmation.Epoch || playGeneration != confirmation.AudioGeneration ||
            JsonSerializer.Serialize(confirmation.Engine.ExportNavigation(), Json.Options) != confirmation.Navigation ||
            AutoGamePackage != confirmation.GamePackage || Screen.DisplayState != confirmation.Display ||
            Screen.SessionId != confirmation.CaptureSession || Screen.CaptureActive != confirmation.CaptureActive ||
            Settings.AdvanceTargetFor(confirmation.DisplayKey) != confirmation.Target) return false;
        var state = GameAdvanceAccessibilityService.GetAvailability(confirmation.GamePackage);
        return state.CanTap && state.DisplayKey == confirmation.DisplayKey && MatchesTarget(confirmation.Target, state);
    }

    bool BeginManualBranchFollow(PlaybackEngine engine, AdvanceTapTarget target, string displayKey, bool resumeAutoAfterCommon,
        string? confirmedOptionId = null, string? entryOptionId = null, bool showOptionLabels = false)
    {
        if (!Settings.BranchAutoFollow || engine.Current is not { Kind: "choice" } menu ||
            !(BranchFollowPolicy.IsEligibleManualMenu(engine, menu.Id, out _) ||
              BranchFollowPolicy.IsEligibleInteractionMenu(engine, menu.Id, out _))) return false;
        Invalidate(); audio.Stop(); engine.PauseForBrowse();
        var confirmation = new ManualBranchConfirmation(engine, menu, ocrEpoch, playGeneration,
            JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options), AutoGamePackage, target, displayKey,
            Screen.DisplayState, Screen.SessionId, Screen.CaptureActive, resumeAutoAfterCommon,
            menu.Options.Select(o => o.Id).ToArray(), entryOptionId);
        if (!ManualBranchEnvironmentValid(confirmation))
        { Status = "游戏、屏幕或点按区域已变化，请手动核对分支位置。"; Notify(); return true; }
        // 仅监听前台/屏幕失效，不发触碰；否则两种播放都暂停时，服务不会上报瞬间切出。
        manualBranchServiceToken = GameAdvanceAccessibilityService.BeginSession(confirmation.GamePackage,
            target.DisplayWidth, target.DisplayHeight, target.Rotation);
        if (manualBranchServiceToken == 0)
        { Status = GameAdvanceAccessibilityService.LastFailureReason; Notify(); return true; }
        manualBranchConfirmation = confirmation;
        overlay.SetCaptureHidden(false); overlay.SetExpanded(false);
        bool interaction = menu.MenuType is "interaction" or "topics";
        Status = interaction ? "请按游戏当前的人物、话题或开头台词确认；已撤下点按区域，可以在游戏中移动。" :
            "已到分支，请先在游戏中选择，再确认并开启点按跟随。"; Notify();
        manualBranchWatchdog = () =>
        {
            if (!ReferenceEquals(manualBranchConfirmation, confirmation)) return;
            if (!ManualBranchEnvironmentValid(confirmation))
            { CancelManualBranchFollow(); Status = "游戏、屏幕或分支位置已变化，分支确认已取消。请重新核对。"; Notify(); return; }
            if (manualBranchWatchdog != null) main.PostDelayed(manualBranchWatchdog, 400);
        };
        main.PostDelayed(manualBranchWatchdog, 400);
        if (confirmedOptionId != null)
        {
            ConfirmManualBranchFollow(confirmation, Array.IndexOf(confirmation.OptionIds, confirmedOptionId));
            return true;
        }
        if (!interaction && !showOptionLabels && ShowManualBranchAnchorCards(confirmation)) return true;
        var entryOption = entryOptionId == null ? null : menu.Options.SingleOrDefault(o => o.Id == entryOptionId);
        var topics = entryOption == null ? null : BranchFollowPolicy.DirectTopicsMenu(engine.Pack, menu, entryOption);
        if (entryOptionId != null && (entryOption == null || topics == null))
        { CancelManualBranchFollow(); Status = "当前内容入口已变化，请重新选择人物。"; Notify(); return true; }
        string[] labels = entryOption != null && topics != null
            ? new[] { "开头台词：" + BrowseSpeaker(engine.Pack.ById[entryOption.TargetId]) + "：" + engine.Pack.ById[entryOption.TargetId].Text,
                "话题菜单：" + topics.Text + "\n" + string.Join(" ／ ", topics.Options.Select(o => o.Label)) }
            : menu.Options.Select(o => interaction ? BranchFollowPolicy.InteractionOptionLabel(engine, menu, o) : o.Label).ToArray();
        string? actorMenu = interaction ? BranchFollowPolicy.ActorMenuId(engine) : null;
        bool returnActors = actorMenu != null && (actorMenu != menu.Id || entryOption != null);
        overlay.ShowManualBranchSelection(BrowseSectionTitle(engine, menu.SectionId) + (interaction ? "\n" + menu.Text : ""), labels,
            index => main.PostDelayed(() => Post(() => ConfirmManualBranchFollow(confirmation, index)), 180),
            () => Post(() =>
            {
                if (!ReferenceEquals(manualBranchConfirmation, confirmation)) return;
                CancelManualBranchFollow(); Status = "已取消分支跟随，请在游戏和悬浮分支页核对相同选项。"; Notify();
            }), title: entryOption != null ? "游戏当前显示什么？" : interaction ? menu.MenuType == "topics" ? "请选择游戏当前的话题" : "请选择游戏中交谈的人物" : "此处分支请开启点按跟随",
            instruction: entryOption != null ? "对照游戏当前画面选择，确认后才播放。" :
                interaction ? "先在游戏中选好，再点同名项。聊过的仍可再选。" : "先在游戏中选择，再确认同项并开启点按跟随；无需再次到悬浮控制开启。",
            buttonPrefix: entryOption != null ? "游戏当前显示：" : interaction ? menu.MenuType == "topics" ? "我在游戏中选了：" : "我正在交谈：" : "确认并开启跟随：",
            secondaryActionLabel: returnActors ? "返回人物列表，换人交谈" : null,
            secondaryAction: returnActors ? () => main.PostDelayed(() => Post(() => ReturnManualToActors(confirmation, actorMenu!)), 180) : null,
            additionalActionLabel: "游戏选好后，识别下一句",
            additionalAction: () => main.PostDelayed(() => Post(() => RequestManualBranchAnchor(confirmation)), 180));
        Diagnostics.Log("分支等待人工确认", menu.Id);
        return true;
    }

    void ConfirmManualBranchFollow(ManualBranchConfirmation confirmation, int index)
    {
        if (!ReferenceEquals(manualBranchConfirmation, confirmation)) return;
        // 先消费票据，再验证并提交；重复按钮或旧窗口回调均不能再次选支。
        CancelManualBranchFollow();
        var engine = confirmation.Engine;
        bool interaction = BranchFollowPolicy.IsEligibleInteractionMenu(engine, confirmation.Menu.Id, out _);
        if (!ManualBranchEnvironmentValid(confirmation) ||
            !(interaction || BranchFollowPolicy.IsEligibleManualMenu(engine, confirmation.Menu.Id, out _)) ||
            index < 0 || index >= (confirmation.EntryOptionId != null ? 2 : confirmation.OptionIds.Length))
        { Status = "确认期间位置或屏幕已变化，请重新核对分支后继续。"; Notify(); return; }
        string optionId = confirmation.EntryOptionId ?? confirmation.OptionIds[index];
        var option = engine.AvailableOptions.SingleOrDefault(o => o.Id == optionId);
        if (option == null) { Status = "选项已变化，请重新选择。"; Notify(); return; }
        if (interaction)
        {
            if (!BranchFollowPolicy.TryGetInteractionTarget(engine, confirmation.Menu.Id, option.Id, out _, out string reason))
            { Status = reason; Notify(); return; }
            var topics = confirmation.Menu.MenuType == "interaction" ? BranchFollowPolicy.DirectTopicsMenu(engine.Pack, confirmation.Menu, option) : null;
            if (topics != null && engine.Pack.ById[option.TargetId].Kind == "line")
            {
                if (confirmation.EntryOptionId == null)
                {
                    BeginManualBranchFollow(engine, confirmation.Target, confirmation.DisplayKey, confirmation.ResumeAutoAfterCommon, entryOptionId: option.Id);
                    return;
                }
                if (index == 1)
                {
                    Invalidate(); audio.Stop();
                    if (!engine.OpenGameMenu(topics.Id, confirmation.Menu.SectionId))
                    { Status = engine.NavigationError; Notify(); return; }
                    BeginManualBranchFollow(engine, confirmation.Target, confirmation.DisplayKey, confirmation.ResumeAutoAfterCommon);
                    ContentChanged?.Invoke(); return;
                }
            }
        }
        var probe = new PlaybackEngine(engine.Pack);
        if (!probe.ImportNavigation(engine.ExportNavigation())) return;
        if (interaction) { if (!probe.OpenGameMenu(confirmation.Menu.Id, confirmation.Menu.SectionId)) return; }
        else probe.Commit(confirmation.Menu.Id);
        probe.SelectBranch(probe.AvailableOptions.FindIndex(o => o.Id == option.Id));
        if (probe.Current is not { } selected || selected.Id != option.TargetId || selected.PathId != option.PathId || !probe.Allowed(selected) ||
            (selected.Kind == "line" ? probe.Mode != RunMode.Following : !interaction || selected.Kind != "choice" || probe.Mode != RunMode.Choice))
        { Status = "该分支首句尚未可靠核实，请手动选择续接位置。"; Notify(); return; }
        if (selected.Kind == "line" && !CanAutoPlayLine(engine.Pack, selected))
        { Status = "这条分支首句没有可用配音，尚未开启点按跟随。请按游戏当前句手动核对。"; Notify(); return; }
        Invalidate(); audio.Stop();
        if (interaction && !engine.OpenGameMenu(confirmation.Menu.Id, confirmation.Menu.SectionId))
        { Status = engine.NavigationError; Notify(); return; }
        engine.SelectBranch(engine.AvailableOptions.FindIndex(o => o.Id == option.Id));
        if (engine.CurrentId != option.TargetId)
        { Status = "分支位置未能确认，请手动核对后继续。"; Notify(); return; }
        SectionId = engine.Current!.SectionId;
        if (engine.Mode == RunMode.Choice)
        {
            if (!BeginManualBranchFollow(engine, confirmation.Target, confirmation.DisplayKey, confirmation.ResumeAutoAfterCommon))
            { Status = "已打开当前菜单，请在悬浮分支页按游戏画面选择。"; Notify(); }
        }
        else if (engine.Mode == RunMode.Following && engine.Current.Kind == "line")
        {
            StartClickFollow(preserveAudio: true);
            RecordBranchAutoReturn(engine, confirmation.ResumeAutoAfterCommon);
        }
        Diagnostics.Log("分支人工确认后点按跟随", confirmation.Menu.Id + " → " + option.Id);
        ContentChanged?.Invoke(); Notify();
    }

    void ReturnManualToActors(ManualBranchConfirmation confirmation, string actorMenuId)
    {
        if (!ReferenceEquals(manualBranchConfirmation, confirmation)) return;
        CancelManualBranchFollow();
        if (!ManualBranchEnvironmentValid(confirmation) || BranchFollowPolicy.ActorMenuId(confirmation.Engine) != actorMenuId)
        { Status = "人物列表已变化，请重新核对当前画面。"; Notify(); return; }
        Invalidate(); audio.Stop();
        if (!confirmation.Engine.OpenInteractionMenu(actorMenuId))
        { Status = confirmation.Engine.NavigationError; Notify(); return; }
        BeginManualBranchFollow(confirmation.Engine, confirmation.Target, confirmation.DisplayKey, confirmation.ResumeAutoAfterCommon);
        ContentChanged?.Invoke();
    }

    bool TryBeginInteractionFromOverlay(PlaybackEngine engine, string? confirmedOptionId = null)
    {
        if (!Settings.BranchAutoFollow || engine.Current is not { Kind: "choice" } menu ||
            !BranchFollowPolicy.IsEligibleInteractionMenu(engine, menu.Id, out _)) return false;
        var state = GameAdvanceAccessibilityService.GetForegroundAvailability();
        if (uiVisible || !state.CanTap || !overlay.CanShow || state.DisplayKey is not { } key ||
            Settings.AdvanceTargetFor(key) is not { } target || !MatchesTarget(target, state))
        {
            // 辅助开着却无法核对环境时，不能退回旧选支代码直接播放可能过时的开场。
            Status = uiVisible ? "请回到游戏，通过悬浮人物列表确认当前内容。" : !state.CanTap ? state.Reason :
                "请先在游戏中设置此屏幕的下一句区域，再确认人物或话题。";
            Notify(); return true;
        }
        AutoGamePackage = state.ForegroundPackage ?? "";
        return BeginManualBranchFollow(engine, target, key, false, confirmedOptionId);
    }

    sealed record BranchAutoReturnIntent(PlaybackEngine Engine, string GamePackage, AdvanceTapTarget Target,
        string DisplayKey, long CaptureSession, ScreenDisplayState Display);
    BranchAutoReturnIntent? branchAutoReturn;

    void ClearBranchAutoReturn() => branchAutoReturn = null;

    bool BranchAutoReturnValid(PlaybackEngine engine)
    {
        if (branchAutoReturn is not { } intent || !Settings.BranchAutoFollow || uiVisible || !overlay.CanShow ||
            !ReferenceEquals(Engine, engine) || !ReferenceEquals(intent.Engine, engine) || !Screen.CaptureActive ||
            Screen.SessionId != intent.CaptureSession || Screen.DisplayState != intent.Display ||
            AutoGamePackage != intent.GamePackage || Settings.AdvanceTargetFor(intent.DisplayKey) != intent.Target) return false;
        var state = GameAdvanceAccessibilityService.GetAvailability(intent.GamePackage);
        return state.CanTap && state.DisplayKey == intent.DisplayKey && MatchesTarget(intent.Target, state);
    }

    void RecordBranchAutoReturn(PlaybackEngine engine, bool resumeAutoAfterCommon)
    {
        branchAutoReturn = null;
        if (!resumeAutoAfterCommon || !ClickFollowRunning || clickTarget == null || clickDisplayKey == null ||
            !ReferenceEquals(Engine, engine) || !Screen.CaptureActive) return;
        branchAutoReturn = new(engine, AutoGamePackage, clickTarget, clickDisplayKey, Screen.SessionId, Screen.DisplayState);
    }

    Node? AutomaticBranchMenu(PlaybackEngine engine, out int steps)
    {
        steps = 1;
        if ((!Settings.BranchAutoFollow && !AutomaticBranchRoutesEnabled) || engine.Current is not { Kind: "line", NextId: not null } current) return null;
        Node? menu = null;
        if ((DefaultAutoLinePermitted(engine) || VerifiedCommonLinePermitted(engine)) &&
            DefaultBranchPolicy.TryNext(engine, out var next, out steps, includeChoice: true) && next?.Kind == "choice") menu = next;
        else
        {
            var decision = engine.EvaluateCommonAutoPlayNext();
            if (!decision.CurrentIsCommon || decision.Code != "choice" ||
                !engine.Pack.ById.TryGetValue(current.NextId, out menu) || menu.Archived ||
                menu.Kind != "choice" || menu.SectionId != current.SectionId) return null;
            steps = 1;
        }
        // 用真实导航器克隆核全部无正文控制点，不多点游戏、不略过任何正文。
        var probe = new PlaybackEngine(engine.Pack);
        if (!probe.ImportNavigation(engine.ExportNavigation()) || !probe.ConfirmCurrentPosition()) return null;
        for (int i = 0; i < steps; i++) probe.Next(true);
        if (probe.CurrentId != menu.Id) return null;
        if (Settings.AutoPlayConfirmBranch)
            return ConfirmedBranchPolicy.Create(probe).Cards.Count > 0 ? menu : null;
        if (Settings.AutoPlayDefaultBranchEnabled && DefaultBranchPolicy.TryChoice(probe, Settings.DefaultBranchOption, out _, out _)) return menu;
        return Settings.BranchAutoFollow && BranchFollowPolicy.IsEligibleManualMenu(probe, menu.Id, out _) ? menu : null;
    }

    bool TryAdvanceAutoIntoBranch(PlaybackEngine engine, long epoch)
    {
        if (autoPlay.Epoch != epoch || !autoPlay.Running || engine.CurrentId != autoPlay.NodeId ||
            autoTarget is not { } target || autoDisplay.RegionKey is not { } displayKey ||
            AutomaticBranchMenu(engine, out _) is not { } menu) return false;
        if (Settings.AdvanceTargetFor(displayKey) != target || overlay.ContainsPoint(target.CenterX, target.CenterY))
        { PauseAutoPlayback("下一句区域已变化或被悬浮控制挡住，尚未点击游戏。请核对后继续。"); return true; }
        string navigation = JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options);
        if (!autoPlay.BeginTap(epoch, menu.Id, SystemClock.ElapsedRealtime()))
        { PauseAutoPlayback("分支推进请求已变化，尚未点击游戏。请核对后继续。"); return true; }
        long ticket = autoPlay.TapTicket;
        Diagnostics.Log("自动播放进入分支单击", $"from={engine.CurrentId}; menu={menu.Id}; ticket={ticket}");
        _ = AdvanceIntoBranchOnceAsync(engine, epoch, ticket, menu.Id, navigation, autoServiceToken, target, displayKey);
        return true;
    }

    async Task AdvanceIntoBranchOnceAsync(PlaybackEngine engine, long epoch, long ticket, string menuId,
        string navigation, long token, AdvanceTapTarget target, string displayKey)
    {
        bool StillCurrent() => ReferenceEquals(Engine, engine) && autoPlay.Epoch == epoch && autoPlay.Running &&
            engine.CurrentId == autoPlay.NodeId && JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options) == navigation;
        try
        {
            var result = await GameAdvanceAccessibilityService.TapAsync(token, target.CenterX, target.CenterY);
            Post(() =>
            {
                if (!StillCurrent()) return;
                if (!CheckAutoPlaybackEnvironment()) return;
                if (!result.Completed)
                { PauseAutoPlayback(result.Reason + " 不会重复补点，请手动核对分支画面。"); return; }
                if (!autoPlay.GestureCompleted(epoch, ticket, menuId, SystemClock.ElapsedRealtime())) return;
                main.PostDelayed(() => Post(() =>
                {
                    if (!StillCurrent() || !CheckAutoPlaybackEnvironment()) return;
                    if (AutomaticBranchMenu(engine, out int steps)?.Id != menuId)
                    { PauseAutoPlayback("分支路线已变化，请手动核对；不会再次点击。"); return; }
                    for (int i = 0; i < steps; i++) engine.Next(true);
                    if (engine.CurrentId != menuId || engine.Mode != RunMode.Choice)
                    { PauseAutoPlayback("没有进入预期分支，请手动核对游戏位置。"); return; }
                    // 默认模式直接配已选项，游戏选项仍由玩家点击。
                    if (Settings.AutoPlayConfirmBranch ? BeginConfirmedBranchWait(engine, target, displayKey) : BeginDefaultBranchWait(engine, target, displayKey))
                    { Diagnostics.Advance(); return; }
                    // 本次 NEXT 已结束自动周期。玩家先在游戏选完，再在小窗明确确认同项。
                    CancelAutoPlayback();
                    if (!BeginManualBranchFollow(engine, target, displayKey, resumeAutoAfterCommon: true))
                    { PauseAutoPlayback("已出现分支，请在游戏和配音中选择相同选项；支线使用点按跟随。"); return; }
                    Diagnostics.Advance(); Diagnostics.Log("自动播放转分支跟随", menuId);
                }), 450);
            });
        }
        catch (Exception ex)
        { Post(() => { if (StillCurrent()) PauseAutoPlayback("分支推进未确认：" + ex.Message + " 不会重复补点。"); }); }
    }

    Node? CommonLineAfterBranch(PlaybackEngine engine)
    {
        // 汇合只是控制节点；只从已核导航读出候选，不让真实引擎提前进入或播放。
        if (engine.Current is not { Kind: "merge" } source || engine.Mode != RunMode.Merge || !engine.Allowed(source) ||
            engine.ReviewRoute != null) return null;
        var probe = new PlaybackEngine(engine.Pack);
        var snapshot = engine.ExportNavigation();
        if (snapshot.Current.SingleLine || !probe.ImportNavigation(snapshot)) return null;
        // 导入会主动恢复成 Ready；只在克隆上重设同一个 merge，正式导航仍保持原位。
        probe.Commit(source.Id);
        probe.Next(true);
        return probe.Current is { Kind: "line" } line && line.SectionId == source.SectionId &&
            probe.Mode == RunMode.Following &&
            (probe.EvaluateCommonAutoPlayNext().CurrentIsCommon || VerifiedCommonLinePermitted(probe)) ? line : null;
    }

    bool TryOfferCommonAutoResume(PlaybackEngine engine)
    {
        if (!ClickFollowRunning || !BranchAutoReturnValid(engine) || CommonLineAfterBranch(engine) is not { } candidate) return false;
        var source = engine.Current!;
        Invalidate(); audio.Stop(); engine.PauseForBrowse();
        // 真实位置仍是汇合点，用户核对这句以后才按自动播放起点确认流程定位并播放一次。
        ShowAutoStartConfirmation(engine, source, candidate);
        Diagnostics.Log("分支返回共同线待确认", source.Id + " → " + candidate.Id);
        return true;
    }
}
