using Android.Graphics;
using Android.Views;
using Android.Widget;
using PgrVoice.AndroidApp.Ui;

namespace PgrVoice.AndroidApp.Platform;

public sealed partial class OverlayController
{
    LinearLayout? defaultBranchWaitView;
    TextView? defaultBranchWaitText;
    Action? defaultBranchWaitManual;
    public int CaptureWaitExcludedBottom { get; private set; }

    // 固定顶边的等待通知不拖动、不展开；OCR 始终排除从屏幕顶边到本条底边的整带。
    // 进入完整手动核对前先由会话撤销 OCR，避免浮窗文字成为游戏证据。
    public void ShowDefaultBranchWait(string message, Action manual) => OnMain(() =>
    {
        if (disposed || !captureHidden || !CanShow) return;
        defaultBranchWaitManual = manual;
        if (defaultBranchWaitText != null)
        { defaultBranchWaitText.Text = message; return; }
        var safe = ScreenArea();
        float fontScale = Math.Max(1f, context.Resources?.Configuration?.FontScale ?? 1f);
        int height = Math.Min(ReadDp(Math.Max(76, 14 * fontScale * 4 + 12)), safe.Bottom - safe.Top);
        var body = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        body.SetPadding(ReadDp(8), ReadDp(4), ReadDp(4), ReadDp(4));
        body.SetBackgroundColor(PgrTheme.Raised);
        var text = new TextView(context) { Text = message, TextSize = 14, Gravity = GravityFlags.CenterVertical };
        text.SetTextColor(PgrTheme.Foreground); text.SetMaxLines(4);
        text.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        body.AddView(text, new LinearLayout.LayoutParams(0, -1, 1));
        var button = new Button(context) { Text = "手动续接", TextSize = 13 };
        button.SetAllCaps(false); button.SetTextColor(PgrTheme.Cyan); button.SetMaxLines(2);
        button.SetMinWidth(0); button.SetMinimumWidth(0);
        button.ContentDescription = "识别未接上时，核对完整分支台词并手动续接";
        button.Click += (_, _) => { if (ReferenceEquals(defaultBranchWaitView, body)) defaultBranchWaitManual?.Invoke(); };
        body.AddView(button, new LinearLayout.LayoutParams(ReadDp(96), -1));
        var layout = new WindowManagerLayoutParams(safe.Right - safe.Left, height,
            WindowManagerTypes.ApplicationOverlay, WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchModal |
            WindowManagerFlags.LayoutInScreen, Format.Translucent)
        { Gravity = GravityFlags.Top | GravityFlags.Left, X = safe.Left, Y = safe.Top };
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        { layout.FitInsetsTypes = 0; layout.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.Always; }
        else if (OperatingSystem.IsAndroidVersionAtLeast(28)) layout.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.Never;
        // 先公布排除带再显示文字；额外空白覆盖系统可能加在窗口周围的边缘。
        CaptureWaitExcludedBottom = safe.Top + height + ReadDp(8);
        defaultBranchWaitView = body; defaultBranchWaitText = text;
        try { windows.AddView(body, layout); }
        catch (Exception ex) { ClearDefaultBranchWait(); Error?.Invoke("默认分支等待提示未能显示：" + ex.Message); }
    });

    public void ClearDefaultBranchWait() => OnMain(() =>
    {
        var previous = defaultBranchWaitView;
        defaultBranchWaitView = null; defaultBranchWaitText = null; defaultBranchWaitManual = null;
        if (previous != null)
        { try { windows.RemoveViewImmediate(previous); } catch { } previous.Dispose(); }
        CaptureWaitExcludedBottom = 0;
    });
}
