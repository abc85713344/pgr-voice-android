using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Display;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using PgrVoice.AndroidApp.Ui;

namespace PgrVoice.AndroidApp.Platform;

public enum OverlayCommand { Pause, Replay, Previous, Next, Branch, Original, Ocr, OpenApp, History, Chapters, Hide, AutoPlay, AdvanceRegion, ConfirmPosition, ClickFollow, Feedback }

/// <summary>Owns only our controls; it never intercepts or injects touches outside its window.</summary>
public sealed partial class OverlayController : IDisposable
{
    private readonly Context context;
    private readonly IWindowManager windows;
    private readonly Handler main = new(Looper.MainLooper!);
    private float density;
    private float uiScale = 1;
    private float displayFontScale = 1;
    private int displayWidth, displayHeight;
    private readonly DisplayManager? displays;
    private readonly DisplayListener displayListener;
    private readonly ConfigurationListener configurationListener;
    private LinearLayout? root;
    private LinearLayout? panel;
    private LinearLayout? controlPanel, browsePanel, browseRows;
    private LinearLayout? browseShell;
    private TextView? browseHeading;
    private LinearLayout? compactPanel;
    private LinearLayout? compactActions;
    private FrameLayout? captureStopControl;
    private readonly CaptureCancelGestureGate captureCancelGesture = new();
    private ScrollView? browseScroll;
    private Button? browsePrevious, browseNext, browseCurrent;
    private readonly List<Button> browseTabs = new();
    private readonly List<Button> browseSideTabs = new();
    private OverlayBrowsePage? browsePage;
    private OverlayBrowseModel? browseModel;
    private int browsePageIndex;
    private int browseRenderSerial;
    private bool browseHintExpanded;
    private const int BrowsePageSize = 12;
    private Dialog? confirmationDialog;
    private ContextThemeWrapper? confirmationTheme;
    private Action? confirmationCancelled;
    private bool confirmationIsManualBranch;
    private bool confirmationIsPauseNotice;
    private bool confirmationIsAutoStart;
    private ScrollView? actionScroll;
    private LinearLayout? autoPlaybackPausedCard;
    private string autoPlaybackPausedReason = "";
    private long autoPlaybackPausedRevision;
    private TextView? statusView;
    private TextView? playbackModeView;
    private TextView? handleView;
    private TextView? currentLineView;
    private Button? pauseButton;
    private Button? autoPlayButton;
    private Button? clickFollowButton;
    private Button? compactControlsButton;
    private Button? compactButtonsButton;
    private WindowManagerLayoutParams? parameters;
    private bool showing, expanded, disposed, captureHidden, captureKeepStopControl;
    private bool playbackPaused;
    private bool autoPlaying;
    private bool clickFollowing;
    private bool compactControlsVisible = true;
    private bool compactButtonsVisible = true;
    private bool appForeground;
    private string status = "请选择章节，再开启配音";
    private string currentSpeaker = "", currentText = "";
    public event Action<OverlayCommand>? Command;
    public event Action<OverlayBrowsePage>? BrowseRequested;
    public event Action? BrowseRefreshRequested;
    public event Action<long, string>? BrowseItemSelected;
    public event Action<bool>? CompactButtonsChanged;
    public event Action<bool>? CompactControlsChanged;
    public event Action<long>? CaptureCancelRequested;
    public event Action<string>? CaptureInteraction;
    public event Action<string>? Error;
    public bool IsShowing => showing;
    public long CurrentCaptureControlGeneration => captureCancelGesture.Generation;
    public bool CompactControlsVisible => compactControlsVisible;
    public bool CompactButtonsVisible => compactButtonsVisible;
    public bool IsBrowseVisible => showing && expanded && browsePage.HasValue;
    public OverlayBrowsePage? CurrentBrowsePage => browsePage;
    public bool CanShow => Settings.CanDrawOverlays(context);

    public OverlayController(Context context)
    {
        this.context = context.ApplicationContext!;
        windows = this.context.GetSystemService(Context.WindowService)!.JavaCast<IWindowManager>();
        density = this.context.Resources!.DisplayMetrics!.Density;
        displays = this.context.GetSystemService(Context.DisplayService) as DisplayManager;
        displayListener = new DisplayListener(this);
        configurationListener = new ConfigurationListener(this);
        displays?.RegisterDisplayListener(displayListener, main);
        this.context.RegisterComponentCallbacks(configurationListener);
        RefreshDisplay();
    }

    public void Show() => OnMain(() =>
    {
        if (disposed || showing) return;
        if (!CanShow) { Error?.Invoke("请先允许“显示在其他应用上层”，再开启悬浮控制。"); return; }
        try
        {
            RefreshDisplay();
            BuildView();
            ApplyCaptureVisibility();
            windows.AddView(root, parameters);
            showing = true;
            ClampPosition();
        }
        catch (Exception ex) { showing = false; Error?.Invoke("无法显示悬浮控制：" + ex.Message); }
    });

    public void Hide() => OnMain(() => HideCore());

    /// <summary>
    /// 截图期间隐藏正文，按需保留纯图形取消入口；保留页、展开状态、位置与用户偏好。
    /// 恢复仅恢复原来的可见性，不主动显示原本已关闭的窗口或展开面板。
    /// </summary>
    public void SetCaptureHidden(bool hidden, bool keepStopControl = false) => OnMain(() =>
    {
        if (disposed) return;
        captureHidden = hidden;
        if (!hidden) ClearDefaultBranchWait();
        captureKeepStopControl = hidden && keepStopControl;
        if (CaptureStopVisible)
        {
            long generation = captureCancelGesture.Begin();
            CaptureInteraction?.Invoke($"generation={generation}; phase=begin; uptime={SystemClock.UptimeMillis()}");
            captureStopControl?.Invalidate();
        }
        else captureCancelGesture.End();
        if (hidden) DismissConfirmation();
        RefreshPresentation();
        ClampPosition();
    });

    private bool CaptureStopVisible => captureHidden && captureKeepStopControl;

    private void ApplyCaptureVisibility()
    {
        // Invisible 保留列表测量和滚动位置；NotTouchable 防止隐藏期间透明窗口挡住游戏。
        bool fullyHidden = captureHidden && !captureKeepStopControl;
        if (root != null) root.Visibility = fullyHidden ? ViewStates.Invisible : ViewStates.Visible;
        if (parameters != null)
        {
            if (fullyHidden) parameters.Flags |= WindowManagerFlags.NotTouchable;
            else parameters.Flags &= ~WindowManagerFlags.NotTouchable;
        }
    }

    public void ShowBrowsePage(OverlayBrowsePage page) => OnMain(() =>
    {
        if (disposed) return;
        browsePage = page;
        browseModel = null;
        browsePageIndex = 0;
        browseHintExpanded = false;
        Show();
        if (!showing) return;
        SetExpandedCore(true, refreshBrowse: false);
        // 先让会话接管有效的分支等待，再关闭旧提示；关闭回调不能抢先丢掉原跟随意图。
        BrowseRequested?.Invoke(page);
        DismissConfirmation();
        RenderBrowse();
    });

    public void ShowControlPage() => OnMain(() =>
    {
        if (disposed) return;
        DismissConfirmation();
        browsePage = null;
        browseModel = null;
        Show();
        SetExpanded(true);
        actionScroll?.ScrollTo(0, 0);
    });

    public void UpdateBrowse(OverlayBrowseModel model) => OnMain(() =>
    {
        if (disposed || browsePage != model.Page) return;
        bool changed = browseModel?.Revision != model.Revision;
        bool newSection = browseModel == null || browseModel.Title != model.Title;
        bool dialogueList = model.Page == OverlayBrowsePage.Lines || model.Items.Any(i => i.IsDialogue);
        bool changedLine = dialogueList && browseModel?.Items.FirstOrDefault(i => i.Selected)?.Title != model.Items.FirstOrDefault(i => i.Selected)?.Title;
        if (changed) DismissConfirmation();
        browseModel = model;
        if (newSection || changedLine) browsePageIndex = dialogueList ? SelectedBrowsePage(model) : 0;
        if (newSection) browseHintExpanded = false;
        RenderBrowse(newSection || changedLine);
    });

    private static int SelectedBrowsePage(OverlayBrowseModel model)
    {
        for (int i = 0; i < model.Items.Count; i++) if (model.Items[i].Selected) return i / BrowsePageSize;
        return 0;
    }

    private void RenderBrowse(bool positionContent = true, bool preferSelected = true)
    {
        if (browseRows == null || !browsePage.HasValue) return;
        int serial = ++browseRenderSerial;
        browseRows.RemoveAllViews();
        if (browseModel is not { } model)
        {
            if (browseHeading != null) browseHeading.Text = "读取内容";
            AddBrowseText("正在读取内容…", 14, PgrTheme.Foreground);
            if (browsePrevious != null) browsePrevious.Enabled = false;
            if (browseNext != null) browseNext.Enabled = false;
            if (browseCurrent != null) { browseCurrent.Text = "等待内容"; browseCurrent.Enabled = false; }
            return;
        }
        int pageCount = Math.Max(1, (model.Items.Count + BrowsePageSize - 1) / BrowsePageSize);
        browsePageIndex = Math.Clamp(browsePageIndex, 0, pageCount - 1);
        if (browseHeading != null) browseHeading.Text = model.Title;
        if (!string.IsNullOrWhiteSpace(model.Hint))
        {
            var hint = ReadingButton(browseHintExpanded ? "收起说明 ▴" : model.Page == OverlayBrowsePage.Locate ? "识别原文与当前位置 ▾" : $"{model.Items.Count} 条 · 查看说明 ▾", () =>
            { browseHintExpanded = !browseHintExpanded; RenderBrowse(false); });
            hint.Gravity = GravityFlags.Left | GravityFlags.CenterVertical;
            browseRows.AddView(hint, new LinearLayout.LayoutParams(-1, ReadDp(28)));
            if (browseHintExpanded) AddBrowseText(model.Hint, 12, PgrTheme.Secondary);
        }
        if (model.Actions != null)
        {
            var actionStrip = new HorizontalScrollView(context) { HorizontalScrollBarEnabled = false };
            var actions = new LinearLayout(context) { Orientation = Orientation.Horizontal };
            foreach (var item in model.Actions)
            {
                var button = ReadingButton(item.Title, () => SelectBrowseItem(model, item));
                button.ContentDescription = item.Title + "，" + item.Detail;
                actions.AddView(button, new LinearLayout.LayoutParams(-2, ReadDp(32)) { RightMargin = ReadDp(5) });
            }
            actionStrip.AddView(actions, new HorizontalScrollView.LayoutParams(-2, -2));
            browseRows.AddView(actionStrip, new LinearLayout.LayoutParams(-1, -2) { BottomMargin = ReadDp(6) });
        }
        if (model.Items.Count == 0) AddBrowseText("当前没有可选条目，可先使用上方操作。", 14, PgrTheme.Secondary);
        View? firstItem = null, selectedItem = null;
        foreach (var item in model.Items.Skip(browsePageIndex * BrowsePageSize).Take(BrowsePageSize))
        {
            var card = AddBrowseItem(model, item);
            firstItem ??= card;
            if (item.Selected) selectedItem = card;
        }
        if (browsePrevious != null) browsePrevious.Enabled = browsePageIndex > 0;
        if (browseNext != null) browseNext.Enabled = browsePageIndex + 1 < pageCount;
        if (browseCurrent != null)
        {
            browseCurrent.Text = model.Items.Any(i => i.Selected) ? $"{browsePageIndex + 1}/{pageCount} · 当前" : $"{browsePageIndex + 1}/{pageCount}";
            browseCurrent.Enabled = model.Items.Any(i => i.Selected);
        }
        if (positionContent) ScrollBrowseAfterLayout(model.Page == OverlayBrowsePage.Lines || model.Items.Any(i => i.IsDialogue) ? (preferSelected ? selectedItem ?? firstItem : firstItem) : null, serial);
    }

    private void ScrollBrowseAfterLayout(View? target, int serial)
    {
        var scroll = browseScroll; var rows = browseRows;
        if (scroll == null || rows == null) return;
        var observer = rows.ViewTreeObserver;
        EventHandler? laidOut = null;
        bool completed = false;
        void Apply()
        {
            if (completed) return;
            completed = true;
            var liveObserver = observer?.IsAlive == true ? observer : rows.ViewTreeObserver;
            if (liveObserver?.IsAlive == true && laidOut != null) liveObserver.GlobalLayout -= laidOut;
            if (!disposed && serial == browseRenderSerial && ReferenceEquals(rows, browseRows)) scroll.ScrollTo(0, target?.Top ?? 0);
        }
        laidOut = (_, _) => Apply();
        if (observer != null) observer.GlobalLayout += laidOut;
        scroll.Post(() => { if (target == null || target.IsLaidOut) Apply(); });
    }

    private void AddBrowseText(string text, int size, Color color)
    {
        var view = new TextView(context) { Text = text, TextSize = size };
        view.SetTextColor(color);
        view.SetPadding(ReadDp(3), ReadDp(3), ReadDp(3), ReadDp(7));
        browseRows!.AddView(view, new LinearLayout.LayoutParams(-1, -2));
    }

    private View AddBrowseItem(OverlayBrowseModel model, OverlayBrowseItem item)
    {
        var card = new LinearLayout(context) { Orientation = Orientation.Vertical, Clickable = true, Focusable = true, Selected = item.Selected,
            ContentDescription = (item.Selected ? "当前项，" : "") + item.Title + "，点击核对详情" };
        card.SetPadding(ReadDp(11), ReadDp(6), ReadDp(11), ReadDp(6));
        card.Background = PgrTheme.Surface(context, item.Selected ? PgrTheme.Selection : PgrTheme.Panel,
            item.Selected ? PgrTheme.Red : PgrTheme.Border, item.Selected ? PgrTheme.Red : null);
        if (model.Page == OverlayBrowsePage.Lines || item.IsDialogue)
        {
            string text = System.Text.RegularExpressions.Regex.Replace(item.Title, @"^\s*\d+\.\s*", "");
            int separator = text.IndexOf('：');
            if (separator < 0) separator = text.IndexOf(':');
            string speaker = separator >= 0 ? text[..separator] : "台词";
            string dialogue = separator >= 0 ? text[(separator + 1)..] : text;
            AddBubbleText(card, (item.Selected ? "当前 · " : "") + speaker, 11, 1, item.Selected ? PgrTheme.Cyan : PgrTheme.Secondary);
            AddBubbleText(card, dialogue, 15, 2, PgrTheme.Foreground);
        }
        else
        {
            AddBubbleText(card, (item.Selected ? "当前 · " : "") + item.Title, 14, 2, PgrTheme.Foreground);
            if (!string.IsNullOrWhiteSpace(item.Detail)) AddBubbleText(card, item.Detail.ReplaceLineEndings(" "), 11, 1, PgrTheme.Secondary);
        }
        card.Click += (_, _) => SelectBrowseItem(model, item);
        browseRows!.AddView(card, new LinearLayout.LayoutParams(-1, -2) { BottomMargin = ReadDp(5) });
        return card;
    }

    private void AddBubbleText(LinearLayout card, string text, int size, int lines, Color color)
    {
        var view = new TextView(context) { Text = text, TextSize = size };
        view.SetTextColor(color); view.SetMaxLines(lines); view.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        card.AddView(view, new LinearLayout.LayoutParams(-1, -2));
    }

    private bool BrowseItemIsCurrent(OverlayBrowseModel model, OverlayBrowseItem item) =>
        !disposed && !captureHidden && IsBrowseVisible && browsePage == model.Page && browseModel is { } current && current.Revision == model.Revision &&
        (current.Items.Any(i => i.Id == item.Id) || current.Actions?.Any(i => i.Id == item.Id) == true);

    private void SelectBrowseItem(OverlayBrowseModel model, OverlayBrowseItem item)
    {
        if (disposed || captureHidden) return;
        if (!BrowseItemIsCurrent(model, item)) { if (browsePage is { } page) ShowBrowsePage(page); return; }
        DismissConfirmation();
        if (!item.RequiresConfirmation) { BrowseItemSelected?.Invoke(model.Revision, item.Id); return; }
        try
        {
            string message = string.IsNullOrWhiteSpace(item.ConfirmationText) ? item.Title + "\n\n" + item.Detail : item.ConfirmationText;
            ShowGameNotification(message, new[] { message }, _ =>
            {
                // Only this consumed notification may confirm a still-current frozen browse model.
                // The reading panel is intentionally collapsed, so its visibility is not a validity condition here.
                bool valid = !disposed && !captureHidden && showing && browsePage == model.Page &&
                    browseModel is { } current && ReferenceEquals(current, model) && current.Revision == model.Revision &&
                    (current.Items.Any(i => i.Id == item.Id) || current.Actions?.Any(i => i.Id == item.Id) == true);
                if (valid) BrowseItemSelected?.Invoke(model.Revision, item.Id);
                else if (browsePage is { } page) ShowBrowsePage(page);
            }, item.ConfirmationTitle, null, null, null, null, null, singleConfirmationLabel: "确认采用", previewText: item.Title);
        }
        catch (Exception ex) { DismissConfirmation(); Error?.Invoke("无法显示确认通知：" + ex.Message); }
    }

    public void ShowConfirmedBranchSelection(string text, IReadOnlyList<string> labels, Action<int> selected, Action cancelled) =>
        ShowManualBranchSelection(text, labels, selected, cancelled,
            title: "请对照游戏确认当前台词",
            instruction: "先在游戏中选择，等对白出现，再点对应卡片确认。将从这句继续自动播放。",
            buttonPrefix: "确认并继续配音：", anchorCards: true, choiceConfirmationLabel: "确认并继续配音", dialogueCards: true);

    public void ShowManualBranchSelection(string text, IReadOnlyList<string> optionLabels, Action<int> selected, Action cancelled,
        string? title = null, string? instruction = null, string? buttonPrefix = null,
        string? secondaryActionLabel = null, Action? secondaryAction = null, bool anchorCards = false,
        string? additionalActionLabel = null, Action? additionalAction = null, string? choiceConfirmationLabel = null,
        bool dialogueCards = false) => OnMain(() =>
    {
        if (disposed || captureHidden) { NotifyConfirmationCancelled(cancelled); return; }
        Show();
        if (!showing) { NotifyConfirmationCancelled(cancelled); return; }
        DismissConfirmation();
        confirmationIsManualBranch = true;
        confirmationCancelled = cancelled;
        try
        {
            ShowGameNotification(text, optionLabels, selected, title ?? "游戏中选择后，再确认同项",
                instruction ?? "先在游戏中选好，再确认相同人物、话题或选项。", secondaryActionLabel,
                secondaryAction, additionalActionLabel, additionalAction, anchorCards: anchorCards,
                choiceActionLabel: choiceConfirmationLabel ?? (anchorCards || buttonPrefix == "确认并开启跟随：" ? "确认并开启点按跟随" : null),
                dialogueCards: dialogueCards);
        }
        catch (Exception ex) { DismissConfirmation(); Error?.Invoke("无法显示分支通知：" + ex.Message); }
    });

    private void ShowGameNotification(string section, IReadOnlyList<string> labels, Action<int> selected,
        string? heading, string? instruction, string? optionActionLabel, Action? optionAction,
        string? ocrActionLabel, Action? ocrAction, bool anchorCards = false, string? singleConfirmationLabel = null, string? previewText = null,
        string? choiceActionLabel = null, bool dialogueCards = false)
    {
        // Collapse only the old reading/control surface. Do not call SetExpanded(false),
        // because that would cancel the notification callback installed by our caller.
        expanded = false;
        RefreshPresentation(); UpdateSize(); UpdateLayout(); ClampPosition();
        var themed = new ContextThemeWrapper(context, global::Android.Resource.Style.ThemeMaterialNoActionBar);
        confirmationTheme = themed;
        var safe = ScreenArea();
        int width = safe.Right - safe.Left;
        float fontScale = Math.Max(1f, context.Resources?.Configuration?.FontScale ?? 1f);
        int barHeight = ReadDp(48);
        int rowHeight = ReadDp(dialogueCards ? Math.Max(72f, 13f * fontScale * 4.1f + 10f) : Math.Max(52f, 13f * fontScale * 2.6f + 10f));
        int cardRows = singleConfirmationLabel != null ? 1 : dialogueCards ? Math.Clamp((labels.Count + 1) / 2, 1, 2) : 2;
        int fullHeight = barHeight + rowHeight * cardRows + ReadDp(4);
        int compactHeight = ReadDp(Math.Max(64f, 13f * fontScale * 2.6f + 12f));
        var body = new LinearLayout(themed) { Orientation = Orientation.Vertical };
        body.SetPadding(ReadDp(3), ReadDp(2), ReadDp(3), ReadDp(2));
        body.SetBackgroundColor(PgrTheme.Raised);
        Dialog? dialog = null;
        bool collapsed = true;
        bool detailsShown = false;
        int page = 0;
        string expandedTitle = heading ?? "请对照游戏确认";
        string preview = previewText ?? string.Join(" / ", labels.Take(2).Select(value => dialogueCards
            ? string.Join(" ", value.ReplaceLineEndings("\n").Split('\n').Take(2))
            : value.ReplaceLineEndings(" ")));
        string compactTitle = expandedTitle + "\n" + preview;
        bool Current() => !disposed && !captureHidden && dialog != null && ReferenceEquals(confirmationDialog, dialog);
        void ResizeNotice(int height)
        {
            var currentWindow = dialog?.Window;
            if (currentWindow == null) return;
            var bounds = ScreenArea();
            int shownHeight = Math.Min(height, bounds.Bottom - bounds.Top);
            currentWindow.SetLayout(bounds.Right - bounds.Left, shownHeight);
            var position = currentWindow.Attributes!;
            position.X = bounds.Left;
            position.Y = Math.Clamp(position.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - shownHeight));
            currentWindow.Attributes = position;
        }
        Button SmallButton(string label, Action action)
        {
            var button = new Button(themed) { Text = label, TextSize = 12 };
            button.SetAllCaps(false); button.SetTextColor(PgrTheme.Cyan);
            button.SetMinHeight(0); button.SetMinimumHeight(0); button.SetMinWidth(0); button.SetMinimumWidth(0);
            button.SetPadding(ReadDp(4), 0, ReadDp(4), 0); button.SetMaxLines(1);
            button.Click += (_, _) => { if (Current()) action(); };
            return button;
        }
        void Confirm(int choice)
        {
            if (!Current()) return;
            DismissConfirmation(notifyCancellation: false);
            try { selected(choice); }
            catch (Exception ex) { Error?.Invoke("确认未完成：" + ex.Message); }
        }
        void RunAction(Action action)
        {
            if (!Current()) return;
            DismissConfirmation(notifyCancellation: false);
            try { action(); }
            catch (Exception ex) { Error?.Invoke("切换对话未完成：" + ex.Message); }
        }
        var toolbar = new LinearLayout(themed) { Orientation = Orientation.Horizontal };
        var dragTitle = new TextView(themed) { Text = compactTitle, TextSize = 13, Gravity = GravityFlags.CenterVertical };
        dragTitle.SetTextColor(PgrTheme.Foreground); dragTitle.SetMaxLines(2);
        dragTitle.SetPadding(ReadDp(6), 0, ReadDp(6), 0);
        dragTitle.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        toolbar.AddView(dragTitle, new LinearLayout.LayoutParams(0, -1, 1));
        Button? collapseButton = null;
        var content = new LinearLayout(themed) { Orientation = Orientation.Vertical };
        var footer = new LinearLayout(themed) { Orientation = Orientation.Horizontal };
        var toolsScroll = new HorizontalScrollView(themed) { HorizontalScrollBarEnabled = true, FillViewport = false };
        toolsScroll.AddView(footer, new HorizontalScrollView.LayoutParams(-2, -1));
        toolbar.AddView(toolsScroll, new LinearLayout.LayoutParams(0, -1, 1));
        void RefreshExpansion()
        {
            content.Visibility = toolsScroll.Visibility = collapsed ? ViewStates.Gone : ViewStates.Visible;
            collapseButton!.Text = collapsed ? "展开" : "收起";
            dragTitle.LayoutParameters = collapsed ? new LinearLayout.LayoutParams(0, -1, 1) : new LinearLayout.LayoutParams(ReadDp(112), -1);
            dragTitle.SetMaxLines(collapsed ? 2 : 1);
            dragTitle.Text = collapsed ? compactTitle : detailsShown ? "核对完整内容 · 可拖动" : expandedTitle;
            toolbar.LayoutParameters = new LinearLayout.LayoutParams(-1, collapsed ? compactHeight - ReadDp(4) : barHeight);
            ResizeNotice(collapsed ? compactHeight : detailsShown ? Math.Min(ReadDp(280), (int)((safe.Bottom - safe.Top) * .6f)) : fullHeight);
        }
        collapseButton = SmallButton("展开", () =>
        {
            collapsed = !collapsed;
            RefreshExpansion();
        });
        toolbar.AddView(collapseButton, new LinearLayout.LayoutParams(ReadDp(Math.Max(52, 24 * fontScale + 12)), -1));
        toolbar.AddView(SmallButton("取消", () => DismissConfirmation()), new LinearLayout.LayoutParams(ReadDp(Math.Max(48, 24 * fontScale + 12)), -1));
        body.AddView(toolbar, new LinearLayout.LayoutParams(-1, compactHeight - ReadDp(4)));
        body.AddView(content, new LinearLayout.LayoutParams(-1, 0, 1));
        void FooterButton(string label, Action action, bool enabled = true)
        {
            var button = SmallButton(label, action); button.Enabled = enabled;
            footer.AddView(button, new LinearLayout.LayoutParams(ReadDp(Math.Max(48, label.Length * 12 * fontScale + 16)), -1));
        }
        void ShowDetail(int choice)
        {
            if (!Current()) return;
            detailsShown = true;
            content.RemoveAllViews(); footer.RemoveAllViews();
            toolsScroll.ScrollTo(0, 0);
            var scroll = new ScrollView(themed);
            var detail = new TextView(themed) { Text = labels[choice] + (labels[choice] == section ? "" : "\n\n" + section) + "\n" + instruction, TextSize = 15 };
            detail.SetTextColor(PgrTheme.Foreground); detail.SetPadding(ReadDp(8), ReadDp(5), ReadDp(8), ReadDp(5));
            scroll.AddView(detail); content.AddView(scroll, new LinearLayout.LayoutParams(-1, -1));
            FooterButton("返回通知", () => { collapsed = true; RenderCards(); });
            FooterButton(singleConfirmationLabel ?? choiceActionLabel ?? "确认就是这句", () => Confirm(choice));
            RefreshExpansion();
        }
        void RenderCards()
        {
            detailsShown = false;
            content.RemoveAllViews(); footer.RemoveAllViews();
            toolsScroll.ScrollTo(0, 0);
            expandedTitle = (heading ?? "请对照游戏确认") + (labels.Count > 4 ? $" · {page + 1}/{(labels.Count + 3) / 4}" : "");
            int start = page * 4;
            for (int row = 0; row < cardRows; row++)
            {
                var cardRow = new LinearLayout(themed) { Orientation = Orientation.Horizontal };
                content.AddView(cardRow, new LinearLayout.LayoutParams(-1, 0, 1));
                for (int column = 0; column < (singleConfirmationLabel != null ? 1 : 2); column++)
                {
                    int choice = start + row * 2 + column;
                    var cell = new LinearLayout(themed) { Orientation = Orientation.Horizontal };
                    cell.SetPadding(ReadDp(2), ReadDp(1), ReadDp(2), ReadDp(1));
                    cardRow.AddView(cell, new LinearLayout.LayoutParams(0, -1, 1));
                    if (choice >= labels.Count) continue;
                    if (dialogueCards)
                    {
                        // 选项和实际对白各占自己的空间，长选项不能挤掉要核对的对白。
                        var parts = labels[choice].Split('\n', 3);
                        var dialogueCard = new LinearLayout(themed)
                        {
                            Orientation = Orientation.Vertical,
                            Clickable = true, LongClickable = true,
                            ContentDescription = labels[choice] + "。确认并继续配音。长按或点全文核对完整台词。"
                        };
                        dialogueCard.SetGravity(GravityFlags.CenterVertical);
                        dialogueCard.SetPadding(ReadDp(6), ReadDp(3), ReadDp(6), ReadDp(3));
                        var optionText = new TextView(themed) { Text = parts[0], TextSize = 11 };
                        optionText.SetTextColor(PgrTheme.Cyan); optionText.SetMaxLines(1);
                        optionText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                        var dialogueText = new TextView(themed) { Text = parts.Length > 1 ? parts[1] : parts[0], TextSize = 13 };
                        dialogueText.SetTextColor(PgrTheme.Foreground); dialogueText.SetMaxLines(2);
                        dialogueText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                        dialogueCard.AddView(optionText, new LinearLayout.LayoutParams(-1, -2));
                        dialogueCard.AddView(dialogueText, new LinearLayout.LayoutParams(-1, -2));
                        dialogueCard.Click += (_, _) => Confirm(choice);
                        dialogueCard.LongClick += (_, e) => { if (Current()) ShowDetail(choice); e.Handled = true; };
                        cell.AddView(dialogueCard, new LinearLayout.LayoutParams(0, -1, 1));
                        cell.AddView(SmallButton("全文", () => ShowDetail(choice)), new LinearLayout.LayoutParams(ReadDp(48), -1));
                        continue;
                    }
                    var card = SmallButton(labels[choice], () => { if (singleConfirmationLabel != null) ShowDetail(choice); else Confirm(choice); });
                    card.TextSize = 13; card.SetMaxLines(2); card.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
                    card.SetPadding(ReadDp(6), 0, ReadDp(6), 0);
                    card.SetIncludeFontPadding(false);
                    card.Gravity = GravityFlags.CenterVertical | GravityFlags.Left;
                    card.ContentDescription = labels[choice] + (choiceActionLabel == null ? "" : "。" + choiceActionLabel) + "。长按或点全文核对完整台词。";
                    card.LongClick += (_, e) => { if (Current()) ShowDetail(choice); e.Handled = true; };
                    cell.AddView(card, new LinearLayout.LayoutParams(0, -1, 1));
                    cell.AddView(SmallButton("全文", () => ShowDetail(choice)), new LinearLayout.LayoutParams(ReadDp(48), -1));
                }
            }
            if (singleConfirmationLabel != null) FooterButton(singleConfirmationLabel, () => Confirm(0));
            if (optionAction != null) FooterButton(optionActionLabel == "按选项核对" ? "按选项" : optionActionLabel ?? "更多", () => RunAction(optionAction));
            if (ocrAction != null) FooterButton(anchorCards ? "识别下一句" : ocrActionLabel ?? "重新定位", () => RunAction(ocrAction));
            if (labels.Count > 4)
            {
                FooterButton("上一页", () => { page--; RenderCards(); }, page > 0);
                FooterButton("下一页", () => { page++; RenderCards(); }, start + 4 < labels.Count);
            }
            RefreshExpansion();
        }
        RenderCards();
        dialog = new Dialog(themed);
        dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);
        dialog.SetContentView(body);
        confirmationDialog = dialog;
        var window = dialog.Window!;
        window.SetType(WindowManagerTypes.ApplicationOverlay);
        // ScreenArea already excludes system bars and cutouts. Use screen coordinates
        // throughout so a floating Dialog parent does not apply those insets twice.
        window.AddFlags(WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal | WindowManagerFlags.LayoutInScreen);
        var screenCoordinates = window.Attributes!;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            screenCoordinates.FitInsetsTypes = 0;
            screenCoordinates.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.Always;
        }
        else if (OperatingSystem.IsAndroidVersionAtLeast(28))
            screenCoordinates.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.Never;
        window.Attributes = screenCoordinates;
        window.ClearFlags(WindowManagerFlags.DimBehind);
        dialog.SetCanceledOnTouchOutside(false);
        dialog.DismissEvent += (_, _) =>
        {
            if (!ReferenceEquals(confirmationDialog, dialog)) return;
            var onCancelled = confirmationCancelled; confirmationCancelled = null;
            var theme = confirmationTheme; confirmationTheme = null;
            confirmationDialog = null; confirmationIsManualBranch = false; confirmationIsPauseNotice = false; confirmationIsAutoStart = false; dialog.Dispose(); theme?.Dispose();
            NotifyConfirmationCancelled(onCancelled);
        };
        dialog.Show();
        window.SetBackgroundDrawable(new Android.Graphics.Drawables.ColorDrawable(Color.Transparent));
        window.DecorView.SetPadding(0, 0, 0, 0);
        window.DecorView.Elevation = 0;
        window.SetLayout(width, compactHeight);
        window.SetGravity(GravityFlags.Top | GravityFlags.Left);
        var attributes = window.Attributes!;
        attributes.X = safe.Left;
        attributes.Y = safe.Top; window.Attributes = attributes;
        float downX = 0, downY = 0; int originX = 0, originY = 0; bool dragged = false;
        dragTitle.Touch += (_, e) =>
        {
            if (e.Event == null || !Current()) return;
            if (e.Event.ActionMasked == MotionEventActions.Down)
            { downX = e.Event.RawX; downY = e.Event.RawY; originX = window.Attributes!.X; originY = window.Attributes!.Y; dragged = false; }
            else if (e.Event.ActionMasked == MotionEventActions.Move)
            {
                if (Math.Abs(e.Event.RawX - downX) + Math.Abs(e.Event.RawY - downY) > ReadDp(6)) dragged = true;
                if (!dragged) { e.Handled = true; return; }
                var bounds = ScreenArea(); var moved = window.Attributes!;
                moved.X = Math.Clamp(originX + (int)(e.Event.RawX - downX), bounds.Left, Math.Max(bounds.Left, bounds.Right - window.DecorView.Width));
                moved.Y = Math.Clamp(originY + (int)(e.Event.RawY - downY), bounds.Top, Math.Max(bounds.Top, bounds.Bottom - window.DecorView.Height));
                window.Attributes = moved;
            }
            else if (e.Event.ActionMasked == MotionEventActions.Up && !dragged && collapsed)
            { collapsed = false; RefreshExpansion(); }
            e.Handled = true;
        };
    }

    public void ClearManualBranchSelection() => OnMain(() =>
    {
        if (confirmationIsManualBranch) DismissConfirmation(notifyCancellation: false);
    });

    public void ClearAutoStartConfirmation() => OnMain(() =>
    {
        if (confirmationIsAutoStart) DismissConfirmation(notifyCancellation: false);
    });

    public void ShowAutoPlaybackStart(string text, Action confirmed, Action locate, Action cancelled, bool isCommonReturn = false) => OnMain(() =>
    {
        if (disposed || captureHidden) { NotifyConfirmationCancelled(cancelled); return; }
        Show();
        if (!showing) { NotifyConfirmationCancelled(cancelled); return; }
        DismissConfirmation();
        confirmationCancelled = cancelled;
        try
        {
            ShowGameNotification(text, new[] { text }, _ => confirmed(),
                isCommonReturn ? "已到共同线，请核对当前句" : "核对自动播放起点",
                "先核对游戏当前台词；确认后从这句配音，读完再自动点击。缺音或未知连接仍暂停。",
                null, null, "重新 OCR 定位", locate,
                singleConfirmationLabel: isCommonReturn ? "确认继续" : "从这句开始",
                previewText: text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim());
            confirmationIsAutoStart = true;
        }
        catch (Exception ex) { DismissConfirmation(); Error?.Invoke("无法显示起点通知：" + ex.Message); }
    });

    private void DismissConfirmation(bool notifyCancellation = true)
    {
        confirmationIsManualBranch = false;
        confirmationIsPauseNotice = false;
        confirmationIsAutoStart = false;
        var dialog = confirmationDialog; confirmationDialog = null;
        var onCancelled = confirmationCancelled; confirmationCancelled = null;
        var theme = confirmationTheme; confirmationTheme = null;
        if (dialog != null)
        {
            try { dialog.Dismiss(); } catch { }
            dialog.Dispose();
        }
        theme?.Dispose();
        if (notifyCancellation) NotifyConfirmationCancelled(onCancelled);
    }

    private void NotifyConfirmationCancelled(Action? cancelled)
    {
        try { cancelled?.Invoke(); }
        catch (Exception ex) { Error?.Invoke("取消起点核对未完成：" + ex.Message); }
    }

    public void UpdateCurrentLine(string speaker, string text) => OnMain(() =>
    {
        if (disposed) return;
        string nextSpeaker = (speaker ?? "").Trim(), nextText = (text ?? "").Trim();
        if (currentSpeaker == nextSpeaker && currentText == nextText) return;
        currentSpeaker = nextSpeaker;
        currentText = nextText;
        RefreshCurrentLine();
    });

    private void RefreshCurrentLine()
    {
        if (currentLineView == null) return;
        string text = string.IsNullOrWhiteSpace(currentText) ? "尚未选择当前台词"
            : (string.IsNullOrWhiteSpace(currentSpeaker) ? "" : currentSpeaker + "：") + currentText;
        // Two fixed lines keep a long sentence from growing the window or hiding its buttons.
        currentLineView.Text = text.ReplaceLineEndings(" ");
        currentLineView.ContentDescription = "当前台词，" + text;
    }

    public void UpdateClickFollow(bool active) => OnMain(() =>
    {
        if (disposed) return;
        if (clickFollowing == active) return;
        clickFollowing = active;
        RefreshPlaybackMode();
        if (clickFollowButton != null)
        {
            clickFollowButton.Text = active ? "停止点按" : "点按跟随";
            clickFollowButton.ContentDescription = active ? "停止点按跟随" : "开启点按跟随";
            PgrTheme.StyleButton(clickFollowButton, primary: active);
            clickFollowButton.SetPadding(Dp(3), Dp(7), Dp(3), Dp(7));
            clickFollowButton.SetMinHeight(Dp(44)); clickFollowButton.SetMinimumHeight(Dp(44));
            clickFollowButton.SetCompoundDrawablesWithIntrinsicBounds(null, Icon(active ? "pause" : "locate"), null, null);
        }
        RefreshPresentation();
    });

    /// <summary>Preference setter; persistence is owned by AppSession and only user changes emit an event.</summary>
    public void SetCompactControlsVisible(bool value) => OnMain(() =>
    {
        if (disposed) return;
        compactControlsVisible = value;
        RefreshPresentation();
        ClampPosition();
    });

    public void SetCompactButtonsVisible(bool value) => OnMain(() =>
    {
        if (disposed) return;
        compactButtonsVisible = value;
        RefreshPresentation();
        ClampPosition();
    });

    /// <summary>Temporarily use only the ball over our pages, preserving the user's game preference.</summary>
    public void UpdateAppForeground(bool foreground) => OnMain(() =>
    {
        if (disposed || appForeground == foreground) return;
        appForeground = foreground;
        if (foreground) DismissConfirmation();
        RefreshPresentation();
        ClampPosition();
    });

    private void RefreshPlaybackMode()
    {
        if (playbackModeView != null)
        {
            playbackModeView.Text = clickFollowing ? "当前模式 · 点按跟随" : autoPlaying
                ? "当前模式 · 自动播放" : "当前模式 · 手动播放";
            playbackModeView.SetTextColor(clickFollowing || autoPlaying ? PgrTheme.Cyan : PgrTheme.Secondary);
            playbackModeView.ContentDescription = playbackModeView.Text;
        }
    }

    public void UpdateStatus(string text, bool paused = false, bool autoPlaying = false) => OnMain(() =>
    {
        if (disposed) return;
        status = text;
        bool pauseChanged = playbackPaused != paused;
        bool autoChanged = this.autoPlaying != autoPlaying;
        playbackPaused = paused;
        this.autoPlaying = autoPlaying;
        // A normal status refresh must not erase the reason the automatic run stopped.
        if (autoPlaying && autoPlaybackPausedReason.Length > 0) ClearAutoPlaybackPaused();
        RefreshPlaybackMode();
        if (statusView != null)
        {
            statusView.Text = text;
            statusView.SetTextColor(paused ? PgrTheme.Secondary : PgrTheme.Foreground);
        }
        if (pauseButton != null)
        {
            pauseButton.Text = paused ? "继续" : "暂停";
            pauseButton.ContentDescription = pauseButton.Text;
            if (pauseChanged) pauseButton.SetCompoundDrawablesWithIntrinsicBounds(null, Icon(paused ? "play" : "pause"), null, null);
        }
        if (autoPlayButton != null)
        {
            autoPlayButton.Text = autoPlaying ? "停止自动" : "自动播放";
            autoPlayButton.ContentDescription = autoPlaying ? "停止自动播放" : "开始自动播放";
            if (autoChanged)
            {
                PgrTheme.StyleButton(autoPlayButton, primary: autoPlaying);
                autoPlayButton.SetPadding(Dp(3), Dp(7), Dp(3), Dp(7));
                autoPlayButton.SetMinHeight(Dp(44));
                autoPlayButton.SetMinimumHeight(Dp(44));
                autoPlayButton.SetCompoundDrawablesWithIntrinsicBounds(null, Icon(autoPlaying ? "pause" : "play"), null, null);
            }
        }
        if (autoChanged) RefreshPresentation();
    });

    /// <summary>Called on the session's main looper; use the actual screen rectangle, not layout offsets.</summary>
    public bool ContainsPoint(float x, float y)
    {
        if (captureHidden && !captureKeepStopControl || !showing || disposed || root == null || !root.IsAttachedToWindow || root.Visibility != ViewStates.Visible)
            return false;
        var location = new int[2];
        root.GetLocationOnScreen(location);
        return x >= location[0] && y >= location[1] &&
            x < location[0] + root.Width && y < location[1] + root.Height;
    }

    /// <summary>分支选项的整个触碰范围不得挡住现有悬浮控制。</summary>
    public bool IntersectsRectangle(float left, float top, float right, float bottom)
    {
        if (!float.IsFinite(left) || !float.IsFinite(top) || !float.IsFinite(right) || !float.IsFinite(bottom) ||
            right <= left || bottom <= top) return true;
        if (captureHidden && !captureKeepStopControl || !showing || disposed || root == null ||
            !root.IsAttachedToWindow || root.Visibility != ViewStates.Visible) return false;
        var location = new int[2];
        root.GetLocationOnScreen(location);
        return left < location[0] + root.Width && right > location[0] &&
            top < location[1] + root.Height && bottom > location[1];
    }

    public void ShowNotice(string message) => OnMain(() =>
    {
        if (disposed || captureHidden || string.IsNullOrWhiteSpace(message)) return;
        status = message;
        Show();
        if (!showing) return;
        DismissConfirmation();
        UpdateStatus(message, playbackPaused, autoPlaying);
        try
        {
            ShowGameNotification(message, new[] { message }, _ => { }, "配音提示", null,
                null, null, null, null, singleConfirmationLabel: "知道了");
        }
        catch (Exception ex) { DismissConfirmation(); Error?.Invoke("无法显示提示通知：" + ex.Message); }
    });

    public void ShowAutoPlaybackPaused(string reason) => OnMain(() =>
    {
        if (disposed || captureHidden || string.IsNullOrWhiteSpace(reason)) return;
        autoPlaybackPausedReason = reason.Trim();
        long revision = ++autoPlaybackPausedRevision;
        Show();
        if (!showing) return;
        DismissConfirmation();
        RefreshAutoPlaybackPaused();
        try
        {
            confirmationCancelled = () => { if (revision == autoPlaybackPausedRevision) ClearAutoPlaybackPaused(); };
            ShowGameNotification(autoPlaybackPausedReason, new[] { autoPlaybackPausedReason }, _ =>
                { if (revision == autoPlaybackPausedRevision) ClearAutoPlaybackPaused(); },
                "自动播放已暂停", "关闭提示不会恢复自动播放。", null, null, null, null,
                singleConfirmationLabel: "知道了");
            confirmationIsPauseNotice = true;
        }
        catch (Exception ex) { DismissConfirmation(); Error?.Invoke("无法显示暂停通知：" + ex.Message); }
    });

    public void ClearAutoPlaybackPaused() => OnMain(() =>
    {
        if (disposed) return;
        if (confirmationIsPauseNotice) DismissConfirmation(notifyCancellation: false);
        autoPlaybackPausedReason = "";
        autoPlaybackPausedRevision++;
        RefreshAutoPlaybackPaused();
    });

    private void RefreshAutoPlaybackPaused()
    {
        if (autoPlaybackPausedCard is not { } card) return;
        card.RemoveAllViews();
        bool visible = autoPlaybackPausedReason.Length > 0;
        card.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
        if (!visible) return;

        long revision = autoPlaybackPausedRevision;
        var header = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        var title = new TextView(context) { Text = "自动播放已暂停", TextSize = FontSize(15) };
        title.SetTextColor(PgrTheme.Red);
        title.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        header.AddView(title, new LinearLayout.LayoutParams(0, -2, 1));
        var dismiss = new Button(context)
        {
            Text = "知道了",
            TextSize = FontSize(12),
            ContentDescription = "知道了，仅关闭暂停提示，不恢复自动播放"
        };
        PgrTheme.StyleButton(dismiss, primary: true);
        dismiss.SetPadding(Dp(5), Dp(5), Dp(5), Dp(5));
        dismiss.SetMinHeight(Dp(40)); dismiss.SetMinimumHeight(Dp(40));
        dismiss.Click += (_, _) =>
        {
            if (disposed || revision != autoPlaybackPausedRevision || !ReferenceEquals(card, autoPlaybackPausedCard)) return;
            ClearAutoPlaybackPaused();
        };
        header.AddView(dismiss, new LinearLayout.LayoutParams(Dp(68), Dp(40)) { LeftMargin = Dp(6) });
        card.AddView(header, new LinearLayout.LayoutParams(-1, -2));
        var reason = new TextView(context) { Text = autoPlaybackPausedReason, TextSize = FontSize(13) };
        reason.SetTextColor(PgrTheme.Foreground);
        reason.SetPadding(0, Dp(6), 0, Dp(6));
        reason.SetLineSpacing(Dp(2), 1);
        // 关闭入口先于完整原因，横屏也无需滚过长段正文才能关闭。
        card.AddView(reason, new LinearLayout.LayoutParams(-1, -2));
    }

    public void SetExpanded(bool value) => OnMain(() => SetExpandedCore(value));

    private void SetExpandedCore(bool value, bool refreshBrowse = true)
    {
        if (disposed) return;
        bool revealBrowse = refreshBrowse && value && !expanded && browsePage.HasValue && showing;
        if (!value) DismissConfirmation();
        expanded = value;
        // 同一主线程内先取得当前章节的模型，再让收起的阅读页重新可见。
        if (revealBrowse) BrowseRefreshRequested?.Invoke();
        RefreshPresentation();
        UpdateSize();
        UpdateLayout();
        ClampPosition();
        if (value && autoPlaying) actionScroll?.Post(() => actionScroll?.ScrollTo(0, 0));
    }

    private void RefreshPresentation()
    {
        ApplyCaptureVisibility();
        if (captureStopControl != null) captureStopControl.Visibility = CaptureStopVisible ? ViewStates.Visible : ViewStates.Gone;
        if (CaptureStopVisible)
        {
            // 定位期间保持图形与尺寸不变，状态通知不能把本应用的台词带回截图。
            if (handleView != null) handleView.Visibility = ViewStates.Gone;
            if (compactPanel != null) compactPanel.Visibility = ViewStates.Gone;
            if (panel != null) panel.Visibility = ViewStates.Gone;
            if (browseShell != null) browseShell.Visibility = ViewStates.Gone;
            return;
        }
        bool compact = !expanded && compactControlsVisible && !appForeground;
        bool reading = expanded && browsePage.HasValue;
        if (panel != null) panel.Visibility = expanded && !reading ? ViewStates.Visible : ViewStates.Gone;
        if (browseShell != null) browseShell.Visibility = reading ? ViewStates.Visible : ViewStates.Gone;
        for (int i = 0; i < browseTabs.Count; i++)
        {
            bool selected = i == (browsePage.HasValue ? (int)browsePage.Value + 1 : 0);
            PgrTheme.StyleButton(browseTabs[i], selected: selected, quiet: !selected);
            browseTabs[i].TextSize = FontSize(11);
            browseTabs[i].SetPadding(Dp(1), Dp(2), Dp(1), Dp(2));
            browseTabs[i].SetMinHeight(Dp(34)); browseTabs[i].SetMinimumHeight(Dp(34));
        }
        for (int i = 0; i < browseSideTabs.Count; i++)
        {
            bool selected = i == (browsePage.HasValue ? (int)browsePage.Value + 1 : 0);
            PgrTheme.StyleButton(browseSideTabs[i], selected: selected, quiet: !selected);
            browseSideTabs[i].TextSize = 12;
            browseSideTabs[i].SetSingleLine(true);
            browseSideTabs[i].SetPadding(ReadDp(1), ReadDp(2), ReadDp(1), ReadDp(2));
            browseSideTabs[i].SetMinHeight(ReadDp(38)); browseSideTabs[i].SetMinimumHeight(ReadDp(38));
        }
        if (compactPanel != null) compactPanel.Visibility = compact ? ViewStates.Visible : ViewStates.Gone;
        if (compactActions != null) compactActions.Visibility = compactButtonsVisible ? ViewStates.Visible : ViewStates.Gone;
        if (handleView != null)
        {
            handleView.Visibility = reading ? ViewStates.Gone : ViewStates.Visible;
            handleView.Text = expanded ? "配音控制" : compact
                ? (clickFollowing ? "点按跟随 · 点此展开" : autoPlaying ? "自动播放 · 点此展开" : "配音 · 点此展开") : "配";
            handleView.TextSize = FontSize(compact ? 10 : 15);
            handleView.Gravity = (expanded || compact ? GravityFlags.Start : GravityFlags.CenterHorizontal) | GravityFlags.CenterVertical;
            handleView.SetCompoundDrawablesWithIntrinsicBounds(expanded || compact ? Icon("wave", compact ? 12 : 18) : null, null,
                expanded || compact ? Icon(expanded ? "chevron_up" : "chevron_down", compact ? 12 : 17, PgrTheme.Secondary) : null, null);
            handleView.CompoundDrawablePadding = Dp(compact ? 4 : 9);
            handleView.SetPadding(Dp(expanded ? 13 : compact ? 6 : 0), 0, Dp(expanded ? 12 : compact ? 5 : 0), 0);
            handleView.Background = PgrTheme.Surface(context, expanded ? PgrTheme.Panel : PgrTheme.Selection,
                Color.Transparent, expanded ? null : PgrTheme.Red);
            handleView.ContentDescription = expanded ? "配音控制，点击收起，按住拖动" : compact
                ? "台词悬浮条，点击展开完整控制，按住拖动" : "战双配音悬浮球，点击展开，按住拖动";
            if (handleView.LayoutParameters is LinearLayout.LayoutParams handleLayout)
            {
                int height = Dp(compact ? 26 : 54);
                if (handleLayout.Height != height) { handleLayout.Height = height; handleView.LayoutParameters = handleLayout; }
            }
        }
        if (compactControlsButton != null)
        {
            compactControlsButton.Text = compactControlsVisible ? "仅保留悬浮球" : "显示台词和小按钮";
            compactControlsButton.ContentDescription = compactControlsButton.Text;
        }
        if (compactButtonsButton != null)
        {
            compactButtonsButton.Text = compactButtonsVisible ? "小按钮：开" : "小按钮：关";
            compactButtonsButton.ContentDescription = "上一句、当前句、下一句小按钮，" + (compactButtonsVisible ? "已显示，点此隐藏" : "已隐藏，点此显示");
        }
    }

    private void BuildView()
    {
        if (root != null) return;
        root = new LinearLayout(context) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(2), Dp(2), Dp(2), Dp(2));
        root.Background = PgrTheme.Surface(context, PgrTheme.Background, PgrTheme.Border);
        root.Elevation = Dp(10);
        root.LayoutChange += (_, _) =>
        {
            if (!showing || parameters == null) return;
            int oldWidth = parameters.Width, oldHeight = parameters.Height;
            UpdateSize();
            if (oldWidth != parameters.Width || oldHeight != parameters.Height) ClampPosition();
        };
        var captureStop = new CaptureStopFrame(context, this)
        {
            Clickable = true,
            Focusable = true,
            ContentDescription = "正在定位，点击取消自动播放",
            Visibility = ViewStates.Gone,
            Background = PgrTheme.Surface(context, PgrTheme.Selection, PgrTheme.Red)
        };
        captureStopControl = captureStop;
        captureStop.SetMinimumWidth(ReadDp(48));
        captureStop.SetMinimumHeight(ReadDp(48));
        captureStop.SetOnTouchListener(new CaptureCancelTouchListener(this));
        var stopSquare = new View(context);
        stopSquare.SetBackgroundColor(PgrTheme.Red);
        captureStop.AddView(stopSquare, new FrameLayout.LayoutParams(ReadDp(16), ReadDp(16), GravityFlags.Center));
        captureStop.Click += (_, _) =>
        {
            // 真实触摸直接由下面的门控接受；旧 PerformClick 或无触摸的直接 Click 只能记诊断。
            CaptureInteraction?.Invoke($"generation={CurrentCaptureControlGeneration}; action=Click; DownTime=none; rejected=direct-click-without-touch");
        };
        root.AddView(captureStop, new LinearLayout.LayoutParams(-1, ReadDp(52)));
        var handle = new TextView(context)
        {
            Text = "配",
            TextSize = FontSize(15),
            Gravity = GravityFlags.Center,
            ContentDescription = "战双配音悬浮球，点击展开，按住拖动"
        };
        handleView = handle;
        handle.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
        handle.SetTextColor(PgrTheme.Foreground);
        handle.CompoundDrawablePadding = Dp(9);
        handle.Background = PgrTheme.Surface(context, PgrTheme.Selection, Color.Transparent, PgrTheme.Red);
        handle.SetOnTouchListener(new DragListener(this));
        root.AddView(handle, new LinearLayout.LayoutParams(-1, Dp(54)));
        compactPanel = new LinearLayout(context) { Orientation = Orientation.Vertical };
        compactPanel.SetPadding(Dp(4), Dp(3), Dp(4), Dp(3));
        currentLineView = new TextView(context) { TextSize = FontSize(10), Gravity = GravityFlags.Top | GravityFlags.Start };
        currentLineView.SetTextColor(PgrTheme.Foreground);
        currentLineView.SetMaxLines(2);
        currentLineView.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        currentLineView.SetPadding(Dp(2), 0, Dp(2), 0);
        compactPanel.AddView(currentLineView, new LinearLayout.LayoutParams(-1, Dp(30)));
        RefreshCurrentLine();
        compactActions = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        foreach (var item in new[]
        {
            (Label: "上一句", Description: "上一句", Action: OverlayCommand.Previous),
            (Label: "当前句", Description: "重播当前句", Action: OverlayCommand.Replay),
            (Label: "下一句", Description: "下一句", Action: OverlayCommand.Next)
        })
        {
            var button = new Button(context) { Text = item.Label, TextSize = FontSize(10), ContentDescription = item.Description };
            PgrTheme.StyleButton(button, primary: item.Action == OverlayCommand.Replay);
            button.SetPadding(Dp(2), Dp(1), Dp(2), Dp(1));
            button.SetSingleLine(true);
            button.SetMinHeight(Dp(28)); button.SetMinimumHeight(Dp(28));
            var command = item.Action;
            button.Click += (_, _) => Command?.Invoke(command);
            compactActions.AddView(button, new LinearLayout.LayoutParams(0, Dp(28), 1) { LeftMargin = Dp(1), RightMargin = Dp(1) });
        }
        compactPanel.AddView(compactActions, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(4) });
        root.AddView(compactPanel, new LinearLayout.LayoutParams(-1, -2));
        panel = new LinearLayout(context) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
        panel.SetPadding(Dp(9), Dp(8), Dp(9), Dp(6));
        browseTabs.Clear();
        var tabs = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        string[] tabLabels = { "控制", "台词", "定位", "分支", "记录" };
        for (int i = 0; i < tabLabels.Length; i++)
        {
            int index = i;
            var tab = new Button(context) { Text = tabLabels[i], TextSize = FontSize(11), ContentDescription = "悬浮" + tabLabels[i] + "页" };
            tab.Click += (_, _) => { if (index == 0) ShowControlPage(); else ShowBrowsePage((OverlayBrowsePage)(index - 1)); };
            tabs.AddView(tab, new LinearLayout.LayoutParams(0, Dp(34), 1) { LeftMargin = Dp(1), RightMargin = Dp(1) });
            browseTabs.Add(tab);
        }
        panel.AddView(tabs, new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(6) });
        controlPanel = new LinearLayout(context) { Orientation = Orientation.Vertical };
        panel.AddView(controlPanel, new LinearLayout.LayoutParams(-1, 0, 1));
        playbackModeView = new TextView(context) { TextSize = FontSize(12) };
        playbackModeView.SetPadding(Dp(3), 0, Dp(3), Dp(5));
        playbackModeView.SetMaxLines(1);
        playbackModeView.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        controlPanel.AddView(playbackModeView, new LinearLayout.LayoutParams(-1, -2));
        // Keep position confirmation accessible above the scrollable controls.
        var confirmPosition = new Button(context) { Text = "确认当前句", TextSize = FontSize(12), ContentDescription = "确认播放器当前句与游戏一致，不会采用未选择的 OCR 结果" };
        PgrTheme.StyleButton(confirmPosition, primary: true);
        confirmPosition.SetPadding(Dp(3), Dp(5), Dp(3), Dp(5));
        confirmPosition.SetMinHeight(Dp(44)); confirmPosition.SetMinimumHeight(Dp(44));
        confirmPosition.Click += (_, _) =>
        {
            actionScroll?.ScrollTo(0, 0);
            Command?.Invoke(OverlayCommand.ConfirmPosition);
        };
        controlPanel.AddView(confirmPosition, new LinearLayout.LayoutParams(-1, Dp(44)) { LeftMargin = Dp(2), RightMargin = Dp(2), BottomMargin = Dp(8) });
        RefreshPlaybackMode();
        var scroll = new ScrollView(context) { FillViewport = false, VerticalScrollBarEnabled = true };
        actionScroll = scroll;
        var actionRows = new LinearLayout(context) { Orientation = Orientation.Vertical };
        scroll.AddView(actionRows, new ScrollView.LayoutParams(-1, -2));
        controlPanel.AddView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        autoPlaybackPausedCard = new LinearLayout(context) { Orientation = Orientation.Vertical };
        autoPlaybackPausedCard.SetPadding(Dp(10), Dp(9), Dp(10), Dp(9));
        autoPlaybackPausedCard.Background = PgrTheme.Surface(context, PgrTheme.Selection, PgrTheme.Red, PgrTheme.Red);
        actionRows.AddView(autoPlaybackPausedCard, new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(8) });
        RefreshAutoPlaybackPaused();
        var compactOptions = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        compactButtonsButton = BrowsePagingButton("小按钮：开", () =>
        {
            SetCompactButtonsVisible(!compactButtonsVisible);
            CompactButtonsChanged?.Invoke(compactButtonsVisible);
        });
        compactControlsButton = BrowsePagingButton("仅保留悬浮球", () =>
        {
            SetCompactControlsVisible(!compactControlsVisible);
            CompactControlsChanged?.Invoke(compactControlsVisible);
            SetExpanded(false);
        });
        foreach (var button in new[] { compactButtonsButton, compactControlsButton })
            compactOptions.AddView(button, new LinearLayout.LayoutParams(0, Dp(36), 1) { LeftMargin = Dp(1), RightMargin = Dp(1) });
        actionRows.AddView(compactOptions, new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(5) });
        statusView = new TextView(context) { Text = status, TextSize = FontSize(12) };
        statusView.SetTextColor(playbackPaused ? PgrTheme.Secondary : PgrTheme.Foreground);
        statusView.SetPadding(Dp(3), 0, Dp(3), Dp(8));
        statusView.SetLineSpacing(Dp(2), 1);
        statusView.SetMaxLines(3);
        statusView.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        actionRows.AddView(statusView, new LinearLayout.LayoutParams(-1, -2));
        AddRow(actionRows, false, false, (autoPlaying ? "停止自动" : "自动播放", OverlayCommand.AutoPlay),
            (clickFollowing ? "停止点按" : "点按跟随", OverlayCommand.ClickFollow), ("下一句区域", OverlayCommand.AdvanceRegion));
        AddRow(actionRows, true, false, ("上一句", OverlayCommand.Previous), ("重播", OverlayCommand.Replay), ("下一句", OverlayCommand.Next));
        AddRow(actionRows, false, false, (playbackPaused ? "继续" : "暂停", OverlayCommand.Pause), ("分支", OverlayCommand.Branch), ("原声", OverlayCommand.Original));
        AddRow(actionRows, false, false, ("识别定位", OverlayCommand.Ocr), ("历史", OverlayCommand.History), ("章节", OverlayCommand.Chapters));
        AddRow(actionRows, false, false, ("反馈这句", OverlayCommand.Feedback));
        browseShell = new LinearLayout(context) { Orientation = Orientation.Horizontal, Visibility = ViewStates.Gone };
        var sidebar = new LinearLayout(context) { Orientation = Orientation.Vertical };
        sidebar.SetPadding(ReadDp(3), ReadDp(4), ReadDp(3), ReadDp(4));
        sidebar.Background = PgrTheme.Surface(context, PgrTheme.Panel, PgrTheme.Border);
        var sideScroll = new ScrollView(context) { VerticalScrollBarEnabled = false };
        var sideItems = new LinearLayout(context) { Orientation = Orientation.Vertical };
        sideScroll.AddView(sideItems, new ScrollView.LayoutParams(-1, -2));
        sidebar.AddView(sideScroll, new LinearLayout.LayoutParams(-1, 0, 1));
        browseSideTabs.Clear();
        for (int i = 0; i < tabLabels.Length; i++)
        {
            int index = i;
            var tab = ReadingButton(tabLabels[i], () => { if (index == 0) ShowControlPage(); else ShowBrowsePage((OverlayBrowsePage)(index - 1)); });
            tab.ContentDescription = "悬浮" + tabLabels[i] + "页";
            sideItems.AddView(tab, new LinearLayout.LayoutParams(-1, ReadDp(38)) { BottomMargin = ReadDp(2) });
            browseSideTabs.Add(tab);
        }
        var collapse = ReadingButton("收起", () => SetExpanded(false));
        sidebar.AddView(collapse, new LinearLayout.LayoutParams(-1, ReadDp(34)));
        browseShell.AddView(sidebar, new LinearLayout.LayoutParams(ReadDp(54), -1));
        browsePanel = new LinearLayout(context) { Orientation = Orientation.Vertical };
        browsePanel.SetPadding(ReadDp(8), ReadDp(3), ReadDp(5), ReadDp(3));
        var readHeader = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        readHeader.SetGravity(GravityFlags.CenterVertical);
        browseHeading = new TextView(context) { Text = "台词", TextSize = 13, Gravity = GravityFlags.CenterVertical,
            ContentDescription = "阅读面板标题，按住拖动" };
        browseHeading.SetTextColor(PgrTheme.Secondary); browseHeading.SetSingleLine(true);
        browseHeading.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        browseHeading.SetOnTouchListener(new DragListener(this, false));
        readHeader.AddView(browseHeading, new LinearLayout.LayoutParams(0, -1, 1));
        var closeReading = ReadingButton("收回", ShowControlPage);
        closeReading.ContentDescription = "收回阅读面板，返回控制页";
        readHeader.AddView(closeReading, new LinearLayout.LayoutParams(ReadDp(48), -1));
        browsePanel.AddView(readHeader, new LinearLayout.LayoutParams(-1, ReadDp(30)) { BottomMargin = ReadDp(3) });
        browseScroll = new ScrollView(context) { FillViewport = false, VerticalScrollBarEnabled = true };
        browseRows = new LinearLayout(context) { Orientation = Orientation.Vertical };
        browseScroll.AddView(browseRows, new ScrollView.LayoutParams(-1, -2));
        browsePanel.AddView(browseScroll, new LinearLayout.LayoutParams(-1, 0, 1));
        var pagination = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        browsePrevious = ReadingButton("上一页", () => { browsePageIndex--; RenderBrowse(preferSelected: false); });
        browseCurrent = ReadingButton("回当前句", () => { if (browseModel != null) { browsePageIndex = SelectedBrowsePage(browseModel); RenderBrowse(); } });
        browseNext = ReadingButton("下一页", () => { browsePageIndex++; RenderBrowse(preferSelected: false); });
        foreach (var button in new[] { browsePrevious, browseCurrent, browseNext })
            pagination.AddView(button, new LinearLayout.LayoutParams(0, ReadDp(32), 1) { LeftMargin = ReadDp(1), RightMargin = ReadDp(1) });
        browsePanel.AddView(pagination, new LinearLayout.LayoutParams(-1, -2) { TopMargin = ReadDp(2) });
        browseShell.AddView(browsePanel, new LinearLayout.LayoutParams(0, -1, 1));
        RenderBrowse();
        var divider = new View(context); divider.SetBackgroundColor(PgrTheme.Border);
        panel.AddView(divider, new LinearLayout.LayoutParams(-1, Math.Max(1, Dp(.5f))) { TopMargin = Dp(6), BottomMargin = Dp(2) });
        // Keep collapse/hide accessible even when a landscape screen makes the actions scroll.
        AddRow(panel, false, true, ("主界面", OverlayCommand.OpenApp), ("收起", null), ("隐藏", OverlayCommand.Hide));
        root.AddView(panel, new LinearLayout.LayoutParams(-1, 0, 1));
        root.AddView(browseShell, new LinearLayout.LayoutParams(-1, 0, 1));
        parameters = new WindowManagerLayoutParams(Dp(58), Dp(58),
            WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal | WindowManagerFlags.LayoutInScreen,
            Format.Translucent)
        { Gravity = GravityFlags.Top | GravityFlags.Left, X = Dp(12), Y = Dp(100) };
        if (Build.VERSION.SdkInt >= BuildVersionCodes.P) parameters.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.Never;
        RefreshPresentation();
        UpdateSize();
    }

    private Button BrowsePagingButton(string label, Action action)
    {
        var button = new Button(context) { Text = label, TextSize = FontSize(10), ContentDescription = label };
        PgrTheme.StyleButton(button, quiet: true);
        button.SetMinHeight(Dp(34)); button.SetMinimumHeight(Dp(34));
        button.SetPadding(Dp(2), Dp(2), Dp(2), Dp(2));
        button.Click += (_, _) => action();
        return button;
    }

    private Button ReadingButton(string label, Action action)
    {
        var button = new Button(context) { Text = label, TextSize = 12, ContentDescription = label };
        PgrTheme.StyleButton(button, quiet: true);
        button.SetSingleLine(true); button.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        button.SetMinHeight(ReadDp(28)); button.SetMinimumHeight(ReadDp(28));
        button.SetPadding(ReadDp(5), ReadDp(2), ReadDp(5), ReadDp(2));
        button.Click += (_, _) => action();
        return button;
    }

    private void AddRow(LinearLayout container, bool playbackRow, bool footer, params (string Label, OverlayCommand? Action)[] actions)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        foreach (var item in actions)
        {
            var button = new Button(context) { Text = item.Label, TextSize = FontSize(11) };
            PgrTheme.StyleButton(button, primary: playbackRow && item.Action == OverlayCommand.Replay || autoPlaying && item.Action == OverlayCommand.AutoPlay || clickFollowing && item.Action == OverlayCommand.ClickFollow, quiet: footer);
            button.SetPadding(Dp(3), Dp(footer ? 3 : 7), Dp(3), Dp(footer ? 3 : 7));
            button.SetMinHeight(Dp(44));
            button.SetMinimumHeight(Dp(44));
            button.ContentDescription = item.Action == OverlayCommand.OpenApp ? "打开主界面" : item.Label;
            var icon = Icon(IconName(item.Action), footer ? 15 : playbackRow ? 24 : 20, footer ? PgrTheme.Secondary : PgrTheme.Foreground);
            button.CompoundDrawablePadding = Dp(footer ? 5 : 4);
            if (footer) button.SetCompoundDrawablesWithIntrinsicBounds(icon, null, null, null);
            else button.SetCompoundDrawablesWithIntrinsicBounds(null, icon, null, null);
            if (item.Action == OverlayCommand.Pause) pauseButton = button;
            if (item.Action == OverlayCommand.AutoPlay)
            {
                autoPlayButton = button;
                button.ContentDescription = autoPlaying ? "停止自动播放" : "开始自动播放";
            }
            if (item.Action == OverlayCommand.ClickFollow)
            {
                clickFollowButton = button;
                button.ContentDescription = clickFollowing ? "停止点按跟随" : "开启点按跟随";
            }
            var action = item.Action;
            button.Click += (_, _) =>
            {
                if (action == null) SetExpanded(false);
                else if (action == OverlayCommand.Hide) { HideCore(); Command?.Invoke(action.Value); }
                else if (action == OverlayCommand.Branch) ShowBrowsePage(OverlayBrowsePage.Branches);
                else if (action == OverlayCommand.History) ShowBrowsePage(OverlayBrowsePage.History);
                else if (action == OverlayCommand.Ocr) { ShowBrowsePage(OverlayBrowsePage.Locate); Command?.Invoke(action.Value); }
                else Command?.Invoke(action.Value);
            };
            row.AddView(button, new LinearLayout.LayoutParams(0, Dp(footer ? 44 : playbackRow ? 68 : 58), 1)
                { LeftMargin = Dp(2), RightMargin = Dp(2), TopMargin = Dp(footer ? 2 : 5) });
        }
        container.AddView(row);
    }

    private PgrIconDrawable Icon(string name, int size = 20, Color? color = null) => new(name, color ?? PgrTheme.Foreground, density * uiScale, size);
    private string IconName(OverlayCommand? command) => command switch
    {
        OverlayCommand.Pause => playbackPaused ? "play" : "pause",
        OverlayCommand.Replay => "replay", OverlayCommand.Previous => "previous", OverlayCommand.Next => "next",
        OverlayCommand.Branch => "branch", OverlayCommand.Original => "original", OverlayCommand.Ocr => "locate",
        OverlayCommand.OpenApp => "open", OverlayCommand.History => "history", OverlayCommand.Chapters => "archive",
        OverlayCommand.AutoPlay => autoPlaying ? "pause" : "play", OverlayCommand.AdvanceRegion => "locate",
        OverlayCommand.ClickFollow => clickFollowing ? "pause" : "locate",
        OverlayCommand.Hide => "close", _ => "chevron_up"
    };
    private int Dp(float value) => (int)(value * density * uiScale + 0.5f);
    private int ReadDp(float value) => (int)(value * density + .5f);
    private float FontSize(float value) => value * Math.Max(.85f, uiScale);
    private void OnMain(Action action)
    {
        if (Looper.MyLooper() == Looper.MainLooper) action();
        else main.Post(action);
    }
    private void ClampPosition()
    {
        if (parameters == null) return;
        UpdateSize();
        var safe = ScreenArea();
        parameters.X = Math.Clamp(parameters.X, safe.Left, Math.Max(safe.Left, safe.Right - parameters.Width));
        parameters.Y = Math.Clamp(parameters.Y, safe.Top, Math.Max(safe.Top, safe.Bottom - parameters.Height));
        UpdateLayout();
    }
    private void UpdateSize()
    {
        if (parameters == null) return;
        var safe = ScreenArea();
        int screenWidth = Math.Max(1, safe.Right - safe.Left), screenHeight = Math.Max(1, safe.Bottom - safe.Top);
        if (CaptureStopVisible)
        {
            // 不应用小屏 uiScale，取消按钮始终保留至少 48dp 的触摸尺寸。
            parameters.Width = Math.Min(ReadDp(56), screenWidth);
            parameters.Height = Math.Min(ReadDp(56), screenHeight);
            return;
        }
        bool compact = compactControlsVisible && !appForeground;
        bool reading = expanded && browsePage.HasValue;
        int readWidth = ReadDp(screenWidth > screenHeight && screenHeight / density < 450 ? 486 : 456);
        parameters.Width = Math.Min(reading ? readWidth : expanded ? Dp(300) : compact ? Dp(180) : Dp(58), screenWidth);
        int desiredHeight = reading ? ReadDp(440) : expanded ? Dp(444) : compact ? Dp(compactButtonsVisible ? 98 : 66) : Dp(58);
        parameters.Height = Math.Min(desiredHeight, Math.Max(1, (int)(screenHeight * (reading ? .9f : expanded ? .82f : 1))));
    }
    private (int Left, int Top, int Right, int Bottom) ScreenArea()
    {
        var metrics = context.Resources!.DisplayMetrics!;
        int left = 0, top = (int)(26 * metrics.Density), right = metrics.WidthPixels, bottom = metrics.HeightPixels - (int)(26 * metrics.Density);
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var screen = windows.CurrentWindowMetrics;
            var bounds = screen.Bounds;
            var insets = screen.WindowInsets.GetInsetsIgnoringVisibility(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
            left = bounds.Left + insets.Left; top = bounds.Top + insets.Top;
            right = bounds.Right - insets.Right; bottom = bounds.Bottom - insets.Bottom;
        }
        return (left, top, Math.Max(left + 1, right), Math.Max(top + 1, bottom));
    }
    private void RefreshDisplay() => OnMain(() =>
    {
        if (disposed) return;
        var safe = ScreenArea();
        float nextDensity = context.Resources!.DisplayMetrics!.Density;
        float nextFontScale = context.Resources.Configuration?.FontScale ?? 1f;
        int width = safe.Right - safe.Left, height = safe.Bottom - safe.Top;
        float heightDp = height / Math.Max(.1f, nextDensity);
        float nextScale = heightDp < 450 ? Math.Clamp(heightDp / 500f, .72f, 1f) : 1f;
        bool changed = displayWidth != width || displayHeight != height || Math.Abs(density - nextDensity) > .01f || Math.Abs(uiScale - nextScale) > .01f || Math.Abs(displayFontScale - nextFontScale) > .01f;
        displayWidth = width; displayHeight = height; density = nextDensity; uiScale = nextScale; displayFontScale = nextFontScale;
        if (!changed) { if (showing) ClampPosition(); return; }
        DismissConfirmation();
        if (root == null) return;
        bool restore = showing;
        int x = parameters?.X ?? Dp(12), y = parameters?.Y ?? safe.Top;
        HideCore(immediate: true, preserveCaptureRound: true);
        root.Dispose(); root = null;
        try
        {
            BuildView();
            if (parameters != null) { parameters.X = x; parameters.Y = y; }
            if (restore) { windows.AddView(root, parameters); showing = true; ClampPosition(); }
        }
        catch (Exception ex) { showing = false; Error?.Invoke("调整悬浮窗口未完成：" + ex.Message); }
    });
    private void UpdateLayout()
    {
        if (showing && root != null && parameters != null)
            try { windows.UpdateViewLayout(root, parameters); } catch (Exception ex) { Error?.Invoke(ex.Message); }
    }
    private void HideCore(bool immediate = false, bool preserveCaptureRound = false)
    {
        ClearBranchEntered();
        ClearDefaultBranchWait();
        DismissConfirmation();
        if (preserveCaptureRound) captureCancelGesture.ResetPresentation();
        else captureCancelGesture.End();
        if (!showing) return;
        showing = false;
        try { if (root != null) { if (immediate) windows.RemoveViewImmediate(root); else windows.RemoveView(root); } } catch { }
    }
    public void Dispose() => OnMain(() =>
    {
        if (disposed) return;
        disposed = true; HideCore();
        displays?.UnregisterDisplayListener(displayListener);
        context.UnregisterComponentCallbacks(configurationListener);
        displayListener.Dispose(); configurationListener.Dispose();
        root?.Dispose(); root = null;
    });

    private sealed class DisplayListener(OverlayController owner) : Java.Lang.Object, DisplayManager.IDisplayListener
    {
        public void OnDisplayAdded(int displayId) => owner.RefreshDisplay();
        public void OnDisplayChanged(int displayId) => owner.RefreshDisplay();
        public void OnDisplayRemoved(int displayId) => owner.RefreshDisplay();
    }

    private sealed class ConfigurationListener(OverlayController owner) : Java.Lang.Object, IComponentCallbacks
    {
        public void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig) => owner.RefreshDisplay();
        public void OnLowMemory() { }
    }

    private void CaptureControlDrawn(View view)
    {
        if (disposed || !showing || !CaptureStopVisible || !ReferenceEquals(captureStopControl, view)) return;
        long now = SystemClock.UptimeMillis();
        if (captureCancelGesture.MarkShown(CurrentCaptureControlGeneration, now))
            CaptureInteraction?.Invoke($"generation={CurrentCaptureControlGeneration}; phase=shown; uptime={now}");
    }

    private sealed class CaptureStopFrame : FrameLayout
    {
        private readonly OverlayController owner;
        public CaptureStopFrame(Context context, OverlayController owner) : base(context)
        {
            this.owner = owner;
            SetWillNotDraw(false);
        }
        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            owner.CaptureControlDrawn(this);
        }
    }

    private sealed class CaptureCancelTouchListener(OverlayController owner) : Java.Lang.Object, View.IOnTouchListener
    {
        private long gestureGeneration;
        public bool OnTouch(View? view, MotionEvent? motion)
        {
            if (motion == null) return true;
            if (motion.ActionMasked == MotionEventActions.Down) gestureGeneration = owner.CurrentCaptureControlGeneration;
            if (owner.disposed || !owner.showing || !owner.CaptureStopVisible || view == null ||
                !ReferenceEquals(owner.captureStopControl, view))
            {
                if (motion.ActionMasked != MotionEventActions.Move)
                    owner.CaptureInteraction?.Invoke($"generation={gestureGeneration}; action={motion.ActionMasked}; DownTime={motion.DownTime}; rejected=inactive-or-old-view");
                return true;
            }
            var action = motion.ActionMasked switch
            {
                MotionEventActions.Down => CaptureCancelGestureAction.Down,
                MotionEventActions.Move => CaptureCancelGestureAction.Move,
                MotionEventActions.Up => CaptureCancelGestureAction.Up,
                MotionEventActions.PointerDown or MotionEventActions.PointerUp => CaptureCancelGestureAction.PointerChanged,
                _ => CaptureCancelGestureAction.Cancel
            };
            int count = motion.PointerCount;
            int pointer = count > 0 ? motion.GetPointerId(0) : -1;
            bool inside = count > 0 && motion.GetX(0) >= 0 && motion.GetY(0) >= 0 && motion.GetX(0) < view.Width && motion.GetY(0) < view.Height;
            // Android 可以批量合并 Move；任何历史采样出界都取消本次触摸。
            for (int i = 0; inside && i < motion.HistorySize; i++)
                inside = motion.GetHistoricalX(0, i) >= 0 && motion.GetHistoricalY(0, i) >= 0 &&
                    motion.GetHistoricalX(0, i) < view.Width && motion.GetHistoricalY(0, i) < view.Height;
            bool accepted = owner.captureCancelGesture.Handle(gestureGeneration, action, pointer, count,
                motion.DownTime, motion.EventTime, inside, out string reason);
            if (motion.ActionMasked != MotionEventActions.Move)
                owner.CaptureInteraction?.Invoke($"generation={gestureGeneration}; current={owner.CurrentCaptureControlGeneration}; action={motion.ActionMasked}; pointer={pointer}; count={count}; DownTime={motion.DownTime}; eventTime={motion.EventTime}; {(accepted ? "accepted" : "rejected")}={reason}");
            if (accepted) owner.CaptureCancelRequested?.Invoke(gestureGeneration);
            return true;
        }
    }

    private sealed class DragListener(OverlayController owner, bool toggleOnTap = true) : Java.Lang.Object, View.IOnTouchListener
    {
        private float downX, downY;
        private int startX, startY;
        private bool dragged;
        public bool OnTouch(View? view, MotionEvent? motion)
        {
            if (motion == null || owner.parameters == null) return false;
            switch (motion.ActionMasked)
            {
                case MotionEventActions.Down:
                    downX = motion.RawX; downY = motion.RawY;
                    startX = owner.parameters.X; startY = owner.parameters.Y; dragged = false;
                    return true;
                case MotionEventActions.Move:
                    float dx = motion.RawX - downX, dy = motion.RawY - downY;
                    if (Math.Abs(dx) + Math.Abs(dy) > owner.Dp(7)) dragged = true;
                    if (dragged)
                    {
                        owner.parameters.X = startX + (int)dx; owner.parameters.Y = startY + (int)dy;
                        owner.ClampPosition();
                    }
                    return true;
                case MotionEventActions.Up:
                    if (!dragged) { view?.PerformClick(); if (toggleOnTap) owner.SetExpanded(!owner.expanded); }
                    return true;
                case MotionEventActions.Cancel: return true;
                default: return false;
            }
        }
    }
}
