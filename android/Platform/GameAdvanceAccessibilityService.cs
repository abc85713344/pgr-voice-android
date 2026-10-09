using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Hardware.Display;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Views.Accessibility;
using System.Diagnostics.CodeAnalysis;

namespace PgrVoice.AndroidApp.Platform;

public sealed record GameAdvanceAvailability(bool Connected, bool TargetForeground, bool ScreenReady,
    int DisplayWidth, int DisplayHeight, int Rotation, string Reason)
{
    public bool CanTap => Connected && TargetForeground && ScreenReady;
    public string? DisplayKey { get; init; }
    public string? ForegroundPackage { get; init; }
}

public enum GameAdvanceTapStatus { Completed, Cancelled, Rejected, Unavailable, Invalidated }

public sealed record GameAdvanceTapResult(GameAdvanceTapStatus Status, string Reason)
{
    public bool Completed => Status == GameAdvanceTapStatus.Completed;
}

/// <summary>
/// Optional, explicitly armed, single-tap adapter. Story and audio policy belong to AppSession.
/// Window roots are inspected only for package identity; no text or child node is read.
/// </summary>
[Service(Name = "cn.pgrvoice.player.GameAdvanceAccessibilityService", Exported = true,
    Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE", Label = "@string/game_advance_service_name")]
[IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
[MetaData("android.accessibilityservice", Resource = "@xml/game_advance_accessibility")]
public sealed class GameAdvanceAccessibilityService : AccessibilityService
{
    private static readonly object Gate = new();
    private static GameAdvanceAccessibilityService? current;
    private static long nextSession;
    private static long nextGesture;
    private Handler? main;
    private DisplayManager? displayManager;
    private GameAdvanceDisplayListener? displayListener;
    private GameAdvanceScreenReceiver? screenReceiver;
    private bool connected, destroyed;
    private long session;
    private string target = "";
    private int expectedWidth, expectedHeight, expectedRotation;
    private PendingTap? pending;
    private string lastReason = "请在系统无障碍设置中开启“剧情自动下一句”。";

    public static bool IsConnected { get { lock (Gate) return current is { connected: true, destroyed: false }; } }
    public static string LastFailureReason { get { lock (Gate) return current?.lastReason ?? "自动点击服务尚未连接。"; } }
    public static event Action<string>? SessionInvalidated;

    public static GameAdvanceAvailability GetAvailability(string targetPackage)
        => InspectCurrent(targetPackage);

    // 仅在用户主动开启或框选时读取当前窗口；运行后仍核对同一个窗口，切出即停。
    public static GameAdvanceAvailability GetForegroundAvailability() => InspectCurrent(null);

    static GameAdvanceAvailability InspectCurrent(string? targetPackage)
    {
        lock (Gate)
        {
            if (current is not { connected: true, destroyed: false } service)
                return new(false, false, false, 0, 0, 0, "请在系统无障碍设置中开启“剧情自动下一句”。");
            return service.Inspect(targetPackage, null).Availability;
        }
    }

    /// <summary>Requires an explicit user action and a foreground game. Tokens are never persisted.</summary>
    public static long BeginSession(string targetPackage, int width, int height, int rotation)
    {
        lock (Gate)
        {
            if (current is not { connected: true, destroyed: false } service) return 0;
            // The player's own Activity must never be treated as the game, including in diagnostics.
            if (string.IsNullOrWhiteSpace(targetPackage) || targetPackage == service.PackageName) return 0;
            var state = service.Inspect(targetPackage, null).Availability;
            if (!state.CanTap || width <= 0 || height <= 0 || state.DisplayWidth != width ||
                state.DisplayHeight != height || state.Rotation != rotation || service.pending != null)
            {
                service.lastReason = state.CanTap ? "触碰区域对应的屏幕已变化，请重新设置。" : state.Reason;
                return 0;
            }
            service.session = ++nextSession;
            service.target = targetPackage;
            service.expectedWidth = width;
            service.expectedHeight = height;
            service.expectedRotation = rotation;
            service.lastReason = "";
            return service.session;
        }
    }

    /// <summary>
    /// Sends at most one 60 ms tap. Dispatch acceptance is not success: the native completion callback
    /// decides the result. Cancellation invalidates all queued requests. An already dispatched touch
    /// cannot be recalled by Android; its late callback is ignored and never authorizes another tap.
    /// </summary>
    public static Task<GameAdvanceTapResult> TapAsync(long token, float x, float y,
        CancellationToken cancellationToken = default)
    {
        var result = new TaskCompletionSource<GameAdvanceTapResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        GameAdvanceAccessibilityService? service;
        lock (Gate)
        {
            service = current;
            if (service is not { connected: true, destroyed: false })
            {
                result.SetResult(new(GameAdvanceTapStatus.Unavailable, "自动点击服务尚未连接。"));
                return result.Task;
            }
        }
        if (cancellationToken.IsCancellationRequested)
        {
            service.CancelToken(token);
            result.SetResult(new(GameAdvanceTapStatus.Cancelled, "点击已取消。"));
            return result.Task;
        }
        // The token is checked again on the main looper immediately before Android dispatch.
        if (service.main?.Post(() => service.DispatchOne(token, x, y, cancellationToken, result)) != true)
            result.TrySetResult(new(GameAdvanceTapStatus.Unavailable, "自动点击服务已停止。"));
        return result.Task;
    }

    /// <summary>Synchronous invalidation prevents already queued work from becoming a new touch.</summary>
    public static void CancelSession(string reason = "自动播放已暂停。")
    {
        bool changed;
        lock (Gate) changed = current?.InvalidateLocked(reason) == true;
        if (changed) NotifyInvalidated(reason);
    }

    public static void InvalidatePending(string reason = "触碰区域已变化，请重新开始自动播放。") => CancelSession(reason);

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(GameAdvanceGestureCallback))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(GameAdvanceDisplayListener))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(GameAdvanceScreenReceiver))]
    public override void OnCreate()
    {
        base.OnCreate();
        main = new Handler(Looper.MainLooper!);
        displayManager = (DisplayManager?)GetSystemService(DisplayService);
        displayListener = new GameAdvanceDisplayListener(this);
        displayManager?.RegisterDisplayListener(displayListener, main);
        screenReceiver = new GameAdvanceScreenReceiver(this);
        var filter = new IntentFilter(Intent.ActionScreenOff);
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) RegisterReceiver(screenReceiver, filter, ReceiverFlags.NotExported);
        else RegisterReceiver(screenReceiver, filter);
    }

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        bool invalidated;
        PendingTap? stale;
        lock (Gate)
        {
            // Enabling accessibility never resumes or arms story autoplay.
            invalidated = current?.InvalidateLocked("自动点击服务重新连接，请手动重新开始。") == true;
            current = this;
            connected = true;
            destroyed = false;
            session = 0;
            stale = pending;
            pending = null;
        }
        stale?.Cancellation.Dispose();
        stale?.Callback?.Detach();
        if (invalidated) NotifyInvalidated("自动点击服务重新连接，请手动重新开始。");
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
        // Do not read event text/source. Only window changes can affect targeting.
        if (e?.EventType is EventTypes.WindowStateChanged or EventTypes.WindowsChanged)
            ValidateSession();
    }

    public override void OnInterrupt() => Invalidate("系统中断了自动点击，请确认游戏位置后重新开始。");

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ValidateSession();
    }

    public override bool OnUnbind(Intent? intent)
    {
        Disconnect("辅助点击服务暂时断开，跟随已停止；服务恢复后请核对位置再开始。");
        return base.OnUnbind(intent);
    }

    public override void OnDestroy()
    {
        Disconnect("自动点击服务已停止，自动播放已暂停。");
        lock (Gate) destroyed = true;
        displayListener?.Detach();
        if (displayListener != null) displayManager?.UnregisterDisplayListener(displayListener);
        screenReceiver?.Detach();
        if (screenReceiver != null)
        {
            try { UnregisterReceiver(screenReceiver); } catch (Java.Lang.IllegalArgumentException) { }
        }
        // Let Java finish any queued callbacks before their peers are collected.
        main = null;
        displayListener = null;
        screenReceiver = null;
        base.OnDestroy();
    }

    private void Disconnect(string reason)
    {
        bool changed;
        PendingTap? stale;
        lock (Gate)
        {
            connected = false;
            changed = InvalidateLocked(reason);
            stale = pending;
            pending = null;
            if (ReferenceEquals(current, this)) current = null;
        }
        stale?.Cancellation.Dispose();
        stale?.Callback?.Detach();
        if (changed) NotifyInvalidated(reason);
    }

    internal void Invalidate(string reason)
    {
        bool changed;
        lock (Gate) changed = InvalidateLocked(reason);
        if (changed) NotifyInvalidated(reason);
    }

    private bool InvalidateLocked(string reason)
    {
        bool changed = session != 0;
        session = 0;
        target = "";
        lastReason = reason;
        pending?.Result.TrySetResult(new(GameAdvanceTapStatus.Invalidated, reason));
        // Keep the slot occupied until Android reports completion/cancellation, so a new session
        // cannot overlap a gesture whose callback has not arrived yet.
        return changed;
    }

    private static void NotifyInvalidated(string reason)
    {
        var handlers = SessionInvalidated;
        if (handlers == null) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
            try { handler(reason); } catch (Exception ex) { Android.Util.Log.Warn("PgrVoice", "自动播放暂停回调失败：" + ex.Message); }
    }

    internal void ValidateSession()
    {
        string? reason = null;
        lock (Gate)
        {
            if (session == 0 || destroyed) return;
            var state = Inspect(target, null).Availability;
            if (!state.CanTap) reason = state.Reason;
            else if (state.DisplayWidth != expectedWidth || state.DisplayHeight != expectedHeight || state.Rotation != expectedRotation)
                reason = "屏幕尺寸或方向已变化，请重新设置触碰区域后开始。";
            if (reason != null) InvalidateLocked(reason);
        }
        if (reason != null) NotifyInvalidated(reason);
    }

    private void DispatchOne(long token, float x, float y, CancellationToken cancellation,
        TaskCompletionSource<GameAdvanceTapResult> result)
    {
        if (cancellation.IsCancellationRequested)
        {
            CancelToken(token);
            result.TrySetResult(new(GameAdvanceTapStatus.Cancelled, "点击已取消。"));
            return;
        }
        string? invalidation = null;
        PendingTap? dispatched = null;
        lock (Gate)
        {
            if (destroyed || !connected || !ReferenceEquals(current, this))
            { result.TrySetResult(new(GameAdvanceTapStatus.Unavailable, "自动点击服务已停止。")); return; }
            if (token == 0 || session != token)
            { result.TrySetResult(new(GameAdvanceTapStatus.Invalidated, lastReason)); return; }
            if (pending != null)
            { result.TrySetResult(new(GameAdvanceTapStatus.Rejected, "上一触碰仍在处理中，本次未点击。")); return; }
            if (!float.IsFinite(x) || !float.IsFinite(y))
            { result.TrySetResult(new(GameAdvanceTapStatus.Rejected, "触碰位置无效。")); return; }
            var inspection = Inspect(target, (x, y));
            var state = inspection.Availability;
            if (!state.CanTap || state.DisplayWidth != expectedWidth || state.DisplayHeight != expectedHeight ||
                state.Rotation != expectedRotation || !inspection.PointSafe)
            {
                invalidation = !state.CanTap || !inspection.PointSafe ? state.Reason : "屏幕尺寸或方向已变化，请重新设置触碰区域。";
                InvalidateLocked(invalidation);
                result.TrySetResult(new(GameAdvanceTapStatus.Invalidated, invalidation));
            }
            else if (cancellation.IsCancellationRequested)
            {
                // Window queries cross a binder boundary. Recheck cancellation after they return,
                // directly before constructing and dispatching the native gesture.
                invalidation = "点击已取消，自动播放已暂停。";
                result.TrySetResult(new(GameAdvanceTapStatus.Cancelled, invalidation));
                InvalidateLocked(invalidation);
            }
            else
            {
                var work = new PendingTap(token, ++nextGesture, result);
                pending = work;
                try
                {
                    using var path = new Android.Graphics.Path();
                    path.MoveTo(x, y);
                    using var stroke = new GestureDescription.StrokeDescription(path, 0, 60);
                    using var builder = new GestureDescription.Builder();
                    if (OperatingSystem.IsAndroidVersionAtLeast(30)) builder.SetDisplayId(Android.Views.Display.DefaultDisplay);
                    using var gesture = builder.AddStroke(stroke)!.Build();
                    work.Callback = new GameAdvanceGestureCallback(this, work.Gesture);
                    if (!DispatchGesture(gesture!, work.Callback, main))
                    {
                        pending = null;
                        result.TrySetResult(new(GameAdvanceTapStatus.Rejected, "系统未接受触碰，本次未点击。"));
                    }
                    else
                    {
                        dispatched = work;
                    }
                }
                catch (Exception ex)
                {
                    pending = null;
                    result.TrySetResult(new(GameAdvanceTapStatus.Rejected, "无法执行触碰：" + ex.Message));
                }
            }
        }
        if (dispatched != null)
        {
            // Register outside Gate: an already-cancelled token invokes its callback synchronously,
            // and subscribers may acquire the caller's own session lock.
            dispatched.Cancellation = cancellation.Register(() => CancelToken(token));
            var work = dispatched;
            // No retry or second gesture on timeout: a missing native callback is uncertain.
            main?.PostDelayed(() => Timeout(work), 2000);
        }
        if (invalidation != null) NotifyInvalidated(invalidation);
    }

    private void CancelToken(long token)
    {
        bool changed = false;
        const string reason = "点击已取消，自动播放已暂停。";
        lock (Gate)
        {
            if (session != token) return;
            pending?.Result.TrySetResult(new(GameAdvanceTapStatus.Cancelled, reason));
            changed = InvalidateLocked(reason);
        }
        if (changed) NotifyInvalidated(reason);
    }

    private void Timeout(PendingTap work)
    {
        bool changed = false;
        const string reason = "系统未返回触碰结果，自动播放已暂停；请关闭并重新开启“剧情自动下一句”服务后确认当前位置。";
        lock (Gate)
        {
            if (!ReferenceEquals(pending, work)) return;
            changed = InvalidateLocked(reason);
            // The native gesture is bounded to 60 ms; after a missing 2 s callback, leave the
            // service disabled for new sessions until Android reconnects instead of retrying.
            connected = false;
        }
        if (changed) NotifyInvalidated(reason);
    }

    internal void GestureEnded(long gesture, bool completed)
    {
        PendingTap? work;
        bool cancelledSession = false;
        const string cancelledReason = "触碰被系统或其他操作取消，本次没有确认推进。";
        lock (Gate)
        {
            work = pending;
            if (work == null || work.Gesture != gesture) return;
            pending = null;
            if (session != work.Session || destroyed || !connected)
                work.Result.TrySetResult(new(GameAdvanceTapStatus.Invalidated, lastReason));
            else
            {
                work.Result.TrySetResult(new(completed ? GameAdvanceTapStatus.Completed : GameAdvanceTapStatus.Cancelled,
                    completed ? "" : cancelledReason));
                if (!completed) cancelledSession = InvalidateLocked(cancelledReason);
            }
        }
        // Disposing registrations outside Gate avoids waiting for a cancellation callback that
        // is itself waiting to acquire Gate.
        work.Cancellation.Dispose();
        work.Callback?.Detach();
        if (cancelledSession) NotifyInvalidated(cancelledReason);
    }

    private (GameAdvanceAvailability Availability, bool PointSafe) Inspect(string? packageName, (float X, float Y)? point)
    {
        int width = 0, height = 0, rotation = 0;
        GameAdvanceAvailability Failure(string reason, bool screenReady = false, bool foreground = false) =>
            new(connected && !destroyed, foreground, screenReady, width, height, rotation, reason);
        try
        {
            var display = displayManager?.GetDisplay(Android.Views.Display.DefaultDisplay);
            if (display == null) return (Failure("无法确认当前屏幕。"), false);
            using var size = new Point();
#pragma warning disable CS0618, CA1422
            display.GetRealSize(size);
#pragma warning restore CS0618, CA1422
            width = size.X; height = size.Y; rotation = (int)display.Rotation;
            var power = (PowerManager?)GetSystemService(PowerService);
            var keyguard = (KeyguardManager?)GetSystemService(KeyguardService);
            if (power?.IsInteractive != true || keyguard?.IsKeyguardLocked != false)
                return (Failure("屏幕已关闭或锁定，自动播放已暂停。"), false);
            if (packageName != null && (string.IsNullOrWhiteSpace(packageName) || packageName == PackageName))
                return (Failure("请回到游戏，从悬浮控制开始。", true), false);
            var windows = Windows;
            if (windows == null || windows.Count == 0)
                return (Failure("无法确认游戏前台窗口，已暂停自动播放。", true), false);
            try
            {
                var snapshots = new List<WindowSnapshot>(windows.Count);
                foreach (var window in windows)
                {
                    if (OperatingSystem.IsAndroidVersionAtLeast(30) && window.DisplayId != Android.Views.Display.DefaultDisplay) continue;
                    using var root = window.Root;
                    using var bounds = new Rect();
                    window.GetBoundsInScreen(bounds);
                    snapshots.Add(new(window.Id, window.Layer, window.IsFocused, window.IsActive, window.Type,
                        root?.PackageName ?? "", bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
                }
                var focused = snapshots.FirstOrDefault(w => w.Focused);
                if (focused == null || focused.Type != AccessibilityWindowType.Application ||
                    string.IsNullOrWhiteSpace(focused.Package) || focused.Package == PackageName ||
                    (packageName != null && focused.Package != packageName))
                    return (Failure("游戏已离开前台，请回到游戏后重新开始。", true), false);
                // 桌面不能成为一次新的游戏会话；无需枚举或保存已安装客户端。
                using var home = new Intent(Intent.ActionMain);
                home.AddCategory(Intent.CategoryHome); home.SetPackage(focused.Package);
                if (PackageManager?.ResolveActivity(home, (Android.Content.PM.PackageInfoFlags)0) != null)
                    return (Failure("请先打开游戏，再从悬浮控制开始。", true), false);
                // Our non-focusable controller can become active after a touch; it is allowed
                // only while the game retains input focus. Player activities are never allowed.
                if (snapshots.Any(w => w.Active && w.Id != focused.Id &&
                    !(w.Package == PackageName && !w.Focused && w.Type != AccessibilityWindowType.Application)))
                    return (Failure("前台出现了其他窗口，自动播放已暂停。", true), false);
                if (point is { } p)
                {
                    if (p.X < 0 || p.Y < 0 || p.X >= width || p.Y >= height || !focused.Contains(p.X, p.Y))
                        return (Failure("触碰位置不在游戏窗口内，请重新设置。", true, true), false);
                    if (snapshots.Any(w => w.Id != focused.Id && w.Layer > focused.Layer && w.Contains(p.X, p.Y)))
                        return (Failure("触碰区域被悬浮窗或系统窗口遮挡，自动播放已暂停。", true, true), false);
                }
                using var mode = display.GetMode();
                var profile = new PhysicalDisplayProfile(display.DisplayId, mode!.PhysicalWidth, mode.PhysicalHeight, rotation);
                return (new GameAdvanceAvailability(connected && !destroyed, true, true, width, height, rotation, "")
                    { DisplayKey = profile.RegionKey, ForegroundPackage = focused.Package }, true);
            }
            finally
            {
                // No child traversal or content retention. Release the binder-backed snapshots.
                foreach (var window in windows) window.Dispose();
            }
        }
        catch (Exception)
        {
            return (Failure("暂时无法确认游戏和触碰位置，自动播放已暂停。"), false);
        }
    }

    private sealed record WindowSnapshot(int Id, int Layer, bool Focused, bool Active, AccessibilityWindowType Type,
        string Package, int Left, int Top, int Right, int Bottom)
    {
        public bool Contains(float x, float y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    private sealed class PendingTap(long session, long gesture, TaskCompletionSource<GameAdvanceTapResult> result)
    {
        public long Session { get; } = session;
        public long Gesture { get; } = gesture;
        public TaskCompletionSource<GameAdvanceTapResult> Result { get; } = result;
        public GameAdvanceGestureCallback? Callback;
        public CancellationTokenRegistration Cancellation;
    }
}

[Register("cn/pgrvoice/player/GameAdvanceGestureCallback")]
public sealed class GameAdvanceGestureCallback : AccessibilityService.GestureResultCallback
{
    private GameAdvanceAccessibilityService? owner;
    private readonly long gesture;
    public GameAdvanceGestureCallback(GameAdvanceAccessibilityService owner, long gesture) { this.owner = owner; this.gesture = gesture; }
    public GameAdvanceGestureCallback(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }
    public void Detach() => owner = null;
    public override void OnCompleted(GestureDescription? gestureDescription) => owner?.GestureEnded(gesture, true);
    public override void OnCancelled(GestureDescription? gestureDescription) => owner?.GestureEnded(gesture, false);
}

[Register("cn/pgrvoice/player/GameAdvanceDisplayListener")]
public sealed class GameAdvanceDisplayListener : Java.Lang.Object, DisplayManager.IDisplayListener
{
    private GameAdvanceAccessibilityService? owner;
    public GameAdvanceDisplayListener(GameAdvanceAccessibilityService owner) => this.owner = owner;
    public GameAdvanceDisplayListener(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }
    public void Detach() => owner = null;
    public void OnDisplayAdded(int displayId) => owner?.ValidateSession();
    public void OnDisplayChanged(int displayId) => owner?.ValidateSession();
    public void OnDisplayRemoved(int displayId) => owner?.Invalidate("显示屏已断开，自动播放已暂停。");
}

[Register("cn/pgrvoice/player/GameAdvanceScreenReceiver")]
public sealed class GameAdvanceScreenReceiver : BroadcastReceiver
{
    private GameAdvanceAccessibilityService? owner;
    public GameAdvanceScreenReceiver(GameAdvanceAccessibilityService owner) => this.owner = owner;
    public GameAdvanceScreenReceiver(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }
    public void Detach() => owner = null;
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent?.Action == Intent.ActionScreenOff) owner?.Invalidate("屏幕已关闭，自动播放已暂停。");
    }
}
