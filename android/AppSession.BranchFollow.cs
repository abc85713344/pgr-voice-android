using Android.Graphics;
using Android.OS;
using PgrVoice.AndroidApp.Contracts;
using PgrVoice.AndroidApp.Ocr;
using PgrVoice.AndroidApp.Platform;
using PgrVoice.AndroidApp.Ui;
using OperationCanceledException = System.OperationCanceledException;

namespace PgrVoice.AndroidApp;

public sealed partial class AppSession
{
    sealed record BranchResumeIntent(PlaybackEngine Engine, string MenuId, string GamePackage,
        AdvanceTapTarget Target, string DisplayKey, long CreatedAt, bool ResumeAutoAfterCommon, int DefaultOption = 0);

    sealed class BranchFollowAttempt(BranchResumeIntent intent, long captureSession, ScreenDisplayState display)
    {
        public readonly BranchResumeIntent Intent = intent;
        public readonly long CaptureSession = captureSession;
        public readonly ScreenDisplayState Display = display;
        public readonly BranchFollowAttemptGate Gate = new();
        public readonly CancellationTokenSource Cancellation = new();
        public readonly List<TapFollowOverlay> Regions = new();
        public readonly Dictionary<string, byte[]> OptionMasks = new(StringComparer.Ordinal);
        public BranchFollowMenuMatch? MenuMatch;
        public BranchFollowMenuMatch? DefaultObservedMenu;
        public string? SelectedOptionId, PendingLine;
        public string? DefaultFirstNode, DefaultDisplayNode, Navigation;
        public string? WaitMessage;
        public int ExcludedTop;
        public string[] PriorDefaultLines = Array.Empty<string>();
        public volatile bool Invalidated;
        public int Consensus;
        public bool AwaitingMenuFrame;
        public long ArmAfterTimestamp;
        public bool AwaitingLineFrame;
        public long CommitAfterTimestamp;
        public BranchFollowRect PendingLineBounds;
        public byte[]? PendingLineMask;
        public long ServiceToken, NotBeforeFrame, LastFrameTimestamp, CaptureControlGeneration;
        public Action? Watchdog;
    }

    BranchFollowAttempt? branchFollow;
    BranchResumeIntent? branchResumeIntent;
    public bool BranchFollowRunning => branchFollow is { Gate.IsActive: true };

    public void SetBranchAutoFollow(bool enabled)
    {
        ClearBranchAutoReturn();
        CancelBranchFollow();
        Settings.BranchAutoFollow = enabled;
        SaveSettings();
        Status = enabled ? "分支配音跟随：按游戏当前分支、人物或话题确认，随后继续点按跟随。" :
            "分支期间暂停配音：你自行继续游戏；有明确共同线时显示台词提示，确认出现该句后恢复原来的跟随方式。";
        Notify();
    }

    bool BranchIntentEnvironmentValid(BranchResumeIntent intent)
    {
        if ((intent.DefaultOption == 0 ? !Settings.BranchAutoFollow : !Settings.AutoPlayDefaultBranchEnabled || Settings.DefaultBranchOption != intent.DefaultOption) ||
            uiVisible || !ReferenceEquals(Engine, intent.Engine) ||
            SystemClock.ElapsedRealtime() - intent.CreatedAt > 90_000 || !overlay.CanShow) return false;
        var state = GameAdvanceAccessibilityService.GetAvailability(intent.GamePackage);
        return state.CanTap && state.DisplayKey == intent.DisplayKey && MatchesTarget(intent.Target, state) &&
            Settings.AdvanceTargetFor(intent.DisplayKey) == intent.Target;
    }

    bool TryBeginBranchFollow(PlaybackEngine engine)
    {
        if (!ClickFollowRunning || clickTarget == null || clickDisplayKey == null) return false;
        return BeginManualBranchFollow(engine, clickTarget, clickDisplayKey, BranchAutoReturnValid(engine));
    }

    bool BeginBranchFollow(PlaybackEngine engine, AdvanceTapTarget target, string displayKey, bool resumeAutoAfterCommon)
    {
        if (!Settings.BranchAutoFollow || engine.Current is not { Kind: "choice" } menu ||
            !BranchFollowPolicy.IsEligibleMenu(engine, menu.Id, out _)) return false;
        var intent = new BranchResumeIntent(engine, menu.Id, AutoGamePackage, target, displayKey, SystemClock.ElapsedRealtime(), resumeAutoAfterCommon);
        // 撤销旧普通点按会话后再建立分支会话，系统取消事件不能伤及后来创建的会话。
        CancelClickFollow();
        branchResumeIntent = intent;
        Invalidate(preserveBranchIntent: true);
        audio.Stop(); engine.PauseForBrowse();
        if (!Screen.CaptureActive || Screen.DisplayState.RegionKey == null)
        {
            Status = "已到分支；自动跟随需要已有屏幕捕获授权。请在游戏和悬浮分支页选择相同选项。";
            Notify(); overlay.ShowNotice(Status); return true;
        }
        if (!BranchIntentEnvironmentValid(intent))
        {
            branchResumeIntent = null;
            Status = "游戏或屏幕已变化，请手动核对分支位置。"; Notify(); return true;
        }
        var attempt = new BranchFollowAttempt(intent, Screen.SessionId, Screen.DisplayState);
        branchFollow = attempt;
        long now = SystemClock.ElapsedRealtime();
        attempt.Gate.Start(now); attempt.NotBeforeFrame = now + 200;
        overlay.SetExpanded(false);
        // 复用无文字的停止入口；播放器正文、菜单和旧台词不能进入 OCR 证据。
        overlay.SetCaptureHidden(true, keepStopControl: true);
        attempt.CaptureControlGeneration = overlay.CurrentCaptureControlGeneration;
        Status = "分支跟随：正在识别两个选项；识别完成后轻点游戏选项文字。";
        attempt.Watchdog = () =>
        {
            if (!CheckBranchFollow(attempt)) return;
            if (attempt.Watchdog != null) main.PostDelayed(attempt.Watchdog, 400);
        };
        main.PostDelayed(attempt.Watchdog, 400);
        Diagnostics.Log("分支跟随开始", menu.Id); Notify(); return true;
    }

    void CancelBranchFollow(bool preserveResumeIntent = false)
    {
        CancelManualBranchFollow();
        var attempt = branchFollow; branchFollow = null;
        if (!preserveResumeIntent) branchResumeIntent = null;
        if (attempt == null) return;
        attempt.Gate.Stop();
        if (attempt.Watchdog != null) main.RemoveCallbacks(attempt.Watchdog);
        attempt.Watchdog = null;
        foreach (var region in attempt.Regions) region.Dispose();
        attempt.Regions.Clear();
        attempt.Cancellation.Cancel(); attempt.Cancellation.Dispose();
        if (attempt.ServiceToken != 0) GameAdvanceAccessibilityService.CancelSession("分支跟随会话已结束。");
        overlay.SetCaptureHidden(false);
    }

    void StopBranchFollow(string reason, bool preserveResumeIntent = false)
    {
        CancelBranchFollow(preserveResumeIntent);
        audio.Stop(); Engine?.PauseForBrowse(); Status = reason;
        Diagnostics.Log("分支跟随暂停", reason); Notify(); overlay.ShowNotice(reason);
    }

    bool CheckBranchFollow(BranchFollowAttempt attempt)
    {
        if (!ReferenceEquals(branchFollow, attempt) || !attempt.Gate.IsActive) return false;
        if (attempt.Invalidated || !BranchIntentEnvironmentValid(attempt.Intent) ||
            attempt.Navigation != null && attempt.Navigation != System.Text.Json.JsonSerializer.Serialize(attempt.Intent.Engine.ExportNavigation(), Json.Options) ||
            !Screen.CaptureActive || Screen.SessionId != attempt.CaptureSession ||
            Screen.DisplayState != attempt.Display || Engine?.CurrentId != attempt.Intent.MenuId || Engine.Mode != RunMode.Choice)
        {
            StopBranchFollow("游戏、屏幕或分支位置已变化，分支跟随已停止。请手动核对后继续。"); return false;
        }
        if (attempt.Gate.IsExpired(SystemClock.ElapsedRealtime()))
        {
            StopBranchFollow("这次分支未能及时确认，请在游戏和悬浮分支页选择相同选项；不会替你重试点击。", true); return false;
        }
        return true;
    }

    bool CancelBranchFromCaptureControl(long generation)
    {
        if (branchFollow is not { } attempt || generation != attempt.CaptureControlGeneration) return false;
        StopBranchFollow("已停止分支跟随，请在悬浮分支页手动选择。点击不会自动重试。"); return true;
    }

    void HandleBranchFollowFrame(CapturedFrame frame)
    {
        if (branchFollow is not { } attempt || !CheckBranchFollow(attempt)) return;
        if (attempt.Intent.DefaultOption != 0)
            attempt.ExcludedTop = Math.Max(attempt.ExcludedTop, overlay.CaptureWaitExcludedBottom);
        ObservedFrameCount++; Diagnostics.Frame(frame.IsRepeatedSample);
        if (frame.SessionId != attempt.CaptureSession || frame.DisplayState != attempt.Display) return;
        var target = attempt.Intent.Target;
        // 单应用捕获可能有未提供的屏幕偏移，本试验不猜其坐标。
        if (frame.Width != target.DisplayWidth || frame.Height != target.DisplayHeight)
        {
            StopBranchFollow("捕获画面与物理屏幕范围不同，无法可靠对齐选项。请在悬浮分支页手动选择。", true); return;
        }
        long now = SystemClock.ElapsedRealtime();
        if (frame.TimestampMs < attempt.NotBeforeFrame || frame.TimestampMs > now || now - frame.TimestampMs > 1_500) return;
        attempt.LastFrameTimestamp = frame.TimestampMs;
        if (attempt.AwaitingLineFrame)
        {
            if (frame.IsRepeatedSample || frame.TimestampMs < attempt.CommitAfterTimestamp) return;
            attempt.AwaitingLineFrame = false;
            if (attempt.PendingLine != null && attempt.PendingLineMask != null &&
                !BranchMaskChanged(attempt.PendingLineMask, BranchOptionMask(frame.Bitmap, attempt.PendingLineBounds)))
            { CommitBranchLine(attempt, attempt.PendingLine); return; }
            attempt.PendingLine = null; attempt.PendingLineMask = null; attempt.Consensus = 0;
        }
        if (attempt.AwaitingMenuFrame)
        {
            if (frame.IsRepeatedSample || frame.TimestampMs < attempt.ArmAfterTimestamp) return;
            bool same = attempt.MenuMatch != null && attempt.MenuMatch.Options.All(hit =>
                attempt.OptionMasks.TryGetValue(hit.OptionId, out var expected) &&
                !BranchMaskChanged(expected, BranchOptionMask(frame.Bitmap, hit.Bounds)));
            attempt.AwaitingMenuFrame = false;
            if (same) { ArmBranchOptions(attempt); return; }
            attempt.MenuMatch = null; attempt.Consensus = 0; attempt.OptionMasks.Clear();
        }
        if (attempt.Gate.Stage == BranchFollowStage.AwaitingChoice)
        {
            if (attempt.MenuMatch == null || attempt.MenuMatch.Options.Any(hit =>
                !attempt.OptionMasks.TryGetValue(hit.OptionId, out var expected) ||
                BranchMaskChanged(expected, BranchOptionMask(frame.Bitmap, hit.Bounds))))
                StopBranchFollow("选项画面已变化，已撤下识别框。请在悬浮分支页手动确认。", true);
            return;
        }
        if (!attempt.Gate.TryBeginObservation(now, frame.SourceFrameId, frame.TimestampMs, frame.IsRepeatedSample, out var ticket)) return;
        Bitmap input;
        long request;
        try
        {
            request = ocr.CreateRequest(); input = frame.Bitmap.Copy(Bitmap.Config.Argb8888!, attempt.ExcludedTop > 0)!;
            if (attempt.ExcludedTop > 0)
            {
                using var canvas = new Canvas(input);
                using var paint = new Paint { Color = Color.Black };
                canvas.DrawRect(0, 0, input.Width, Math.Min(input.Height, attempt.ExcludedTop), paint);
            }
        }
        catch (Exception ex) { StopBranchFollow("无法读取分支画面：" + ex.Message, true); return; }
        var kind = Settings.OcrEngine;
        var cancellation = attempt.Cancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await ocr.RecognizeAsync(kind, input, request, cancellation);
                Diagnostics.Ocr(result.Elapsed.TotalMilliseconds);
                Post(() =>
                {
                    try { HandleBranchFollowObservation(attempt, ticket, result, input); }
                    finally { input.Dispose(); }
                });
            }
            catch (OperationCanceledException) { input.Dispose(); }
            catch (Exception ex)
            {
                input.Dispose();
                Post(() => { if (ReferenceEquals(branchFollow, attempt)) StopBranchFollow("分支识别未完成，请手动选择：" + ex.Message, true); });
            }
        });
    }

    void HandleBranchFollowObservation(BranchFollowAttempt attempt, BranchFollowObservationTicket ticket, OcrResult result, Bitmap input)
    {
        if (!CheckBranchFollow(attempt) || !attempt.Gate.CompleteObservation(ticket, SystemClock.ElapsedRealtime())) return;
        if (result.IsTruncated) { attempt.Consensus = 0; attempt.MenuMatch = null; attempt.PendingLine = null; return; }
        var blocks = result.Blocks.Select(b => new OcrBlock
        { Text = b.Text, Score = b.Confidence, Box = b.Polygon.Select(p => new double[] { p.X, p.Y }).ToArray() })
            .Where(b => attempt.ExcludedTop <= 0 || b.Box.Length >= 4 && b.Box.All(p => p[1] >= attempt.ExcludedTop)).ToList();
        var engine = attempt.Intent.Engine;
        if (attempt.Gate.Stage == BranchFollowStage.ReadingOptions)
        {
            if (!BranchFollowPolicy.TryMatchMenu(engine, attempt.Intent.MenuId, blocks, input.Width, input.Height, out var match, out _))
            { attempt.MenuMatch = null; attempt.Consensus = 0; return; }
            attempt.Consensus = attempt.MenuMatch != null && BranchFollowPolicy.AreSameMenu(attempt.MenuMatch, match!) ? attempt.Consensus + 1 : 1;
            attempt.MenuMatch = match;
            if (attempt.Consensus < 2) return;
            attempt.OptionMasks.Clear();
            foreach (var hit in match!.Options) attempt.OptionMasks[hit.OptionId] = BranchOptionMask(input, hit.Bounds);
            // 慢推理不能把旧框直接变为触摸窗口；等待 OCR 结束后取得的新画面再次核对框内文字。
            attempt.AwaitingMenuFrame = true; attempt.ArmAfterTimestamp = SystemClock.ElapsedRealtime() + 1;
            return;
        }
        if (attempt.Gate.Stage != BranchFollowStage.ReadingLine || attempt.SelectedOptionId == null) return;
        if (attempt.Intent.DefaultOption != 0 && DefaultBranchPolicy.ReadMenu(engine, blocks, input.Width, input.Height) is { } observedMenu)
        {
            // 旧菜单掩码必须可见，才有资格证明后来的原框确实消失；全零并不等于发生变化。
            // 每次新菜单观察先清证据，不能在本帧不可比较时沿用更旧的可见菜单。
            attempt.DefaultObservedMenu = null;
            attempt.OptionMasks.Clear();
            foreach (var hit in observedMenu.Options) attempt.OptionMasks[hit.OptionId] = BranchOptionMask(input, hit.Bounds);
            if (attempt.OptionMasks.Values.All(mask => mask.Count(value => value != 0) >= 24))
                attempt.DefaultObservedMenu = observedMenu;
            ShowDefaultBranchWait(attempt, "已看到游戏选项，请先在游戏中选择；新台词核对后自动续播。");
            attempt.PendingLine = null; attempt.Consensus = 0; return;
        }
        string? nodeId;
        bool matched = attempt.Intent.DefaultOption != 0
            ? (DefaultBranchDisplayPolicy.TryGetDisplayAnchor(engine, attempt.Intent.DefaultOption, out _, out _)
                ? DefaultBranchDisplayPolicy.TryMatchDisplayAnchor(engine, attempt.Intent.DefaultOption, blocks, input.Width, input.Height, out nodeId)
                : DefaultBranchPolicy.TryMatchFirst(engine, attempt.Intent.DefaultOption, blocks, input.Width, input.Height, out nodeId))
            : BranchFollowPolicy.TryMatchSelectedLine(engine, attempt.Intent.MenuId, attempt.SelectedOptionId, blocks, input.Width, input.Height, out nodeId, out _);
        if (!matched)
        {
            attempt.PendingLine = null; attempt.Consensus = 0;
            ShowDefaultBranchWait(attempt, "尚未确认这句游戏台词；若游戏已进入正文，请点手动续接核对。"); return;
        }
        attempt.Consensus = attempt.PendingLine == nodeId ? attempt.Consensus + 1 : 1; attempt.PendingLine = nodeId;
        if (attempt.Consensus < 2 || nodeId == null) return;
        var exact = Matcher.FindAll(engine, engine.Current!.SectionId, blocks.Where(b => b.Score >= .85).ToList())
            .FirstOrDefault(c => c.Node.Id == nodeId && Matcher.Normalize(c.Evidence) == Matcher.Normalize(c.Node.Text));
        if (exact?.Region is not { Length: 4 } bounds || bounds.Any(v => !double.IsFinite(v)) ||
            bounds[0] < 0 || bounds[1] < attempt.ExcludedTop || bounds[2] > input.Width || bounds[3] > input.Height ||
            bounds[2] - bounds[0] < 2 || bounds[3] - bounds[1] < 2)
        { attempt.PendingLine = null; attempt.Consensus = 0; return; }
        attempt.PendingLineBounds = new(bounds[0], bounds[1], bounds[2], bounds[3]);
        if (attempt.Intent.DefaultOption != 0 && engine.Pack.ById.TryGetValue(nodeId, out var first) &&
            (Matcher.Normalize(first.Text).Length < 4 || engine.Current!.Options.Any(o => Matcher.Normalize(o.Label) == Matcher.Normalize(first.Text))))
        {
            // 同选项的首句必须已离开原选项框；仅漏识别另一项加上旧说话人，不能证明玩家已点入。
            var old = attempt.DefaultObservedMenu;
            if (old == null || old.Options.Any(o => DefaultBranchPolicy.Overlap(o.Bounds, attempt.PendingLineBounds) ||
                !attempt.OptionMasks.TryGetValue(o.OptionId, out var oldMask) ||
                !BranchMaskChanged(oldMask, BranchOptionMask(input, o.Bounds))))
            {
                attempt.PendingLine = null; attempt.Consensus = 0;
                ShowDefaultBranchWait(attempt, "未取得完整选项切换证据，不能自动认定路线；请点手动续接核对当前句。"); return;
            }
        }
        attempt.PendingLineMask = BranchOptionMask(input, attempt.PendingLineBounds);
        attempt.AwaitingLineFrame = true; attempt.CommitAfterTimestamp = SystemClock.ElapsedRealtime() + 1;
    }

    void CommitBranchLine(BranchFollowAttempt attempt, string nodeId)
    {
        if (attempt.Intent.DefaultOption != 0) { CommitDefaultBranchLine(attempt, nodeId); return; }
        if (!CheckBranchFollow(attempt) || !BranchFollowPolicy.IsEligibleMenu(attempt.Intent.Engine, attempt.Intent.MenuId, out _)) return;
        var engine = attempt.Intent.Engine;
        // 先验证复制的导航快照；正式引擎只接受一次实际字幕定位，不先 SelectBranch，不补 Next。
        var probe = new PlaybackEngine(engine.Pack);
        if (!probe.ImportNavigation(engine.ExportNavigation()) || !probe.ConfirmGameLine(nodeId) ||
            probe.Mode != RunMode.Following || probe.CurrentId != nodeId)
        { StopBranchFollow("该分支暂不能安全续接，请手动确认当前位置。", true); return; }
        var intent = attempt.Intent;
        CancelBranchFollow();
        if (!BranchIntentEnvironmentValid(intent)) return;
        Command(e => { if (!e.ConfirmGameLine(nodeId)) throw new InvalidOperationException(e.NavigationError); });
        if (ReferenceEquals(Engine, engine) && engine.CurrentId == nodeId && engine.Mode == RunMode.Following && BranchIntentEnvironmentValid(intent))
        {
            StartClickFollow(preserveAudio: true);
            RecordBranchAutoReturn(engine, intent.ResumeAutoAfterCommon);
            Diagnostics.Log("分支跟随确认", intent.MenuId + " → " + nodeId);
        }
    }

    void ArmBranchOptions(BranchFollowAttempt attempt)
    {
        if (!CheckBranchFollow(attempt) || attempt.MenuMatch == null) return;
        long now = SystemClock.ElapsedRealtime();
        var intent = attempt.Intent;
        var windows = attempt.MenuMatch.Options.Select(hit => new BranchFollowRect(
            Math.Max(0, Math.Floor(hit.Bounds.Left) - 4), Math.Max(0, Math.Floor(hit.Bounds.Top) - 4),
            Math.Min(intent.Target.DisplayWidth, Math.Ceiling(hit.Bounds.Right) + 4),
            Math.Min(intent.Target.DisplayHeight, Math.Ceiling(hit.Bounds.Bottom) + 4))).ToArray();
        if (windows.Any(r => overlay.IntersectsRectangle((float)r.Left, (float)r.Top, (float)r.Right, (float)r.Bottom)) ||
            windows[0].Left < windows[1].Right && windows[1].Left < windows[0].Right &&
            windows[0].Top < windows[1].Bottom && windows[1].Top < windows[0].Bottom)
        { StopBranchFollow("选项框相互靠得太近或挡住停止按钮，请手动选择。", true); return; }
        long token = GameAdvanceAccessibilityService.BeginSession(intent.GamePackage, intent.Target.DisplayWidth,
            intent.Target.DisplayHeight, intent.Target.Rotation);
        if (token == 0) { StopBranchFollow(GameAdvanceAccessibilityService.LastFailureReason, true); return; }
        attempt.ServiceToken = token;
        if (!attempt.Gate.Arm(now)) { StopBranchFollow("选项识别已过期，请手动选择。", true); return; }
        try
        {
            foreach (var (hit, index) in attempt.MenuMatch.Options.Select((hit, index) => (hit, index)))
            {
                var rect = windows[index];
                var region = new ScreenRegion((float)(rect.Left / intent.Target.DisplayWidth), (float)(rect.Top / intent.Target.DisplayHeight),
                    (float)(rect.Width / intent.Target.DisplayWidth), (float)(rect.Height / intent.Target.DisplayHeight));
                var surface = new TapFollowOverlay(context, intent.Target.DisplayWidth, intent.Target.DisplayHeight, region,
                    (x, y) => OnBranchOptionTap(attempt, hit, x, y), showHint: false);
                attempt.Regions.Add(surface); surface.Show();
            }
        }
        catch (Exception ex) { StopBranchFollow("选项跟随窗口无法显示，请手动选择：" + ex.Message, true); return; }
        Status = "分支跟随：请轻点游戏中的选项文字；选择后核对实际字幕再继续配音。"; Notify();
        Diagnostics.Log("分支选项已识别", attempt.Intent.MenuId);
    }

    void OnBranchOptionTap(BranchFollowAttempt attempt, BranchFollowOptionHit hit, float x, float y) => Post(() =>
    {
        if (!CheckBranchFollow(attempt) || !hit.Bounds.Contains(x, y) || overlay.ContainsPoint(x, y)) return;
        long now = SystemClock.ElapsedRealtime();
        if (now - attempt.LastFrameTimestamp > 1_500 || !attempt.Gate.TryBeginTap(now)) return;
        attempt.SelectedOptionId = hit.OptionId; attempt.PendingLine = null; attempt.Consensus = 0;
        foreach (var region in attempt.Regions) region.Suspend();
        long generation = attempt.Gate.Generation;
        var cancellation = attempt.Cancellation.Token;
        // 同普通点按一样，先同步撤窗，再仅向用户原坐标转发一次。
        main.PostDelayed(() => Post(() =>
        {
            if (!CheckBranchFollow(attempt) || attempt.Gate.Generation != generation || attempt.Gate.Stage != BranchFollowStage.Dispatching) return;
            _ = CompleteBranchOptionTapAsync(attempt, generation, x, y, cancellation);
        }), 80);
    });

    async Task CompleteBranchOptionTapAsync(BranchFollowAttempt attempt, long generation, float x, float y, CancellationToken cancellation)
    {
        try
        {
            var result = await GameAdvanceAccessibilityService.TapAsync(attempt.ServiceToken, x, y, cancellation);
            Post(() =>
            {
                if (!CheckBranchFollow(attempt) || attempt.Gate.Generation != generation) return;
                long now = SystemClock.ElapsedRealtime();
                if (!attempt.Gate.CompleteTap(generation, result.Completed, now))
                { StopBranchFollow(result.Reason + " 请手动确认分支；不会补点。", true); return; }
                foreach (var region in attempt.Regions) region.Dispose(); attempt.Regions.Clear();
                attempt.NotBeforeFrame = now + 200; attempt.LastFrameTimestamp = 0;
                Status = "分支跟随：等待游戏实际字幕确认，不会重复点击。"; Notify();
                Diagnostics.Log("分支用户点击已转发", attempt.SelectedOptionId ?? "");
            });
        }
        catch (Exception ex)
        { Post(() => { if (ReferenceEquals(branchFollow, attempt)) StopBranchFollow("分支点击未确认：" + ex.Message + " 请手动确认，不会补点。", true); }); }
    }

    static byte[] BranchOptionMask(Bitmap bitmap, BranchFollowRect rect)
    {
        int x = Math.Clamp((int)Math.Floor(rect.Left), 0, bitmap.Width - 1), y = Math.Clamp((int)Math.Floor(rect.Top), 0, bitmap.Height - 1);
        int width = Math.Clamp((int)Math.Ceiling(rect.Right) - x, 1, bitmap.Width - x), height = Math.Clamp((int)Math.Ceiling(rect.Bottom) - y, 1, bitmap.Height - y);
        var crop = Bitmap.CreateBitmap(bitmap, x, y, width, height)!;
        using var ownedCrop = ReferenceEquals(crop, bitmap) ? null : crop;
        return TextMask(crop);
    }

    static bool BranchMaskChanged(byte[] previous, byte[] current)
    {
        if (previous.Length != current.Length) return true;
        int visible = 0;
        for (int i = 0; i < previous.Length; i++)
        {
            if (previous[i] != current[i]) return true;
            if (previous[i] != 0) visible++;
        }
        // 不把两个空掩码或只差一个字的长台词当作相同。动画导致变化时回退，不能放宽成旧句提交。
        return visible < 24;
    }

    void SelectOverlayBranch(PlaybackEngine expectedEngine, ChoiceOption option)
    {
        if (TryResumeBranchFromBrowse(expectedEngine, option)) return;
        if (ReferenceEquals(Engine, expectedEngine) && TryBeginInteractionFromOverlay(expectedEngine, option.Id)) return;
        var intent = branchResumeIntent;
        bool resume = intent != null && ReferenceEquals(expectedEngine, intent.Engine) && expectedEngine.CurrentId == intent.MenuId &&
            expectedEngine.Mode == RunMode.Choice && BranchIntentEnvironmentValid(intent);
        Command(e =>
        {
            if (!ReferenceEquals(e, expectedEngine)) throw new InvalidOperationException("章节已变化，请重新选择分支。");
            int i = e.AvailableOptions.FindIndex(o => ReferenceEquals(o, option));
            if (i < 0) throw new InvalidOperationException("分支选项已变化。");
            overlayBrowseDirectory = false; e.SelectBranch(i);
        });
        if (resume && intent != null && BranchIntentEnvironmentValid(intent) && Engine == expectedEngine &&
            expectedEngine.Mode == RunMode.Following && expectedEngine.Current is { Kind: "line" } node && node.PathId == option.PathId)
        {
            StartClickFollow(preserveAudio: true);
            RecordBranchAutoReturn(expectedEngine, intent.ResumeAutoAfterCommon);
        }
    }
}
