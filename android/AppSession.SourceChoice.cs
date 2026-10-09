using Android.OS;
using PgrVoice.AndroidApp.Platform;
using System.Text.Json;

namespace PgrVoice.AndroidApp;

public sealed partial class AppSession
{
    // 原包把已核单项游戏选项压成了 line；只在会话中等待，不改包图或存档指纹。
    sealed record SourceChoiceWait(PlaybackEngine Engine, SourceChoiceOffer Offer, long Epoch, long AudioGeneration,
        string Navigation, string GamePackage, AdvanceTapTarget Target, string DisplayKey,
        ScreenDisplayState Display, long CaptureSession, bool CaptureActive, FollowResumeMode ResumeMode)
    {
        public volatile bool Invalidated;
        public bool Submitted;
    }
    sealed record SourceChoiceAdvance(PlaybackEngine Engine, SourceChoiceOffer Offer, long Epoch, long Ticket,
        string Navigation, AdvanceTapTarget Target, string DisplayKey)
    { public volatile bool Invalidated; }
    SourceChoiceWait? sourceChoiceWait;
    SourceChoiceAdvance? sourceChoiceAdvance;
    long sourceChoiceServiceToken;
    Action? sourceChoiceWatchdog;
    public bool SourceChoiceWaiting => sourceChoiceWait != null;

    void CancelSourceChoiceWait()
    {
        bool hadWait = sourceChoiceWait != null;
        sourceChoiceWait = null; sourceChoiceAdvance = null;
        bool hadSession = sourceChoiceServiceToken != 0; sourceChoiceServiceToken = 0;
        if (sourceChoiceWatchdog != null) main.RemoveCallbacks(sourceChoiceWatchdog);
        sourceChoiceWatchdog = null;
        if (hadWait) overlay.ClearManualBranchSelection();
        if (hadSession) GameAdvanceAccessibilityService.CancelSession("单项选择确认已结束。");
    }

    Action CaptureSourceChoiceInvalidation(string message)
    {
        var wait = sourceChoiceWait; var advance = sourceChoiceAdvance;
        if (wait != null) wait.Invalidated = true;
        if (advance != null) advance.Invalidated = true;
        return () =>
        {
            if (advance != null && ReferenceEquals(sourceChoiceAdvance, advance))
            { PauseAutoPlayback(message + " 请按游戏画面重新核对选项。"); return; }
            if (wait == null || !ReferenceEquals(sourceChoiceWait, wait)) return;
            CancelSourceChoiceWait(); Status = message + " 本次选项确认已取消，请重新核对。"; Notify();
            if (overlay.CanShow) overlay.ShowNotice(Status);
        };
    }

    bool SourceChoiceDisplayValid(SourceChoiceWait wait)
    {
        if (wait.Invalidated || !FollowConfirmationEnabled(wait.ResumeMode) || uiVisible || !overlay.CanShow ||
            AutoGamePackage != wait.GamePackage || Screen.CaptureActive != wait.CaptureActive ||
            (wait.ResumeMode == FollowResumeMode.AutoPlayback && !Screen.CaptureActive) || Screen.SessionId != wait.CaptureSession ||
            Screen.DisplayState != wait.Display || Settings.AdvanceTargetFor(wait.DisplayKey) != wait.Target) return false;
        var state = GameAdvanceAccessibilityService.GetAvailability(wait.GamePackage);
        return state.CanTap && state.DisplayKey == wait.DisplayKey && MatchesTarget(wait.Target, state);
    }

    bool SourceChoiceEnvironmentValid(SourceChoiceWait wait) => sourceChoiceServiceToken != 0 &&
        ReferenceEquals(Engine, wait.Engine) && ocrEpoch == wait.Epoch && playGeneration == wait.AudioGeneration &&
        JsonSerializer.Serialize(wait.Engine.ExportNavigation(), Json.Options) == wait.Navigation && SourceChoiceDisplayValid(wait);

    bool BeginSourceChoiceWait(PlaybackEngine engine, SourceChoiceOffer offered, AdvanceTapTarget target, string displayKey,
        FollowResumeMode resumeMode = FollowResumeMode.AutoPlayback)
    {
        if (!FollowConfirmationEnabled(resumeMode)) return false;
        string optionId = offered.OptionLineNode.Id;
        bool atOption = engine.CurrentId == optionId;
        Invalidate(); audio.Stop(); engine.PauseForBrowse();
        // 普通正文暂停会改变 Mode，重新绑定这一轮导航，而非继续使用暂停前的票据。
        SourceChoiceOffer? offer;
        bool rebound = atOption ? SourceChoicePolicy.TryAt(engine, out offer) : SourceChoicePolicy.TryNext(engine, out offer, out _);
        if (!rebound || offer == null || offer.OptionLineNode.Id != optionId ||
            offer.SectionContract != offered.SectionContract || offer.SourceIdentity != offered.SourceIdentity)
        { Status = "当前游戏选项或路线已变化，请按画面重新定位。"; Notify(); overlay.ShowNotice(Status); return true; }
        var wait = new SourceChoiceWait(engine, offer, ocrEpoch, playGeneration,
            JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options), AutoGamePackage, target, displayKey,
            Screen.DisplayState, Screen.SessionId, Screen.CaptureActive, resumeMode);
        if (!SourceChoiceDisplayValid(wait))
        { Status = "游戏、屏幕或下一句区域已变化，请重新核对当前位置。"; Notify(); overlay.ShowNotice(Status); return true; }
        sourceChoiceWait = wait;
        sourceChoiceServiceToken = GameAdvanceAccessibilityService.BeginSession(wait.GamePackage,
            target.DisplayWidth, target.DisplayHeight, target.Rotation);
        if (sourceChoiceServiceToken == 0)
        { CancelSourceChoiceWait(); Status = GameAdvanceAccessibilityService.LastFailureReason; Notify(); overlay.ShowNotice(Status); return true; }
        sourceChoiceWatchdog = () =>
        {
            if (!ReferenceEquals(sourceChoiceWait, wait)) return;
            if (!SourceChoiceEnvironmentValid(wait))
            { CancelSourceChoiceWait(); Status = "确认期间游戏或位置已变化，请重新核对当前台词。"; Notify(); overlay.ShowNotice(Status); return; }
            if (sourceChoiceWatchdog != null) main.PostDelayed(sourceChoiceWatchdog, 400);
        };
        main.PostDelayed(sourceChoiceWatchdog, 400);
        overlay.SetCaptureHidden(false); overlay.SetExpanded(false);
        void Selected(int index)
        {
            if (!ReferenceEquals(sourceChoiceWait, wait) || wait.Submitted) return;
            if (index != 0) { CancelSourceChoiceWait(); Status = "这条选项确认已失效，请重新核对。"; Notify(); return; }
            wait.Submitted = true;
            main.PostDelayed(() => Post(() => ConfirmSourceChoice(wait)), 180);
        }
        void Cancelled() => Post(() =>
        {
            if (!ReferenceEquals(sourceChoiceWait, wait)) return;
            CancelSourceChoiceWait(); Status = "已取消选项确认，配音保持暂停。"; Notify();
        });
        string label = "本处选项 1：" + offer.OptionLabel;
        string section = BrowseSectionTitle(engine, offer.OptionLineNode.SectionId);
        if (offer.ActualAnswerNode is { } answer)
        {
            Status = resumeMode == FollowResumeMode.ClickFollow
                ? "请先在游戏中点击这个选项，再确认选后的当前台词；确认后播放这句并恢复点按跟随。"
                : "请先在游戏中点击这个选项，再确认选后的当前台词；确认后自动继续配音。";
            ShowFollowDialogueCards(resumeMode, section,
                new[] { label + "\n" + (string.IsNullOrWhiteSpace(answer.Speaker) ? "旁白" : answer.Speaker) + "：" + answer.Text +
                    StoryReferenceDetails(engine.Pack, offer.OptionLineNode, answer, 1) }, Selected, Cancelled);
        }
        else if (offer.NextChoiceNode is { } next)
        {
            Status = "选后还有下一处游戏选项。请先在游戏选择本项，看到下一处选项后点卡片确认；配音继续暂停。";
            overlay.ShowManualBranchSelection(section,
                new[] { label + "\n下一处选项：" + next.Text + "\n确认后只显示下一张选项卡，不播放台词。" }, Selected, Cancelled,
                title: "请核对下一处游戏选项", instruction: Status, anchorCards: true,
                choiceConfirmationLabel: "已到下一处选项", dialogueCards: true);
        }
        else
        {
            Status = offer.Reason;
            overlay.ShowManualBranchSelection(section,
                new[] { label + "\n选后对白尚待核对\n" + offer.Reason }, Selected, Cancelled,
                title: "请选择游戏后核对当前对白", instruction: "先在游戏中选择，再点卡片打开台词目录。配音保持暂停，不会猜测后续路线。",
                anchorCards: true, choiceConfirmationLabel: "核对当前对白", dialogueCards: true);
        }
        Notify();
        Diagnostics.Log("等待已核单项选择确认", optionId + "；不播放选项复述，不修改包图");
        return true;
    }

    void ConfirmSourceChoice(SourceChoiceWait wait)
    {
        if (!ReferenceEquals(sourceChoiceWait, wait) || !wait.Submitted) return;
        if (!SourceChoiceEnvironmentValid(wait))
        { CancelSourceChoiceWait(); Status = "确认期间位置或游戏画面已变化，请重新核对。"; Notify(); return; }
        var engine = wait.Engine;
        if (!SourceChoicePolicy.TryResolveOffer(engine, wait.Offer, out var offer, out var reason) || offer == null)
        { CancelSourceChoiceWait(); Status = reason; Notify(); overlay.ShowNotice(Status); return; }
        if (offer.NextChoiceNode != null)
        { ConfirmNextSourceChoice(wait, offer.NextChoiceNode); return; }
        if (offer.ActualAnswerNode is not { } answer)
        {
            // 未核后续只提供受本次票据约束的人工目录入口，不把确认选项当作自动续播许可。
            CancelSourceChoiceWait();
            if (SourceChoiceDisplayValid(wait))
            {
                overlay.ShowBrowsePage(OverlayBrowsePage.Lines);
                Status = offer.Reason + (wait.ResumeMode == FollowResumeMode.ClickFollow
                    ? " 请在台词目录核对游戏当前对白；点按跟随保持暂停。" : " 请在台词目录核对游戏当前对白；自动播放保持暂停。"); Notify();
            }
            return;
        }
        if (!CanAutoPlayLine(engine.Pack, answer))
        { CancelSourceChoiceWait(); Status = "这句没有可用配音，尚未续播或点击游戏，请核对当前台词。"; Notify(); overlay.ShowNotice(Status); return; }
        var snapshot = engine.ExportNavigation(); var probe = new PlaybackEngine(engine.Pack);
        if (!probe.ImportNavigation(snapshot) || !probe.ConfirmGameLine(answer.Id) || !CanResumeConfirmedRoute(probe, wait.ResumeMode, out reason) ||
            !probe.Heard.SetEquals(snapshot.Current.Heard) || !probe.Facts.SetEquals(snapshot.Current.Facts))
        { CancelSourceChoiceWait(); Status = "选后对白的路线或状态尚不能确认，请按游戏画面定位。"; Notify(); overlay.ShowNotice(Status); return; }
        CancelSourceChoiceWait(); Invalidate(); audio.Stop();
        if (!SourceChoiceDisplayValid(wait) || !PrepareConfirmedPlayback(wait.ResumeMode, engine, answer)) return;
        if (!engine.ConfirmGameLine(answer.Id)) { PauseAutoPlayback(engine.NavigationError); return; }
        SectionId = answer.SectionId;
        FinishConfirmedPlayback(wait.ResumeMode);
        Diagnostics.Log(wait.ResumeMode == FollowResumeMode.ClickFollow ? "单项选择后恢复点按跟随" : "单项选择后自动续播", wait.Offer.OptionLineNode.Id + " → " + answer.Id + "；无游戏点击");
        ContentChanged?.Invoke(); Notify();
    }

    void ConfirmNextSourceChoice(SourceChoiceWait wait, Node next)
    {
        var engine = wait.Engine; var before = engine.ExportNavigation();
        var probe = new PlaybackEngine(engine.Pack);
        if (!probe.ImportNavigation(before) || !probe.ConfirmGameLine(next.Id))
        { CancelSourceChoiceWait(); Status = "下一处选项尚不能确认，请按游戏画面定位。"; Notify(); overlay.ShowNotice(Status); return; }
        var positioned = probe.ExportNavigation();
        int activeVisits = before.Current.HistoryPosition + 1;
        // ConfirmGameLine 只在克隆中校验；删除它新记的选项台词，真实会话仅静音定位到下一处待选。
        if (positioned.Visits.Count != activeVisits + 1 || positioned.Current.HistoryPosition != activeVisits ||
            positioned.Visits[^1].NodeId != next.Id || positioned.Choices.Count != before.Current.ChoiceCursor ||
            !positioned.Current.Facts.SetEquals(before.Current.Facts) || !positioned.Current.Heard.SetEquals(before.Current.Heard) ||
            !positioned.Current.Choices.OrderBy(p => p.Key).SequenceEqual(before.Current.Choices.OrderBy(p => p.Key)))
        { CancelSourceChoiceWait(); Status = "下一处选项的路线或记录已变化，请重新核对。"; Notify(); overlay.ShowNotice(Status); return; }
        positioned.Visits.RemoveAt(positioned.Visits.Count - 1);
        positioned.Current.HistoryPosition = before.Current.HistoryPosition;
        positioned.Current.Mode = RunMode.Paused;
        if (!probe.ValidateNavigation(positioned) || !probe.ImportNavigation(positioned) ||
            !SourceChoicePolicy.TryAt(probe, out var nextOffer) || nextOffer == null || nextOffer.OptionLineNode.Id != next.Id)
        { CancelSourceChoiceWait(); Status = "下一处选项尚不能等待确认，请按游戏画面定位。"; Notify(); overlay.ShowNotice(Status); return; }
        CancelSourceChoiceWait(); Invalidate(); audio.Stop();
        if (!SourceChoiceDisplayValid(wait)) return;
        if (!engine.ImportNavigation(positioned)) { Status = engine.NavigationError; Notify(); overlay.ShowNotice(Status); return; }
        BeginSourceChoiceWait(engine, nextOffer, wait.Target, wait.DisplayKey, wait.ResumeMode);
        Diagnostics.Log("等待下一处单项选择", wait.Offer.OptionLineNode.Id + " → " + next.Id + "；无播放、无游戏点击");
        ContentChanged?.Invoke(); Notify();
    }

    bool TryAdvanceAutoIntoSourceChoice(PlaybackEngine engine, long epoch)
    {
        if (!Settings.AutoPlayConfirmBranch || autoPlay.Epoch != epoch || !autoPlay.Running || engine.CurrentId != autoPlay.NodeId ||
            autoTarget is not { } target || autoDisplay.RegionKey is not { } key ||
            !SourceChoicePolicy.TryNext(engine, out var offer, out _) || offer == null) return false;
        if (Settings.AdvanceTargetFor(key) != target || overlay.ContainsPoint(target.CenterX, target.CenterY))
        { PauseAutoPlayback("下一句区域已变化或被悬浮控制挡住，尚未点击游戏。请核对后继续。"); return true; }
        if (!autoPlay.BeginTap(epoch, offer.OptionLineNode.Id, SystemClock.ElapsedRealtime()))
        { PauseAutoPlayback("选项推进请求已变化，尚未点击游戏。"); return true; }
        var advance = new SourceChoiceAdvance(engine, offer, epoch, autoPlay.TapTicket,
            JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options), target, key);
        sourceChoiceAdvance = advance;
        _ = AdvanceIntoSourceChoiceOnceAsync(advance, autoServiceToken);
        return true;
    }

    async Task AdvanceIntoSourceChoiceOnceAsync(SourceChoiceAdvance advance, long token)
    {
        bool Current() => !advance.Invalidated && ReferenceEquals(sourceChoiceAdvance, advance) &&
            ReferenceEquals(Engine, advance.Engine) && autoPlay.Running && autoPlay.Epoch == advance.Epoch &&
            advance.Engine.CurrentId == autoPlay.NodeId &&
            JsonSerializer.Serialize(advance.Engine.ExportNavigation(), Json.Options) == advance.Navigation;
        try
        {
            var result = await GameAdvanceAccessibilityService.TapAsync(token, advance.Target.CenterX, advance.Target.CenterY);
            Post(() =>
            {
                if (!Current() || !CheckAutoPlaybackEnvironment()) return;
                if (!result.Completed) { PauseAutoPlayback(result.Reason + " 不会重复补点，请核对选项画面。"); return; }
                if (!autoPlay.GestureCompleted(advance.Epoch, advance.Ticket, advance.Offer.OptionLineNode.Id, SystemClock.ElapsedRealtime())) return;
                main.PostDelayed(() => Post(() =>
                {
                    if (!Current() || !CheckAutoPlaybackEnvironment()) return;
                    if (!SourceChoicePolicy.TryNext(advance.Engine, out var refreshed, out _) || refreshed == null ||
                        refreshed.OptionLineNode.Id != advance.Offer.OptionLineNode.Id ||
                        refreshed.SectionContract != advance.Offer.SectionContract || refreshed.SourceIdentity != advance.Offer.SourceIdentity)
                    { PauseAutoPlayback("游戏选项或路线已变化，请按画面核对；不会再次点击。"); return; }
                    // 游戏进入了菜单，真实导航仍停在前句，不把选项复述冒充已经播放。
                    BeginSourceChoiceWait(advance.Engine, refreshed, advance.Target, advance.DisplayKey);
                    Diagnostics.Advance();
                }), 450);
            });
        }
        catch (Exception ex) { Post(() => { if (Current()) PauseAutoPlayback("选项推进未确认：" + ex.Message + " 不会重复补点。"); }); }
    }

    bool TryWaitAtSourceChoicePreview(PlaybackEngine engine, PlaybackEngine probe, AdvanceTapTarget target, string displayKey)
    {
        if (!Settings.AutoPlayConfirmBranch || !SourceChoicePolicy.TryAt(probe, out var offer) || offer == null) return false;
        var original = engine.ExportNavigation(); var positioned = probe.ExportNavigation();
        // 手动下一句的克隆可能刚记录了 option line。只删除这次新建的一条，旧履历回退不改历史。
        int activeVisits = original.Current.HistoryPosition + 1;
        if (engine.CurrentId != offer.OptionLineNode.Id && positioned.Current.HistoryPosition == activeVisits &&
            positioned.Visits.Count == activeVisits + 1 && positioned.Visits[^1].NodeId == offer.OptionLineNode.Id)
        {
            positioned.Visits.RemoveAt(positioned.Visits.Count - 1);
            positioned.Current.HistoryPosition = original.Current.HistoryPosition;
            positioned.Current.Facts = new(original.Current.Facts); positioned.Current.Heard = new(original.Current.Heard);
        }
        if (!probe.ValidateNavigation(positioned)) { PauseAutoPlayback("选项位置尚不能恢复，请按游戏画面核对。"); return true; }
        Invalidate(); audio.Stop();
        if (!engine.ImportNavigation(positioned)) { PauseAutoPlayback(engine.NavigationError); return true; }
        if (!SourceChoicePolicy.TryAt(engine, out var current) || current == null)
        { PauseAutoPlayback("选项位置已变化，请按游戏画面重新核对。"); return true; }
        BeginSourceChoiceWait(engine, current, target, displayKey);
        return true;
    }
}
