using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using PgrVoice.AndroidApp.Contracts;
using System.Diagnostics.CodeAnalysis;

namespace PgrVoice.AndroidApp.Platform;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback | ForegroundService.TypeMediaProjection)]
public sealed class VoiceForegroundService : Service
{
    private const string ChannelId = "pgr_voice_session";
    private const int NotificationId = 3101;
    private const string ActionStart = "pgr.voice.START";
    private const string ActionCapture = "pgr.voice.CAPTURE";
    private const string ActionStopCapture = "pgr.voice.STOP_CAPTURE";
    private const string ActionStop = "pgr.voice.STOP";
    private const string ExtraCaptureData = "capture_data";
    private const string ExtraCaptureCode = "capture_code";
    private static VoiceForegroundService? current;
    private static long nextSessionId;
    private static readonly List<Action> readyActions = new();
    private HandlerThread? captureThread;
    private Handler? captureHandler;
    private Handler? mainHandler;
    private MediaProjection? projection;
    private ProjectionCallback? projectionCallback;
    private VirtualDisplay? virtualDisplay;
    private ImageReader? imageReader;
    private FrameListener? frameListener;
    private ScreenOffReceiver? screenOffReceiver;
    private DisplayManager? displayManager;
    private PhysicalDisplayListener? displayListener;
    private volatile ScreenDisplayState displayState = ScreenDisplayState.Unavailable;
    private int captureWidth, captureHeight, densityDpi;
    private long sessionId;
    // One retained pixel buffer, owned only by the capture looper. Static surfaces are not
    // required to enqueue another Image, so consumers sample this latest buffer at 5 Hz.
    private Bitmap? latestFrame;
    private long latestFrameTime, latestFrameSession, latestFrameId, nextSourceFrameId;
    private ScreenDisplayState latestFrameDisplay = ScreenDisplayState.Unavailable;
    private bool latestFrameDelivered, framePumpQueued;
    private Action? framePump;
    private volatile bool destroyed, stopping;
    private bool foreground;
    private volatile bool captureActive;
    private string? pendingNotificationText, pendingNotificationPage;
    private string? displayedNotificationText, displayedNotificationPage;
    private long lastNotificationTime;
    private bool notificationUpdateQueued;

    public static bool CaptureActive => current?.captureActive == true;
    public static bool IsRunning => current?.foreground == true && !current.stopping;
    public static long CaptureSessionId => current?.sessionId ?? 0;
    public static ScreenDisplayState CurrentDisplayState => current?.displayState ?? ScreenDisplayState.Unavailable;
    public static event Action<CapturedFrame>? FrameAvailable;
    public static event Action<string>? CaptureStopped;
    public static event Action<ScreenDisplayState>? DisplayChanged;
    public static event Action? StopRequested;

    // A notification tap is an explicit user action and can legally return to the app
    // when Android blocks an unsolicited Activity launch from the background.
    public static void UpdateNotification(string message, string? page = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var service = current;
        service?.mainHandler?.Post(() => service.QueueNotification(message, page));
    }

    // Call from a user-visible Activity or a user interaction. No automatic background restart.
    public static void EnsureStarted(Context context) => Start(context, new Intent(context, typeof(VoiceForegroundService)).SetAction(ActionStart));
    public static void EnsureStarted(Context context, Action ready)
    {
        var service = current;
        if (IsRunning && service != null) { service.mainHandler?.Post(ready); return; }
        lock (readyActions) readyActions.Add(ready);
        try { EnsureStarted(context); }
        catch { lock (readyActions) readyActions.Remove(ready); throw; }
    }
    public static void StartCapture(Context context, Result resultCode, Intent resultData)
    {
        if (resultCode != Result.Ok) throw new InvalidOperationException("尚未授权屏幕捕获。");
        var intent = new Intent(context, typeof(VoiceForegroundService)).SetAction(ActionCapture);
        intent.PutExtra(ExtraCaptureCode, (int)resultCode);
        intent.PutExtra(ExtraCaptureData, resultData);
        Start(context, intent);
    }
    public static void StopCapture(Context context)
    {
        var service = current;
        service?.captureHandler?.Post(() => service.ReleaseCapture("已停止屏幕识别", true));
    }
    public static void StopAll(Context context)
    {
        var service = current;
        if (service != null) service.mainHandler?.Post(service.StopSession);
    }
    private static void Start(Context context, Intent intent)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) context.StartForegroundService(intent);
        else context.StartService(intent);
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(FrameListener))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ProjectionCallback))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ScreenOffReceiver))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(PhysicalDisplayListener))]
    public override void OnCreate()
    {
        base.OnCreate();
        current = this;
        mainHandler = new Handler(Looper.MainLooper!);
        captureThread = new HandlerThread("PgrVoice.ScreenCapture");
        captureThread.Start();
        captureHandler = new Handler(captureThread.Looper!);
        framePump = PumpFrames;
        // ImageReader can retain this peer in already queued Java Runnables after a resize.
        // Keep one live listener for the service rather than disposing a peer on every reader swap.
        frameListener = new FrameListener(this);
        displayManager = (DisplayManager)GetSystemService(DisplayService)!;
        displayListener = new PhysicalDisplayListener(this);
        displayManager.RegisterDisplayListener(displayListener, captureHandler);
        captureHandler.Post(RefreshPhysicalDisplay);
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "剧情配音运行状态", NotificationImportance.Low)
        { Description = "显示配音和屏幕识别状态，可随时停止" });
        screenOffReceiver = new ScreenOffReceiver(this);
        var filter = new IntentFilter(Intent.ActionScreenOff);
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) RegisterReceiver(screenOffReceiver, filter, ReceiverFlags.NotExported);
        else RegisterReceiver(screenOffReceiver, filter);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop) { StopSession(); return StartCommandResult.NotSticky; }
        if (intent?.Action == ActionStopCapture) { StopCapture(this); return StartCommandResult.NotSticky; }
        bool requestingCapture = intent?.Action == ActionCapture;
        Promote(requestingCapture || captureActive);
        Action[] callbacks;
        lock (readyActions) { callbacks = readyActions.ToArray(); readyActions.Clear(); }
        foreach (var callback in callbacks)
            try { callback(); } catch (Exception ex) { Android.Util.Log.Warn("PgrVoice", "播放启动回调失败：" + ex.Message); }
        if (requestingCapture)
        {
#pragma warning disable CS0618, CA1422
            var resultData = intent!.GetParcelableExtra(ExtraCaptureData) as Intent;
#pragma warning restore CS0618, CA1422
            var resultCode = (Result)intent!.GetIntExtra(ExtraCaptureCode, (int)Result.Canceled);
            // Never retain or persist the grant. Android 14 grants permit one projection/display session only.
            intent.RemoveExtra(ExtraCaptureData);
            if (resultData != null && resultCode == Result.Ok)
                captureHandler?.Post(() => BeginCapture(resultCode, resultData));
            else CaptureEnded("屏幕授权无效，请重新开启识别。");
        }
        return StartCommandResult.NotSticky;
    }

    private void Promote(bool capturing)
    {
        if (destroyed) return;
        var message = capturing ? "正在识别游戏字幕 · 点此打开控制" : "配音控制已就绪 · 点此打开";
        pendingNotificationText = displayedNotificationText = message;
        pendingNotificationPage = displayedNotificationPage = null;
        var notification = BuildNotification(message, null);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback |
                (capturing ? ForegroundService.TypeMediaProjection : (ForegroundService)0));
        else StartForeground(NotificationId, notification);
        lastNotificationTime = SystemClock.ElapsedRealtime();
        foreground = true;
    }

    private Notification BuildNotification(string message, string? page)
    {
        var openIntent = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        openIntent?.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
        if (page != null) openIntent?.PutExtra("page", page);
        PendingIntent? open = openIntent == null ? null : PendingIntent.GetActivity(this, 0, openIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var stop = PendingIntent.GetService(this, 1, new Intent(this, typeof(VoiceForegroundService)).SetAction(ActionStop),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var builder = new Notification.Builder(this, ChannelId)
            .SetContentTitle("战双剧情配音")
            .SetContentText(message)
            .SetStyle(new Notification.BigTextStyle().BigText(message))
            .SetSmallIcon(Android.Resource.Drawable.IcMediaPlay)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetContentIntent(open);
        builder.AddAction(new Notification.Action.Builder(Icon.CreateWithResource(this, Android.Resource.Drawable.IcMediaPause), "停止", stop).Build());
        return builder.Build();
    }

    private void QueueNotification(string message, string? page)
    {
        if (destroyed || stopping || !foreground) return;
        pendingNotificationText = message;
        pendingNotificationPage = page;
        if (notificationUpdateQueued ||
            (message == displayedNotificationText && page == displayedNotificationPage)) return;
        long delay = Math.Max(0, 1000 - (SystemClock.ElapsedRealtime() - lastNotificationTime));
        if (delay == 0) { FlushNotification(); return; }
        notificationUpdateQueued = true;
        mainHandler?.PostDelayed(FlushNotification, delay);
    }

    private void FlushNotification()
    {
        notificationUpdateQueued = false;
        if (destroyed || stopping || !foreground || pendingNotificationText == null ||
            (pendingNotificationText == displayedNotificationText && pendingNotificationPage == displayedNotificationPage)) return;
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.Notify(NotificationId, BuildNotification(pendingNotificationText, pendingNotificationPage));
        displayedNotificationText = pendingNotificationText;
        displayedNotificationPage = pendingNotificationPage;
        lastNotificationTime = SystemClock.ElapsedRealtime();
    }

    private void BeginCapture(Result code, Intent data)
    {
        if (destroyed || stopping) { data.Dispose(); return; }
        ReleaseCapture("更换屏幕捕获会话", true, false);
        try
        {
            var manager = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
            projection = manager.GetMediaProjection((int)code, data) ?? throw new InvalidOperationException("系统没有返回屏幕捕获会话。");
            sessionId = Interlocked.Increment(ref nextSessionId);
            projectionCallback = new ProjectionCallback(this, sessionId);
            projection.RegisterCallback(projectionCallback, captureHandler);
            RefreshPhysicalDisplay();
            var (width, height, dpi) = ScreenSize();
            densityDpi = dpi;
            ConfigureReader(width, height, true);
            captureActive = true;
            QueueFramePump(0);
        }
        catch (Exception ex) { ReleaseCapture("无法开始屏幕识别：" + ex.Message, true); }
        finally { data.Dispose(); }
    }

    private (int Width, int Height, int Dpi) ScreenSize()
    {
        var windows = GetSystemService(WindowService)!.JavaCast<IWindowManager>();
        var metrics = Resources!.DisplayMetrics!;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var bounds = windows.MaximumWindowMetrics.Bounds;
            return (Math.Max(1, bounds.Width()), Math.Max(1, bounds.Height()), (int)metrics.DensityDpi);
        }
        using var realMetrics = new DisplayMetrics();
#pragma warning disable CS0618, CA1422
        windows.DefaultDisplay!.GetRealMetrics(realMetrics);
#pragma warning restore CS0618, CA1422
        return (realMetrics.WidthPixels, realMetrics.HeightPixels, (int)realMetrics.DensityDpi);
    }

    private void RefreshPhysicalDisplay()
    {
        if (destroyed || stopping) return;
        PhysicalDisplayProfile? profile = null;
        try
        {
            // A shared app can remain 16:9 on both panels. Query the physical display's active
            // mode, not WindowMetrics, Resources.DisplayMetrics or the captured bitmap.
            var windows = GetSystemService(WindowService)!.JavaCast<IWindowManager>();
#pragma warning disable CS0618, CA1422
            int displayId = windows.DefaultDisplay?.DisplayId ?? Android.Views.Display.DefaultDisplay;
#pragma warning restore CS0618, CA1422
            var display = displayManager?.GetDisplay(displayId);
            var mode = display?.GetMode();
            if (display != null && mode != null)
                profile = new(display.DisplayId, mode.PhysicalWidth, mode.PhysicalHeight, (int)display.Rotation);
        }
        catch (Exception ex) { Android.Util.Log.Warn("PgrVoice", "读取物理显示形态失败：" + ex.Message); }
        var next = displayState.WithProfile(profile);
        if (ReferenceEquals(next, displayState)) return;
        displayState = next;
        ClearLatestFrame();
        if (captureActive && virtualDisplay != null)
        {
            try
            {
                // An app-only projection can keep the same dimensions across a physical fold.
                // Replace its reader surface as well, so an old queued Image cannot be relabelled
                // with the new physical profile. The authorized VirtualDisplay itself is retained.
                ConfigureReader(captureWidth, captureHeight, false, force: true);
            }
            catch (Exception ex) { ReleaseCapture("屏幕形态变化后捕获中止，请重新开启：" + ex.Message, true); }
        }
        // Posted before later captured frames. Consumers also compare the immutable state
        // directly so a frame/OCR already queued before the switch cannot be accepted.
        mainHandler?.Post(() =>
        {
            if (!destroyed && ReferenceEquals(current, this) && displayState == next)
                DisplayChanged?.Invoke(next);
        });
    }

    private void ConfigureReader(int width, int height, bool first, bool force = false)
    {
        if (destroyed || stopping || projection == null || width < 1 || height < 1) return;
        if (!first && !force && width == captureWidth && height == captureHeight) return;
        ClearLatestFrame();
        var nextReader = ImageReader.NewInstance(width, height, (ImageFormatType)1, 2); // RGBA_8888
        try
        {
            nextReader.SetOnImageAvailableListener(frameListener, captureHandler);
            if (first)
                virtualDisplay = projection.CreateVirtualDisplay("PgrVoice.Subtitles", width, height, densityDpi,
                    (DisplayFlags)(int)VirtualDisplayFlags.AutoMirror, nextReader.Surface, null, captureHandler);
            else
            {
                // Resize the existing display. Recreating a VirtualDisplay with the same grant is forbidden on Android 14+.
                virtualDisplay!.Resize(width, height, densityDpi);
                virtualDisplay.Surface = nextReader.Surface;
            }
        }
        catch { CloseReader(nextReader); throw; }
        var previous = imageReader;
        imageReader = nextReader;
        captureWidth = width; captureHeight = height;
        CloseReader(previous);
    }

    private void QueueFramePump(long delay)
    {
        if (destroyed || stopping || !captureActive || framePumpQueued || framePump == null || captureHandler == null) return;
        framePumpQueued = true;
        if (!captureHandler.PostDelayed(framePump, delay)) framePumpQueued = false;
    }

    private void NativeFrameAvailable(ImageReader reader)
    {
        if (destroyed || stopping || !captureActive || !ReferenceEquals(reader, imageReader)) return;
        // The callback is only a wake-up hint. Do not acquire/discard the final image between
        // sampling ticks: that image may be the last update of an otherwise static subtitle.
        QueueFramePump(0);
    }

    private void PumpFrames()
    {
        framePumpQueued = false;
        if (destroyed || stopping || !captureActive) return;
        Android.Media.Image? frame = null;
        try
        {
            RefreshPhysicalDisplay();
            var reader = imageReader;
            long samplingSession = sessionId;
            if (!captureActive || reader == null) return;
            frame = reader.AcquireLatestImage();
            long now = SystemClock.ElapsedRealtime();
            if (frame != null)
            {
                var plane = frame.GetPlanes()![0];
                if (plane.PixelStride != 4) { ClearLatestFrame(); return; }
                var bitmap = CopyFrameBitmap(frame);
                ClearLatestFrame();
                latestFrame = bitmap;
                latestFrameTime = now;
                latestFrameSession = samplingSession;
                latestFrameDisplay = displayState;
                latestFrameId = ++nextSourceFrameId;
            }
            if (destroyed || stopping || !captureActive || latestFrame == null ||
                latestFrameSession != sessionId || latestFrameDisplay != displayState || !ReferenceEquals(reader, imageReader)) return;
            var consumer = FrameAvailable;
            if (consumer == null) return;
            // Each delivered object retains its original pixel-source time/id. A repeated
            // sample permits stable-image OCR; it is not evidence of another screen update.
            var copy = latestFrame.Copy(Bitmap.Config.Argb8888!, false) ?? throw new InvalidOperationException("无法保留屏幕采样。");
            var delivered = new CapturedFrame(copy, latestFrameTime, latestFrameSession, latestFrameDisplay,
                latestFrameId, latestFrameDelivered, now);
            latestFrameDelivered = true;
            try { consumer(delivered); }
            catch (Exception ex) { delivered.Dispose(); Android.Util.Log.Warn("PgrVoice", "帧接收失败：" + ex.Message); }
        }
        catch (Exception ex)
        {
            if (captureActive) ReleaseCapture("屏幕识别已中断：" + ex.Message, true);
        }
        finally
        {
            // Disposing the Java peer alone does not return this image slot to ImageReader.
            if (frame != null)
            {
                // ReleaseCapture may already have closed the reader on the exception path.
                Cleanup(frame.Close, "归还已获取画面");
                Cleanup(frame.Dispose, "释放已获取画面");
            }
            // One queued tick at most, with no backlog or inference work on this looper.
            QueueFramePump(200);
        }
    }

    private void ClearLatestFrame()
    {
        latestFrame?.Dispose();
        latestFrame = null;
        latestFrameTime = latestFrameSession = latestFrameId = 0;
        latestFrameDisplay = ScreenDisplayState.Unavailable;
        latestFrameDelivered = false;
    }

    private static Bitmap CopyFrameBitmap(Android.Media.Image frame)
    {
        var plane = frame.GetPlanes()![0];
        var buffer = plane.Buffer!;
        buffer.Rewind();
        int rowBytes = checked(frame.Width * 4);
        var bitmap = Bitmap.CreateBitmap(frame.Width, frame.Height, Bitmap.Config.Argb8888!)!;
        try
        {
            if (plane.RowStride == rowBytes)
            {
                // Most devices have tightly packed rows: copy once, with no second full-screen bitmap.
                bitmap.CopyPixelsFromBuffer(buffer);
            }
            else if (buffer.Remaining() >= (long)plane.RowStride * frame.Height)
            {
                using var padded = Bitmap.CreateBitmap(plane.RowStride / 4, frame.Height, Bitmap.Config.Argb8888!)!;
                padded.CopyPixelsFromBuffer(buffer);
                using var canvas = new Canvas(bitmap);
                canvas.DrawBitmap(padded, 0, 0, null);
            }
            else
            {
                // Image.Plane need not expose trailing padding after the final row.
                var packed = new byte[checked(rowBytes * frame.Height)];
                for (int row = 0; row < frame.Height; row++)
                {
                    buffer.Position(checked(row * plane.RowStride));
                    buffer.Get(packed, row * rowBytes, rowBytes);
                }
                using var packedBuffer = Java.Nio.ByteBuffer.Wrap(packed)!;
                bitmap.CopyPixelsFromBuffer(packedBuffer);
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private void ResizeCapture(int width, int height, long callbackSession)
    {
        if (destroyed || stopping || !captureActive || callbackSession != sessionId) return;
        try { RefreshPhysicalDisplay(); ConfigureReader(width, height, false); }
        catch (Exception ex) { ReleaseCapture("屏幕尺寸变化后捕获中止，请重新开启：" + ex.Message, true); }
    }

    public override void OnConfigurationChanged(Configuration? newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        captureHandler?.Post(() =>
        {
            if (destroyed || stopping) return;
            RefreshPhysicalDisplay();
            // Android 14+ projection callbacks own app-only capture sizes, but physical
            // display changes above must still be observed when those sizes stay identical.
            if (!captureActive || Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake) return;
            var size = ScreenSize(); densityDpi = size.Dpi; ResizeCapture(size.Width, size.Height, sessionId);
        });
    }

    private void ReleaseCapture(string reason, bool stopProjection, bool notify = true)
    {
        bool hadCapture = projection != null || captureActive;
        captureActive = false;
        if (framePump != null) captureHandler?.RemoveCallbacks(framePump);
        framePumpQueued = false;
        ClearLatestFrame();
        var previousProjection = projection; projection = null;
        var previousCallback = projectionCallback; projectionCallback = null;
        var previousDisplay = virtualDisplay; virtualDisplay = null;
        var previousReader = imageReader; imageReader = null;
        // Unregistering does not revoke callbacks already queued by Android. Make them inert,
        // but do not Dispose their Java peers while the queue can still reference them.
        previousCallback?.Detach();
        if (previousProjection != null && previousCallback != null)
            Cleanup(() => previousProjection.UnregisterCallback(previousCallback), "取消屏幕捕获回调");
        if (previousDisplay != null)
        {
            Cleanup(previousDisplay.Release, "释放屏幕显示连接");
            Cleanup(previousDisplay.Dispose, "释放屏幕显示对象");
        }
        CloseReader(previousReader);
        if (previousProjection != null)
        {
            if (stopProjection) Cleanup(previousProjection.Stop, "停止屏幕捕获会话");
            Cleanup(previousProjection.Dispose, "释放屏幕捕获会话");
        }
        if (notify && (hadCapture || reason.StartsWith("无法", StringComparison.Ordinal))) CaptureEnded(reason);
    }

    private static void CloseReader(ImageReader? reader)
    {
        if (reader == null) return;
        Cleanup(() => reader.SetOnImageAvailableListener(null, null), "取消画面读取回调");
        Cleanup(reader.Close, "关闭画面读取器");
        Cleanup(reader.Dispose, "释放画面读取器");
    }

    private static void Cleanup(Action action, string operation)
    {
        try { action(); }
        catch (Exception ex) { Android.Util.Log.Warn("PgrVoice", operation + "：" + ex.Message); }
    }

    private void CaptureEnded(string reason)
    {
        long endedSession = sessionId;
        mainHandler?.Post(() =>
        {
            // A posted end notification from the old grant/service must not pause a new grant.
            if (sessionId != endedSession || current != null && !ReferenceEquals(current, this)) return;
            if (!destroyed) Promote(false);
            CaptureStopped?.Invoke(reason);
        });
    }

    private void StopSession()
    {
        if (stopping) return;
        stopping = true;
        StopRequested?.Invoke();
        StopSelf();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        destroyed = true;
        foreground = false;
        lock (readyActions) readyActions.Clear();
        if (ReferenceEquals(current, this)) current = null;
        screenOffReceiver?.Detach();
        try { if (screenOffReceiver != null) UnregisterReceiver(screenOffReceiver); } catch { }
        screenOffReceiver = null;
        displayListener?.Detach();
        if (displayManager != null && displayListener != null)
            Cleanup(() => displayManager.UnregisterDisplayListener(displayListener), "取消物理显示监听");
        displayListener = null;
        frameListener?.Detach();
        // All capture resources belong to the capture thread; quit safely after the cleanup message.
        captureHandler?.Post(() =>
        {
            ReleaseCapture("屏幕识别服务已停止", true);
            // Let the JNI bridge collect listener peers after any queued Java references end.
            // Explicit Dispose here could reproduce the same late-callback activation crash.
            frameListener = null;
            captureThread?.QuitSafely();
        });
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }

    private sealed class PhysicalDisplayListener : Java.Lang.Object, DisplayManager.IDisplayListener
    {
        private volatile VoiceForegroundService? owner;
        public PhysicalDisplayListener(VoiceForegroundService owner) => this.owner = owner;
        public PhysicalDisplayListener(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }
        public void Detach() => owner = null;
        public void OnDisplayAdded(int displayId) => owner?.RefreshPhysicalDisplay();
        public void OnDisplayChanged(int displayId) => owner?.RefreshPhysicalDisplay();
        public void OnDisplayRemoved(int displayId) => owner?.RefreshPhysicalDisplay();
    }
    private sealed class FrameListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        private volatile VoiceForegroundService? owner;
        public FrameListener(VoiceForegroundService owner) => this.owner = owner;
        // A peer reactivated only for a late native callback is intentionally detached.
        public FrameListener(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }
        public void Detach() => owner = null;
        public void OnImageAvailable(ImageReader? reader)
        {
            var service = owner;
            if (service != null && reader != null)
                service.NativeFrameAvailable(reader);
        }
    }
    private sealed class ProjectionCallback : MediaProjection.Callback
    {
        private volatile VoiceForegroundService? owner;
        private readonly long session;
        public ProjectionCallback(VoiceForegroundService owner, long session) { this.owner = owner; this.session = session; }
        public ProjectionCallback(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }
        public void Detach() => owner = null;
        public override void OnStop()
        {
            var service = owner;
            service?.captureHandler?.Post(() =>
            {
                if (!service.destroyed && !service.stopping && service.captureActive && service.sessionId == session)
                    service.ReleaseCapture("系统已结束屏幕识别，请重新授权后开启。", false);
            });
        }
        public override void OnCapturedContentResize(int width, int height)
        {
            var service = owner;
            service?.captureHandler?.Post(() => service.ResizeCapture(width, height, session));
        }
    }
    private sealed class ScreenOffReceiver : BroadcastReceiver
    {
        private volatile VoiceForegroundService? owner;
        public ScreenOffReceiver(VoiceForegroundService owner) => this.owner = owner;
        public ScreenOffReceiver(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }
        public void Detach() => owner = null;
        public override void OnReceive(Context? context, Intent? intent)
        {
            var service = owner;
            if (intent?.Action == Intent.ActionScreenOff)
                service?.captureHandler?.Post(() => service.ReleaseCapture("已锁屏，屏幕识别已停止。解锁后可手动继续或重新开启识别。", true));
        }
    }
}
