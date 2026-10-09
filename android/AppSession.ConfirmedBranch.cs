using Android.OS;
using PgrVoice.AndroidApp.Platform;
using System.Text.Json;

namespace PgrVoice.AndroidApp;

public sealed partial class AppSession
{
    sealed record ConfirmedBranchWait(PlaybackEngine Engine, Node Menu, ConfirmedBranchOffer Offer,
        long Epoch, long AudioGeneration, string Navigation, string GamePackage, AdvanceTapTarget Target,
        string DisplayKey, ScreenDisplayState Display, long CaptureSession, bool CaptureActive, FollowResumeMode ResumeMode)
    {
        public volatile bool Invalidated;
        public bool Submitted;
    }
    ConfirmedBranchWait? confirmedBranchWait;
    long confirmedBranchServiceToken;
    Action? confirmedBranchWatchdog;
    public bool ConfirmedBranchWaiting => confirmedBranchWait != null;

    void CancelConfirmedBranchWait()
    {
        // 先清对象，取消服务所产生的同步事件不能再抓到旧等待，也不能撤销下一轮。
        confirmedBranchWait = null;
        bool hadSession = confirmedBranchServiceToken != 0;
        confirmedBranchServiceToken = 0;
        if (confirmedBranchWatchdog != null) main.RemoveCallbacks(confirmedBranchWatchdog);
        confirmedBranchWatchdog = null;
        overlay.ClearManualBranchSelection();
        if (hadSession) GameAdvanceAccessibilityService.CancelSession("台词确认等待已结束。");
    }

    Action CaptureConfirmedBranchInvalidation(string message)
    {
        var wait = confirmedBranchWait;
        if (wait != null) wait.Invalidated = true;
        return () =>
        {
            if (wait == null || !ReferenceEquals(confirmedBranchWait, wait)) return;
            CancelConfirmedBranchWait();
            Status = message + " 本次台词确认已取消，请重新核对。"; Notify();
            if (overlay.CanShow) overlay.ShowNotice(Status);
        };
    }

    bool ConfirmedBranchEnvironmentValid(ConfirmedBranchWait wait)
    {
        if (wait.Invalidated || confirmedBranchServiceToken == 0 || !FollowConfirmationEnabled(wait.ResumeMode) ||
            !ReferenceEquals(Engine, wait.Engine) || !ReferenceEquals(wait.Engine.Current, wait.Menu) ||
            wait.Engine.Mode != RunMode.Choice || ocrEpoch != wait.Epoch || playGeneration != wait.AudioGeneration ||
            JsonSerializer.Serialize(wait.Engine.ExportNavigation(), Json.Options) != wait.Navigation ||
            !ConfirmedBranchDisplayValid(wait)) return false;
        return true;
    }

    bool ConfirmedBranchDisplayValid(ConfirmedBranchWait wait)
    {
        if (wait.Invalidated || !FollowConfirmationEnabled(wait.ResumeMode) || uiVisible || !overlay.CanShow || AutoGamePackage != wait.GamePackage ||
            Screen.DisplayState != wait.Display || Screen.SessionId != wait.CaptureSession ||
            Screen.CaptureActive != wait.CaptureActive || (wait.ResumeMode == FollowResumeMode.AutoPlayback && !Screen.CaptureActive) ||
            Settings.AdvanceTargetFor(wait.DisplayKey) != wait.Target) return false;
        var state = GameAdvanceAccessibilityService.GetAvailability(wait.GamePackage);
        return state.CanTap && state.DisplayKey == wait.DisplayKey && MatchesTarget(wait.Target, state);
    }

    bool BeginConfirmedBranchWait(PlaybackEngine engine, AdvanceTapTarget target, string displayKey,
        FollowResumeMode resumeMode = FollowResumeMode.AutoPlayback)
    {
        if (!FollowConfirmationEnabled(resumeMode) || engine.Current is not { Kind: "choice" } menu || engine.Mode != RunMode.Choice)
            return false;
        var offer = ConfirmedBranchPolicy.Create(engine);
        Invalidate(); audio.Stop(); engine.PauseForBrowse();
        if (offer.Cards.Count == 0)
        {
            Status = string.IsNullOrWhiteSpace(offer.Reason) ? "这处选项的后续台词尚不能确认，请按游戏画面手动定位。" : offer.Reason;
            Notify(); overlay.ShowNotice(Status); return true;
        }
        var wait = new ConfirmedBranchWait(engine, menu, offer, ocrEpoch, playGeneration,
            JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options), AutoGamePackage, target, displayKey,
            Screen.DisplayState, Screen.SessionId, Screen.CaptureActive, resumeMode);
        if (!ConfirmedBranchDisplayValid(wait))
        { Status = "游戏、屏幕或下一句区域已变化，请重新核对当前位置。"; Notify(); overlay.ShowNotice(Status); return true; }
        confirmedBranchWait = wait;
        confirmedBranchServiceToken = GameAdvanceAccessibilityService.BeginSession(wait.GamePackage,
            target.DisplayWidth, target.DisplayHeight, target.Rotation);
        if (confirmedBranchServiceToken == 0)
        { CancelConfirmedBranchWait(); Status = GameAdvanceAccessibilityService.LastFailureReason; Notify(); overlay.ShowNotice(Status); return true; }
        confirmedBranchWatchdog = () =>
        {
            if (!ReferenceEquals(confirmedBranchWait, wait)) return;
            if (!ConfirmedBranchEnvironmentValid(wait))
            { CancelConfirmedBranchWait(); Status = "确认期间游戏或位置已变化，请重新核对当前台词。"; Notify(); overlay.ShowNotice(Status); return; }
            if (confirmedBranchWatchdog != null) main.PostDelayed(confirmedBranchWatchdog, 400);
        };
        main.PostDelayed(confirmedBranchWatchdog, 400);
        overlay.SetCaptureHidden(false); overlay.SetExpanded(false);
        Status = (resumeMode == FollowResumeMode.ClickFollow
            ? "请先在游戏中选好，再点对应的当前台词；确认后播放这句并恢复点按跟随。"
            : "请先在游戏中选好，再点对应的当前台词；确认后自动继续配音。") +
            (string.IsNullOrWhiteSpace(offer.Reason) ? "" : "\n" + offer.Reason); Notify();
        var labels = offer.Cards.Select(card => "本处选项 " + card.OptionNumber + "：" + card.OptionLabel +
            (offer.Cards.Count(c => c.OptionId == card.OptionId) > 1 ? " · 候选第" + card.Position + "句" : "") + "\n" +
            (string.IsNullOrWhiteSpace(card.Speaker) ? "旁白" : card.Speaker) + "：" + card.Text +
            (string.IsNullOrWhiteSpace(card.PreviewText) ? "" : "\n再下一句：" +
                (string.IsNullOrWhiteSpace(card.PreviewSpeaker) ? "旁白" : card.PreviewSpeaker) + "：" + card.PreviewText + "（仅供核对）") +
            StoryReferenceDetails(engine.Pack, menu, engine.Pack.ById.GetValueOrDefault(card.NodeId), card.OptionNumber, card.OptionId)).ToArray();
        ShowFollowDialogueCards(resumeMode, BrowseSectionTitle(engine, menu.SectionId) +
            (string.IsNullOrWhiteSpace(offer.Reason) ? "" : "\n" + offer.Reason), labels,
            index =>
            {
                if (!ReferenceEquals(confirmedBranchWait, wait) || wait.Submitted) return;
                if (index < 0 || index >= offer.Cards.Count)
                { CancelConfirmedBranchWait(); Status = "台词卡已变化，请重新核对。"; Notify(); return; }
                wait.Submitted = true;
                string cardId = offer.Cards[index].Id;
                // 关闭卡片并等待游戏焦点恢复期间，原前台监听与失焦标记继续有效。
                main.PostDelayed(() => Post(() => ConfirmBranchDialogue(wait, cardId)), 180);
            }, () => Post(() =>
            {
                if (!ReferenceEquals(confirmedBranchWait, wait)) return;
                CancelConfirmedBranchWait(); Status = "已取消台词确认，配音保持暂停。"; Notify();
            }));
        Diagnostics.Log(resumeMode == FollowResumeMode.ClickFollow ? "点按跟随等待台词确认" : "自动播放等待台词确认", menu.Id + "；不识别、不代点游戏选项");
        return true;
    }

    void ConfirmBranchDialogue(ConfirmedBranchWait wait, string cardId)
    {
        if (!ReferenceEquals(confirmedBranchWait, wait) || !wait.Submitted) return;
        if (!ConfirmedBranchEnvironmentValid(wait))
        { CancelConfirmedBranchWait(); Status = "确认期间位置或游戏画面已变化，请重新核对。"; Notify(); return; }
        var engine = wait.Engine;
        if (!ConfirmedBranchPolicy.TryResolve(engine, wait.Offer, cardId, out var option, out var node, out var reason) ||
            option == null || node == null)
        { CancelConfirmedBranchWait(); Status = reason; Notify(); overlay.ShowNotice(Status); return; }
        if (!CanAutoPlayLine(engine.Pack, node))
        { CancelConfirmedBranchWait(); Status = "这句没有可用配音，尚未续播或点击游戏，请核对当前台词。"; Notify(); overlay.ShowNotice(Status); return; }
        if (!TryBuildConfirmedBranchSelection(engine, wait.Menu, option, node, out var selection, out var probe, out reason) ||
            selection == null || probe == null || !CanResumeConfirmedRoute(probe, wait.ResumeMode, out reason))
        { CancelConfirmedBranchWait(); Status = reason; Notify(); overlay.ShowNotice(Status); return; }
        CancelConfirmedBranchWait();
        Invalidate(); audio.Stop();
        if (!ConfirmedBranchDisplayValid(wait)) return;
        // 中间快照只有明确选择，没有被跳过句子的播放/履历/已听事实。导入本身保持静音。
        if (!PrepareConfirmedPlayback(wait.ResumeMode, engine, node, selection)) return;
        if (!engine.ConfirmGameLine(node.Id)) { PauseAutoPlayback(engine.NavigationError); return; }
        SectionId = node.SectionId;
        FinishConfirmedPlayback(wait.ResumeMode);
        Diagnostics.Log(wait.ResumeMode == FollowResumeMode.ClickFollow ? "确认游戏台词后恢复点按跟随" : "确认游戏台词后自动续播", wait.Menu.Id + " / " + option.Id + " → " + node.Id + "；无游戏点击");
        ContentChanged?.Invoke(); Notify();
    }

    static bool TryBuildConfirmedBranchSelection(PlaybackEngine engine, Node menu, ChoiceOption option, Node node,
        out NavigationSnapshot? selection, out PlaybackEngine? preview, out string reason)
    {
        selection = null; preview = null; reason = "当前选择的路线无法可靠恢复，请重新核对台词。";
        var probe = new PlaybackEngine(engine.Pack);
        if (!probe.ImportNavigation(engine.ExportNavigation())) return false;
        probe.Commit(menu.Id);
        var beforeChoice = probe.ExportNavigation();
        int index = probe.AvailableOptions.FindIndex(o => o.Id == option.Id);
        if (index < 0) return false;
        probe.SelectBranch(index);
        if (probe.CurrentId != option.TargetId || probe.Mode != RunMode.Following ||
            probe.Choices.GetValueOrDefault(menu.Id) != option.PathId) return false;
        var plan = probe.ExportNavigation();
        int activeVisits = beforeChoice.Current.HistoryPosition + 1;
        int activeChoices = beforeChoice.Current.ChoiceCursor;
        if (plan.Visits.Count != activeVisits + 1 || plan.Visits[^1].NodeId != option.TargetId ||
            plan.Choices.Count != activeChoices + 1 || plan.Choices[^1].MenuId != menu.Id ||
            plan.Choices[^1].OptionId != option.Id) return false;
        plan.Visits.RemoveAt(plan.Visits.Count - 1);
        plan.Current.NodeId = menu.Id; plan.Current.Mode = RunMode.Choice;
        plan.Current.HistoryPosition = beforeChoice.Current.HistoryPosition;
        plan.Current.PendingMenu = menu.Id; plan.Current.SingleLine = false; plan.Current.ReviewRoute = null;
        plan.Current.Facts = new(beforeChoice.Current.Facts); plan.Current.Heard = new(beforeChoice.Current.Heard);
        if (!probe.ValidateNavigation(plan)) return false;
        var check = new PlaybackEngine(engine.Pack);
        if (!check.ImportNavigation(plan) || !check.ConfirmGameLine(node.Id) || check.CurrentId != node.Id ||
            check.Mode != RunMode.Following || check.ReviewRoute != null || check.ExportNavigation().Current.SingleLine ||
            check.Choices.GetValueOrDefault(menu.Id) != option.PathId || !check.Facts.SetEquals(beforeChoice.Current.Facts) ||
            !check.Heard.SetEquals(beforeChoice.Current.Heard)) return false;
        selection = plan; preview = check; reason = ""; return true;
    }

    static string StoryReferenceDetails(Pack pack, Node choice, Node? answer, int number, string optionId = "")
    {
        var details = new List<string> { "", "", "剧情位置（只读资料）" };
        try
        {
            if (BundledStoryReference.TryGetNode(pack, choice, out var reference))
            {
                if (!string.IsNullOrWhiteSpace(reference.LocationText)) details.Add(reference.LocationText);
                var option = reference.Options.FirstOrDefault(o => o.Number == number &&
                    (string.IsNullOrWhiteSpace(optionId) || string.IsNullOrWhiteSpace(o.OptionId) || o.OptionId == optionId));
                if (option != null)
                {
                    Add("选择节点", option.SourceMenuAction); Add("本处选项 " + number, option.Label);
                    Add("原目标节点", option.TargetAction); Add("首个可见节点", option.FirstVisibleAction);
                    if (!string.IsNullOrWhiteSpace(option.FirstVisibleText))
                        details.Add("选后内容：" + (string.IsNullOrWhiteSpace(option.FirstVisibleSpeaker) ? "" : option.FirstVisibleSpeaker + "：") + option.FirstVisibleText);
                    Add("汇合位置", option.MergeAction); Add("条件记录", option.ConditionRaw); Add("衔接说明", option.StopReason);
                }
                else if (reference.Options.Count > 0) details.Add("这张卡的选项尚未与内置资料逐项对应。请查看当前小节完整文本。");
                else if (!string.IsNullOrWhiteSpace(reference.DetailText)) details.Add(reference.DetailText);
                else details.Add("暂无这处选项的来源说明。");
            }
            else details.Add("当前配音包的这处选项尚未与内置资料对应。请查看当前小节完整文本。");
            if (answer != null && BundledStoryReference.TryGetNode(pack, answer, out var dialogue) &&
                !string.IsNullOrWhiteSpace(dialogue.LocationText)) details.Add("当前对白位置：" + dialogue.LocationText);
            return string.Join("\n", details);
        }
        catch (Exception) { return "\n\n剧情位置（只读资料）\n资料暂不可读取；可稍后查看当前小节完整文本。"; }
        void Add(string label, string value) { if (!string.IsNullOrWhiteSpace(value)) details.Add(label + "：" + value); }
    }
}
