#if DEBUG
using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using PgrVoice.Following;
using System.Text;
using System.Text.Json;
using Path = System.IO.Path;

namespace PgrVoice.AndroidApp.Diagnostics;

/// <summary>
/// 仅调试构建：将实际章节文字绘制成游戏字幕，通过已授权的真实屏幕捕获与 OCR 回放。
/// 不订阅 CapturedFrame，不注入 OCR 结果，不替代游戏、音频混音或手机性能验收。
/// </summary>
[Activity(Name = "cn.pgrvoice.player.SubtitleReplayActivity", Exported = true, NoHistory = true,
    ScreenOrientation = ScreenOrientation.Landscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout)]
public sealed class SubtitleReplayActivity : Activity
{
    readonly CancellationTokenSource lifetime = new();
    readonly List<ReplaySample> timeline = new();
    readonly List<ReplayStageResult> stages = new();
    readonly List<ReplayPlayRequest> audioRequests = new();
    AppSession session = null!;
    SubtitleScene scene = null!;
    PlaybackEngine? observedEngine;
    Action<Node?>? playObserver;
    string outputDirectory = "";
    long startedAt, firstCapturedCount;
    int stageIndex = -1;
    bool running;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.Fullscreen | WindowManagerFlags.KeepScreenOn);
#pragma warning disable CS0618, CA1422
        Window!.DecorView.SystemUiVisibility = (StatusBarVisibility)(SystemUiFlags.Fullscreen | SystemUiFlags.HideNavigation |
            SystemUiFlags.ImmersiveSticky | SystemUiFlags.LayoutFullscreen | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutStable);
#pragma warning restore CS0618, CA1422
        session = AppSession.Get(this);
        outputDirectory = FilesDir!.AbsolutePath;
        scene = new SubtitleScene(this);
        SetContentView(scene);
        scene.Post(async () => await RunReplayAsync());
    }

    protected override void OnResume()
    {
        base.OnResume();
        session?.SetUiVisible(false); // 这是被捕获的测试游戏画面，不是播放器设置页面。
    }

    protected override void OnPause()
    {
        if (running) lifetime.Cancel();
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        lifetime.Cancel();
        if (observedEngine != null && playObserver != null) observedEngine.PlayRequested -= playObserver;
        base.OnDestroy();
    }

    async Task RunReplayAsync()
    {
        if (running) return;
        running = true; startedAt = SystemClock.ElapsedRealtime();
        float oldVolume = session.Settings.Volume;
        ReplayScript? script = null;
        string? error = null;
        int setupPlayRequests = 0;
        string diagnosticsBefore = session.Diagnostics.Export();
        long errorsBefore = ErrorCount();
        try
        {
            string file = Path.GetFileName(Intent?.GetStringExtra("script") ?? "replay-script.json");
            script = JsonSerializer.Deserialize<ReplayScript>(File.ReadAllText(Path.Combine(outputDirectory, file)), Json.Options)
                ?? throw new InvalidDataException("回放脚本为空。");
            Validate(script);
            if (!session.Screen.CaptureActive) throw new InvalidOperationException("请先在主界面通过系统对话框授权屏幕捕获，再启动真实链路回放。");
            session.HideOverlay();
            session.Settings.Volume = 0; session.ApplyAudioSettings();
            session.SetMode(FollowMode.Manual);
            session.LoadPack(script.PackId);
            observedEngine = session.Engine ?? throw new InvalidOperationException("测试章节没有导入。");
            playObserver = node => audioRequests.Add(new ReplayPlayRequest
            {
                AtMs = Elapsed, Stage = stageIndex, NodeId = node?.Id,
                AudioExists = node?.Audio != null && File.Exists(observedEngine.Pack.ResolveAudio(node)),
                Notice = node == null ? "" : observedEngine.Pack.AudioNotice(node)
            });
            observedEngine.PlayRequested += playObserver;
            var start = observedEngine.Pack.ById.GetValueOrDefault(script.StartNodeId)
                ?? throw new InvalidDataException("脚本起始台词不属于已导入章节。");
            if (start.Kind != "line") throw new InvalidDataException("回放起点必须是一句台词。");
            scene.Set("准备真实捕获与离线识别", start.Speaker, start.Text, script.AnimateNext);
            await WaitForCaptureSizeAsync();
            var size = session.CaptureSize;
            int[] location = new int[2]; scene.GetLocationOnScreen(location);
            session.SetRegion(size.Width, size.Height, new ScreenRegion(
                (location[0] + scene.Width * SubtitleScene.RegionLeft) / size.Width,
                (location[1] + scene.Height * SubtitleScene.RegionTop) / size.Height,
                scene.Width * SubtitleScene.RegionWidth / size.Width,
                scene.Height * SubtitleScene.RegionHeight / size.Height));
            session.ConfirmLine(start.Id, session.Engine?.Mode == RunMode.Original);
            session.SetMode(FollowMode.Automatic);
            session.ConfirmCurrent();
            if (!session.IsArmed) throw new InvalidOperationException("回放无法启用真实自动跟随：" + session.Status);
            setupPlayRequests = audioRequests.Count;
            firstCapturedCount = session.ObservedFrameCount;
            await ObserveForAsync(script.WarmupMs, "准备", lifetime.Token);
            if (session.Engine?.CurrentId != start.Id || audioRequests.Count != setupPlayRequests)
                throw new InvalidOperationException("仅显示当前句的准备阶段发生了意外推进或重复播放。");

            for (int i = 0; i < script.Frames.Count; i++)
            {
                stageIndex = i;
                var frame = script.Frames[i];
                int playsBefore = audioRequests.Count, historyBefore = session.Engine!.History.Count;
                long ocrBefore = DiagnosticCount("ocrRuns");
                long began = Elapsed;
                scene.Set(frame.Label, frame.Speaker, frame.Text, script.AnimateNext);
                Perform(frame);
                await ObserveForAsync(frame.HoldMs, frame.Label, lifetime.Token);
                var engine = session.Engine!;
                int playDelta = audioRequests.Count - playsBefore, historyDelta = engine.History.Count - historyBefore;
                bool passed = (frame.ExpectNode == null || engine.CurrentId == frame.ExpectNode) &&
                    (frame.ExpectMode == null || engine.Mode.ToString() == frame.ExpectMode) &&
                    (frame.ExpectArmed == null || session.IsArmed == frame.ExpectArmed) &&
                    (frame.ExpectPlayRequests == null || playDelta == frame.ExpectPlayRequests) &&
                    (frame.ExpectHistoryDelta == null || historyDelta == frame.ExpectHistoryDelta) &&
                    (frame.ExpectCandidateId == null || session.Candidates.Any(c => c.Node.Id == frame.ExpectCandidateId));
                stages.Add(new ReplayStageResult
                {
                    Index = i, Label = frame.Label, StartedMs = began, EndedMs = Elapsed, Passed = passed,
                    ExpectedNode = frame.ExpectNode, ActualNode = engine.CurrentId, ExpectedMode = frame.ExpectMode,
                    ActualMode = engine.Mode.ToString(), PlayRequests = playDelta, ExpectedPlayRequests = frame.ExpectPlayRequests,
                    HistoryDelta = historyDelta, ExpectedHistoryDelta = frame.ExpectHistoryDelta, Armed = session.IsArmed,
                    OcrRunsBefore = ocrBefore, OcrRunsAfter = DiagnosticCount("ocrRuns"), CaptureActive = session.Screen.CaptureActive,
                    OcrText = session.OcrText, CandidateIds = session.Candidates.Select(c => c.Node.Id).ToArray(),
                    Status = session.Status, RenderedLines = scene.RenderedLines
                });
                WriteProgress();
                if (!passed && script.StopOnFailure) throw new InvalidOperationException("真实回放断言失败：" + frame.Label);
            }
            if (ErrorCount() > errorsBefore) throw new InvalidOperationException("回放期间会话记录了识别、音频或保存错误，请查看 diagnostics 和状态时间线。");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            running = false;
            var finalState = new
            {
                stage = stageIndex, nodeId = session.Engine?.CurrentId, mode = session.Engine?.Mode.ToString(),
                history = session.Engine?.History.Count, armed = session.IsArmed, captureActive = session.Screen.CaptureActive,
                ocrText = session.OcrText, candidateIds = session.Candidates.Select(c => c.Node.Id).ToArray(), status = session.Status
            };
            session.Command(e => e.PauseForBrowse());
            session.Settings.Volume = oldVolume; session.ApplyAudioSettings();
            bool passed = error == null && stages.Count == script?.Frames.Count && stages.All(s => s.Passed);
            var capture = session.CaptureSize;
            var report = new
            {
                passed, script = script?.Name, error, durationMs = Elapsed,
                pipeline = "真实 MediaProjection → AppSession → 离线 OCR → 共享剧情核心 → Android 音频输出",
                limitations = "测试字幕由应用绘制；配音音量为 0，PlayRequested 是请求计数，不能证明扬声器实际出声、游戏混音、手机发热或真实游戏识别准确率。",
                model = Build.Model, manufacturer = Build.Manufacturer, androidApi = (int)Build.VERSION.SdkInt,
                captureSize = new { width = capture.Width, height = capture.Height }, observedFrames = session.ObservedFrameCount - firstCapturedCount,
                setupPlayRequests, audioRequests, stages, timeline, finalState, diagnosticsBefore, diagnostics = session.Diagnostics.Export()
            };
            Json.Save(Path.Combine(outputDirectory, "replay-report.json"), report);
            if (!IsDestroyed) scene.Set(passed ? "回放通过，结果已保存" : "回放未通过，请查看 replay-report.json", "设备端调试回放", passed ? "所有场景断言通过。" : error?.Split('\n')[0] ?? "部分断言失败。", false);
        }
    }

    async Task WaitForCaptureSizeAsync()
    {
        long deadline = SystemClock.ElapsedRealtime() + 15_000;
        while (SystemClock.ElapsedRealtime() < deadline)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            var size = session.CaptureSize;
            if (scene.Width > 0 && scene.Height > 0 && size.Width > 0 && size.Height > 0 &&
                Math.Abs(size.Width / (double)size.Height - scene.Width / (double)scene.Height) < .08)
            {
                // 等旋转/捕获尺寸更新通知处理完，避免授权会话变化清除刚设置的确认状态。
                await Task.Delay(400, lifetime.Token);
                if (session.CaptureSize == size) return;
            }
            await Task.Delay(100, lifetime.Token);
        }
        throw new TimeoutException("未等到与测试画面比例一致的真实捕获尺寸。请确认共享的是整个屏幕或本测试应用。");
    }

    void Perform(ReplayFrame frame)
    {
        switch (frame.Action)
        {
            case "": case "none": break;
            case "position": session.ConfirmLine(frame.NodeId ?? throw new InvalidDataException("定位场景缺少 nodeId。"), session.Engine?.Mode == RunMode.Original); break;
            case "next": session.Command(e => e.Next(true), true); break;
            case "original": session.Command(e => e.EnterOriginal()); break;
            case "manual": session.SetMode(FollowMode.Manual); break;
            case "automatic": session.SetMode(FollowMode.Automatic); break;
            case "confirm": session.ConfirmCurrent(); break;
            case "pause": session.Command(e => e.PauseForBrowse()); break;
            case "ocr": session.RequestOcr(); break; // 正常结果仍跳到候选页并触发 OnPause；报告保留此中断。
            case "stop": session.Stop(); break;
            default: throw new InvalidDataException("不支持的回放动作：" + frame.Action);
        }
    }

    async Task ObserveForAsync(int milliseconds, string label, CancellationToken token)
    {
        long end = SystemClock.ElapsedRealtime() + milliseconds;
        do
        {
            token.ThrowIfCancellationRequested();
            var engine = session.Engine;
            timeline.Add(new ReplaySample
            {
                AtMs = Elapsed, Stage = stageIndex, Label = label, NodeId = engine?.CurrentId,
                Mode = engine?.Mode.ToString(), HistoryCount = engine?.History.Count ?? 0,
                Armed = session.IsArmed, PlayRequests = audioRequests.Count, CapturedFrames = session.ObservedFrameCount,
                OcrText = session.OcrText, Status = session.Status
            });
            await Task.Delay((int)Math.Min(200, Math.Max(1, end - SystemClock.ElapsedRealtime())), token);
        } while (SystemClock.ElapsedRealtime() < end);
    }

    void WriteProgress() => Json.Save(Path.Combine(outputDirectory, "replay-progress.json"), new { complete = false, stages, timeline, audioRequests });
    long ErrorCount() => DiagnosticCount("errors");
    long DiagnosticCount(string name)
    {
        using var document = JsonDocument.Parse(session.Diagnostics.Export());
        return document.RootElement.GetProperty(name).GetInt64();
    }
    long Elapsed => SystemClock.ElapsedRealtime() - startedAt;
    static void Validate(ReplayScript script)
    {
        if (string.IsNullOrWhiteSpace(script.PackId) || string.IsNullOrWhiteSpace(script.StartNodeId) || script.Frames.Count is < 1 or > 64 ||
            script.WarmupMs is < 1000 or > 30_000 || script.Frames.Any(f => f.HoldMs is < 100 or > 30_000) ||
            script.Frames.Sum(f => f.HoldMs) + script.WarmupMs > 600_000)
            throw new InvalidDataException("回放脚本缺少章节、起点或场景时长超限。");
    }

    public sealed class ReplayScript
    {
        public string Name { get; set; } = "第 3 章真实识别链路回放";
        public string PackId { get; set; } = "pgr-ch03";
        public string StartNodeId { get; set; } = "";
        public int WarmupMs { get; set; } = 8000;
        public bool AnimateNext { get; set; } = true;
        public bool StopOnFailure { get; set; } = true;
        public List<ReplayFrame> Frames { get; set; } = new();
    }
    public sealed class ReplayFrame
    {
        public string Label { get; set; } = "";
        public string Speaker { get; set; } = "";
        public string Text { get; set; } = "";
        public int HoldMs { get; set; } = 8000;
        public string Action { get; set; } = "none";
        public string? NodeId { get; set; }
        public string? ExpectNode { get; set; }
        public string? ExpectMode { get; set; }
        public bool? ExpectArmed { get; set; }
        public int? ExpectPlayRequests { get; set; }
        public int? ExpectHistoryDelta { get; set; }
        public string? ExpectCandidateId { get; set; }
    }
    public sealed class ReplaySample
    {
        public long AtMs { get; set; }
        public int Stage { get; set; }
        public string Label { get; set; } = "";
        public string? NodeId { get; set; }
        public string? Mode { get; set; }
        public int HistoryCount { get; set; }
        public bool Armed { get; set; }
        public int PlayRequests { get; set; }
        public long CapturedFrames { get; set; }
        public string OcrText { get; set; } = "";
        public string Status { get; set; } = "";
    }
    public sealed class ReplayPlayRequest
    {
        public long AtMs { get; set; }
        public int Stage { get; set; }
        public string? NodeId { get; set; }
        public bool AudioExists { get; set; }
        public string Notice { get; set; } = "";
    }
    public sealed class ReplayStageResult
    {
        public int Index { get; set; }
        public string Label { get; set; } = "";
        public long StartedMs { get; set; }
        public long EndedMs { get; set; }
        public bool Passed { get; set; }
        public string? ExpectedNode { get; set; }
        public string? ActualNode { get; set; }
        public string? ExpectedMode { get; set; }
        public string? ActualMode { get; set; }
        public int PlayRequests { get; set; }
        public int? ExpectedPlayRequests { get; set; }
        public int HistoryDelta { get; set; }
        public int? ExpectedHistoryDelta { get; set; }
        public bool Armed { get; set; }
        public long OcrRunsBefore { get; set; }
        public long OcrRunsAfter { get; set; }
        public bool CaptureActive { get; set; }
        public string OcrText { get; set; } = "";
        public string[] CandidateIds { get; set; } = [];
        public string Status { get; set; } = "";
        public int RenderedLines { get; set; }
    }

    sealed class SubtitleScene : View
    {
        public const float RegionLeft = .08f, RegionTop = .68f, RegionWidth = .84f, RegionHeight = .27f;
        readonly Paint paint = new(PaintFlags.AntiAlias);
        string label = "正在准备", speaker = "", text = "";
        bool animate;
        public int RenderedLines { get; private set; }
        public SubtitleScene(Activity activity) : base(activity) { SetBackgroundColor(Color.Black); }
        public void Set(string label, string speaker, string text, bool animate)
        { this.label = label; this.speaker = speaker; this.text = text; this.animate = animate; Invalidate(); }
        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            canvas.DrawColor(Color.Black);
            paint.Color = Color.Rgb(93, 154, 180); paint.TextSize = Width * .014f;
            canvas.DrawText("战双配音 · 设备端真实字幕回放", Width * .06f, Height * .10f, paint);
            paint.Color = Color.Rgb(130, 145, 155); paint.TextSize = Width * .011f;
            canvas.DrawText(label, Width * .06f, Height * .17f, paint);
            paint.Color = Color.Rgb(10, 17, 23);
            canvas.DrawRect(Width * RegionLeft, Height * RegionTop, Width * (RegionLeft + RegionWidth), Height * (RegionTop + RegionHeight), paint);
            paint.Color = Color.White; paint.TextSize = Width * .023f;
            canvas.DrawText(speaker, Width * .10f, Height * .735f, paint);
            paint.TextSize = Width * .024f;
            var lines = Wrap(text, Width * .79f);
            RenderedLines = lines.Count;
            float y = Height * .80f, step = paint.TextSize * 1.35f;
            foreach (string line in lines) { canvas.DrawText(line, Width * .10f, y, paint); y += step; }
            if (animate)
            {
                paint.Color = ((SystemClock.ElapsedRealtime() / 350) % 2) == 0 ? Color.White : Color.Rgb(95, 95, 95);
                paint.TextSize = Width * .010f;
                canvas.DrawText("NEXT", Width * .944f, Height * .96f, paint);
                PostInvalidateDelayed(100);
            }
        }
        List<string> Wrap(string value, float width)
        {
            var result = new List<string>();
            foreach (string paragraph in value.Replace("\r", "").Split('\n'))
            {
                var current = new StringBuilder();
                foreach (char character in paragraph)
                {
                    if (current.Length > 0 && paint.MeasureText(current.ToString() + character) > width)
                    { result.Add(current.ToString()); current.Clear(); }
                    current.Append(character);
                }
                result.Add(current.ToString());
            }
            return result;
        }
        protected override void Dispose(bool disposing) { if (disposing) paint.Dispose(); base.Dispose(disposing); }
    }
}
#endif
