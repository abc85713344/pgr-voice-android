using Android.Graphics;
using Android.Views;
using Android.Widget;
using PgrVoice.AndroidApp.Ui;

namespace PgrVoice.AndroidApp.Platform;

public sealed partial class OverlayController
{
    TextView? branchEnteredView;

    public void ShowBranchEntered(string message) => OnMain(() =>
    {
        ClearBranchEntered();
        if (disposed || !CanShow || string.IsNullOrWhiteSpace(message)) return;
        var safe = ScreenArea();
        var text = new TextView(context) { Text = message, TextSize = 14, Gravity = GravityFlags.CenterVertical };
        text.SetTextColor(PgrTheme.Foreground); text.SetBackgroundColor(PgrTheme.Raised);
        text.SetPadding(ReadDp(12), ReadDp(8), ReadDp(12), ReadDp(8));
        text.SetMaxLines(2); text.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        int width = Math.Min(ReadDp(540), safe.Right - safe.Left - ReadDp(24));
        var layout = new WindowManagerLayoutParams(width, -2, WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.NotTouchable | WindowManagerFlags.LayoutInScreen,
            Format.Translucent) { Gravity = GravityFlags.Top | GravityFlags.Left,
            X = safe.Left + (safe.Right - safe.Left - width) / 2, Y = safe.Top + ReadDp(8) };
        branchEnteredView = text;
        try
        {
            windows.AddView(text, layout);
            main.PostDelayed(() => { if (ReferenceEquals(branchEnteredView, text)) ClearBranchEntered(); }, 1500);
        }
        catch { ClearBranchEntered(); }
    });

    public void ClearBranchEntered() => OnMain(() =>
    {
        var old = branchEnteredView; branchEnteredView = null;
        if (old == null) return;
        try { windows.RemoveViewImmediate(old); } catch { }
        old.Dispose();
    });
}
