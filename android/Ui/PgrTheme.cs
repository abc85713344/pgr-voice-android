using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace PgrVoice.AndroidApp.Ui;

/// <summary>原创档案界面：用明度与留白区分层级，只以少量红色标识当前操作。</summary>
public static class PgrTheme
{
    public static readonly Color Background = Color.ParseColor("#101113");
    public static readonly Color Panel = Color.ParseColor("#191A1D");
    public static readonly Color Raised = Color.ParseColor("#242529");
    public static readonly Color Border = Color.ParseColor("#34353A");
    public static readonly Color Foreground = Color.ParseColor("#EEECE7");
    public static readonly Color Secondary = Color.ParseColor("#A3A1A0");
    public static readonly Color Red = Color.ParseColor("#D6535D");
    public static readonly Color Cyan = Color.ParseColor("#9AD5D1");
    public static readonly Color Selection = Color.ParseColor("#312327");

    public static int Dp(Context context, float value) => (int)(value * context.Resources!.DisplayMetrics!.Density + .5f);

    // Native shapes provide ConstantState for ripple and background cloning.
    public static Drawable Surface(Context context, Color fill, Color? stroke = null, Color? accent = null)
    {
        var body = Shape(context, fill, stroke ?? Border);
        if (accent is not { } markerColor) return body;
        var marker = Shape(context, markerColor);
        var surface = new LayerDrawable(new Drawable[] { body, marker });
        surface.SetLayerSize(1, Dp(context, 2), Dp(context, 18));
        surface.SetLayerGravity(1, GravityFlags.Left | GravityFlags.CenterVertical);
        return surface;
    }

    public static void StyleButton(Button button, bool selected = false, bool primary = false, bool quiet = false, bool alignStart = false)
    {
        var context = button.Context!;
        Color fill = primary ? Color.ParseColor("#AC3543") : selected ? Selection : quiet ? Color.Transparent : Raised;
        Color stroke = primary || quiet ? Color.Transparent : selected ? Color.ParseColor("#583239") : Raised;
        button.BackgroundTintList = null;
        button.Background = new RippleDrawable(ColorStateList.ValueOf(Color.Argb(34, 255, 255, 255)),
            Surface(context, fill, stroke, selected && !primary ? Red : null), Shape(context, Color.White));
        button.SetTextColor(selected || primary ? Foreground : quiet ? Secondary : Foreground);
        button.SetAllCaps(false);
        button.SetSingleLine(false);
        button.Ellipsize = null;
        button.SetMinHeight(Dp(context, 48));
        button.SetMinimumHeight(Dp(context, 48));
        button.SetMinWidth(0);
        button.SetMinimumWidth(0);
        button.SetPadding(Dp(context, alignStart ? 14 : 7), Dp(context, 11), Dp(context, alignStart ? 14 : 7), Dp(context, 11));
        button.Gravity = (alignStart ? GravityFlags.Start : GravityFlags.CenterHorizontal) | GravityFlags.CenterVertical;
        button.SetTypeface(Typeface.Default, selected || primary ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        button.StateListAnimator = null;
        button.Elevation = 0;
    }

    public static void StyleInput(EditText input)
    {
        input.SetTextColor(Foreground);
        input.SetHintTextColor(Secondary);
        input.TextSize = 16;
        input.BackgroundTintList = null;
        input.Background = Surface(input.Context!, Panel);
        input.SetPadding(Dp(input.Context!, 14), Dp(input.Context!, 12), Dp(input.Context!, 14), Dp(input.Context!, 12));
        input.SetMinimumHeight(Dp(input.Context!, 52));
    }

    private static GradientDrawable Shape(Context context, Color fill, Color? stroke = null)
    {
        var shape = new GradientDrawable();
        shape.SetShape(ShapeType.Rectangle);
        shape.SetColor(fill);
        shape.SetCornerRadius(Dp(context, 3));
        if (stroke is { } outline && outline.A != 0) shape.SetStroke(Math.Max(1, Dp(context, .75f)), outline);
        return shape;
    }
}
