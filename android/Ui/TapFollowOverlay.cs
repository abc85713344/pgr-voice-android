using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;

namespace PgrVoice.AndroidApp.Ui;

/// <summary>
/// 仅覆盖用户指定的游戏点按范围。所有方法与回调均须在 UI 线程使用。
/// 轻点由本窗口消费；调用方先 Suspend，注入实际游戏点击，回调核验后再 Resume。
/// </summary>
public sealed class TapFollowOverlay : IDisposable
{
    readonly Context context;
    readonly IWindowManager windows;
    readonly TouchSurface surface;
    readonly WindowManagerLayoutParams parameters;
    bool wanted, suspended, attached, disposed;
    public bool IsVisible => attached;

    public TapFollowOverlay(Context context, int displayWidth, int displayHeight, ScreenRegion region, Action<float, float> tapped, bool showHint = true)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(region); ArgumentNullException.ThrowIfNull(tapped);
        if (displayWidth <= 0 || displayHeight <= 0 || !float.IsFinite(region.Left) || !float.IsFinite(region.Top) ||
            !float.IsFinite(region.Width) || !float.IsFinite(region.Height) || region.Left < 0 || region.Top < 0 ||
            region.Width <= 0 || region.Height <= 0 || region.Left + region.Width > 1.00001f || region.Top + region.Height > 1.00001f)
            throw new ArgumentOutOfRangeException(nameof(region), "点按范围须位于当前物理屏幕内。");
        this.context = context;
        windows = context.GetSystemService(Context.WindowService)!.JavaCast<IWindowManager>();
        int x = Math.Clamp((int)(region.Left * displayWidth), 0, displayWidth - 1);
        int y = Math.Clamp((int)(region.Top * displayHeight), 0, displayHeight - 1);
        int width = Math.Clamp((int)(region.Width * displayWidth), 1, displayWidth - x);
        int height = Math.Clamp((int)(region.Height * displayHeight), 1, displayHeight - y);
        surface = new TouchSurface(context, tapped, showHint);
        parameters = new WindowManagerLayoutParams(width, height, WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal | WindowManagerFlags.LayoutInScreen,
            Format.Translucent) { Gravity = GravityFlags.Top | GravityFlags.Left, X = x, Y = y };
        // 保存的是物理屏幕坐标；避免系统状态栏 inset 再把框往下平移一次。
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) parameters.FitInsetsTypes = 0;
    }

    public void Show()
    {
        if (disposed) return;
        wanted = true; AttachIfNeeded();
    }

    public void Suspend()
    {
        if (disposed) return;
        suspended = true; surface.CancelGesture(); Detach();
    }

    public void Resume()
    {
        if (disposed) return;
        suspended = false; AttachIfNeeded();
    }

    public void Hide()
    {
        if (disposed) return;
        wanted = false; suspended = false; surface.CancelGesture(); Detach();
    }

    void AttachIfNeeded()
    {
        if (!wanted || suspended || attached) return;
        windows.AddView(surface, parameters); attached = true;
    }

    void Detach()
    {
        if (!attached) return;
        // 同步撤窗，调用方才可安排系统注入；不发送任何触摸事件。
        try { windows.RemoveViewImmediate(surface); }
        catch (Java.Lang.IllegalArgumentException) { /* 已由系统移除。 */ }
        attached = false;
    }

    public void Dispose()
    {
        if (disposed) return;
        wanted = false; suspended = true; surface.CancelGesture(); Detach();
        disposed = true; surface.Dispose(); parameters.Dispose();
        // Context 与系统 WindowManager 由平台持有，不在此 Dispose。
        GC.KeepAlive(context);
    }

    [Register("cn/pgrvoice/player/TapFollowSurface")]
    public sealed class TouchSurface : View
    {
        readonly Paint paint = new(PaintFlags.AntiAlias);
        readonly TapFollowGestureGate? gestures;
        readonly Action<float, float>? tapped;
        readonly bool showHint = true;
        readonly float density = 1, textSize = 12;

        public TouchSurface(Context context, Action<float, float> tapped, bool showHint = true) : base(context)
        {
            this.tapped = tapped;
            this.showHint = showHint;
            density = context.Resources?.DisplayMetrics?.Density ?? 1;
            textSize = Android.Util.TypedValue.ApplyDimension(Android.Util.ComplexUnitType.Sp, 12, context.Resources!.DisplayMetrics);
            gestures = new TapFollowGestureGate(12 * density);
            ContentDescription = "点按跟随";
        }
        public TouchSurface(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership) { }

        public void CancelGesture() => gestures?.Cancel();

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            if (!showHint)
            {
                // 分支识别框位于 OCR 文字矩形之外，只画细线，不把提示字或底色送回 OCR。
                paint.Color = Color.Argb(220, 70, 215, 205); paint.SetStyle(Paint.Style.Stroke); paint.StrokeWidth = 1.5f;
                canvas.DrawRect(.75f, .75f, Math.Max(.75f, Width - .75f), Math.Max(.75f, Height - .75f), paint);
                return;
            }
            canvas.DrawColor(Color.Argb(9, 170, 25, 43));
            paint.Color = Color.Argb(120, 215, 75, 86); paint.SetStyle(Paint.Style.Stroke); paint.StrokeWidth = density;
            canvas.DrawRect(density, density, Math.Max(density, Width - density), Math.Max(density, Height - density), paint);
            paint.SetStyle(Paint.Style.Fill); paint.Color = Color.Argb(160, 230, 222, 222); paint.TextSize = textSize;
            canvas.DrawText("点按跟随", 7 * density, 17 * density, paint);
        }

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e == null || gestures == null) return false;
            int count = e.PointerCount;
            int pointer = count > 0 ? e.GetPointerId(0) : -1;
            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    gestures.Begin(pointer, count, e.RawX, e.RawY, e.EventTime);
                    break;
                case MotionEventActions.Move:
                    // Android 可合并移动事件；检查历史点，防止划出后返回被误作轻点。
                    if (count == 1)
                    {
                        float offsetX = e.RawX - e.GetX(0), offsetY = e.RawY - e.GetY(0);
                        for (int i = 0; i < e.HistorySize; i++)
                            gestures.Move(pointer, count, e.GetHistoricalX(0, i) + offsetX, e.GetHistoricalY(0, i) + offsetY, e.GetHistoricalEventTime(i));
                    }
                    gestures.Move(pointer, count, e.RawX, e.RawY, e.EventTime);
                    break;
                case MotionEventActions.Up:
                    if (gestures.TryEnd(pointer, count, e.RawX, e.RawY, e.EventTime, out var point))
                    {
                        PerformClick(); tapped?.Invoke(point.X, point.Y);
                    }
                    break;
                case MotionEventActions.PointerDown:
                case MotionEventActions.PointerUp:
                case MotionEventActions.Cancel:
                case MotionEventActions.Outside:
                    gestures.Cancel();
                    break;
            }
            return true;
        }

        public override bool PerformClick() { base.PerformClick(); return true; }
        protected override void OnDetachedFromWindow() { gestures?.Cancel(); base.OnDetachedFromWindow(); }
        protected override void Dispose(bool disposing) { if (disposing) { gestures?.Cancel(); paint.Dispose(); } base.Dispose(disposing); }
    }
}
