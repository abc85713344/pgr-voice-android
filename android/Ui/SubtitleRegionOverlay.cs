using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

namespace PgrVoice.AndroidApp.Ui;

/// <summary>在游戏上方编辑已捕获的字幕截图，不切换前台应用；持有并统一释放截图。</summary>
public sealed class SubtitleRegionOverlay : IDisposable
{
    readonly Context context;
    readonly Bitmap bitmap;
    readonly ScreenRegion initial;
    IWindowManager? windows;
    LinearLayout? root;
    RegionEditor? editor;
    bool showing, disposed;
    public event Action<ScreenRegion>? Saved;
    public event Action? Closed;

    public SubtitleRegionOverlay(Context context, Bitmap bitmap, ScreenRegion initial)
    {
        this.context = context;
        this.bitmap = bitmap;
        this.initial = initial;
    }

    public void Show()
    {
        if (disposed || showing) return;
        windows = (context.ApplicationContext ?? context).GetSystemService(Context.WindowService)!.JavaCast<IWindowManager>();
        root = new LinearLayout(context) { Orientation = Orientation.Vertical, Clickable = true };
        int padding = PgrTheme.Dp(context, 12);
        root.SetPadding(padding, padding, padding, padding);
        root.SetBackgroundColor(PgrTheme.Background);
        var title = new TextView(context)
        {
            Text = "框选角色名和完整对白",
            TextSize = 16,
            ContentDescription = "在截图上拖动以设置字幕识别范围"
        };
        title.SetTextColor(PgrTheme.Foreground);
        title.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        root.AddView(title, new LinearLayout.LayoutParams(-1, -2));
        var hint = new TextView(context) { Text = "避开 NEXT 和波形。保存前请保持游戏屏幕方向不变。", TextSize = 12 };
        hint.SetTextColor(PgrTheme.Secondary);
        hint.SetPadding(0, PgrTheme.Dp(context, 4), 0, PgrTheme.Dp(context, 8));
        root.AddView(hint, new LinearLayout.LayoutParams(-1, -2));
        editor = new RegionEditor(context, bitmap, initial);
        // RegionEditor also serves a dialog; here the weighted area must fit short landscape screens.
        editor.SetMinimumHeight(0);
        root.AddView(editor, new LinearLayout.LayoutParams(-1, 0, 1));
        var actions = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        var cancel = new Button(context) { Text = "取消", TextSize = 14 };
        PgrTheme.StyleButton(cancel);
        cancel.Click += (_, _) => Dispose();
        var save = new Button(context) { Text = "保存字幕区域", TextSize = 14 };
        PgrTheme.StyleButton(save, primary: true);
        save.Click += (_, _) => Save();
        int buttonHeight = PgrTheme.Dp(context, 48);
        actions.AddView(cancel, new LinearLayout.LayoutParams(0, buttonHeight, 1) { RightMargin = PgrTheme.Dp(context, 5) });
        actions.AddView(save, new LinearLayout.LayoutParams(0, buttonHeight, 1) { LeftMargin = PgrTheme.Dp(context, 5) });
        root.AddView(actions, new LinearLayout.LayoutParams(-1, -2) { TopMargin = PgrTheme.Dp(context, 8) });

        // The full window consumes touches, including blank areas; system bars remain outside it.
        // NotFocusable keeps the game as the focused application and does not request accessibility.
        var parameters = new WindowManagerLayoutParams(-1, -1, WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable, Format.Translucent) { Gravity = GravityFlags.Top | GravityFlags.Left };
        if (Build.VERSION.SdkInt >= BuildVersionCodes.P) parameters.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.Never;
        windows.AddView(root, parameters);
        showing = true;
    }

    void Save()
    {
        if (disposed || editor == null) return;
        var region = editor.Region;
        var saved = Saved;
        // Close first so session validation messages in the normal controls remain visible afterwards.
        Dispose();
        saved?.Invoke(region);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (root != null && (showing || root.IsAttachedToWindow)) windows?.RemoveViewImmediate(root);
        }
        catch (Java.Lang.IllegalArgumentException) { }
        finally
        {
            showing = false;
            root?.RemoveAllViews();
            editor?.Dispose(); editor = null;
            bitmap.Dispose();
            root?.Dispose(); root = null;
            var closed = Closed;
            Saved = null; Closed = null;
            closed?.Invoke();
        }
    }
}
