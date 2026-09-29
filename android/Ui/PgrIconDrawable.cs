using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;

namespace PgrVoice.AndroidApp.Ui;

/// <summary>Original 24-unit line icons. Static geometry is not simulated playback data.</summary>
[Register("cn/pgrvoice/player/ui/PgrIconDrawable")]
public sealed class PgrIconDrawable : Drawable
{
    private readonly PgrIconState state;
    private readonly Paint paint = new(PaintFlags.AntiAlias);
    private readonly global::Android.Graphics.Path path = new();
    private int alpha = 255;

    public PgrIconDrawable(string name, Color color, float density, int sizeDp = 20)
        : this(new PgrIconState(name, color, density, sizeDp)) { }

    internal PgrIconDrawable(PgrIconState state) => this.state = state;
    public override int IntrinsicWidth => Math.Max(1, (int)(state.SizeDp * state.Density + .5f));
    public override int IntrinsicHeight => IntrinsicWidth;
    public override ConstantState GetConstantState() => state;

    public override void Draw(Canvas canvas)
    {
        var bounds = Bounds;
        if (bounds.Width() <= 0 || bounds.Height() <= 0) return;
        float size = Math.Min(bounds.Width(), bounds.Height());
        int save = canvas.Save();
        canvas.Translate(bounds.Left + (bounds.Width() - size) / 2, bounds.Top + (bounds.Height() - size) / 2);
        canvas.Scale(size / 24f, size / 24f);
        paint.Color = state.Color;
        paint.Alpha = state.Color.A * alpha / 255;
        paint.SetStyle(Paint.Style.Stroke);
        paint.StrokeWidth = 1.65f;
        paint.StrokeCap = Paint.Cap.Round;
        paint.StrokeJoin = Paint.Join.Round;
        path.Reset();
        switch (state.Name)
        {
            case "archive": case "chapters":
                Outline(4, 6, 20, 21); Outline(3, 3, 21, 7);
                Line(9, 11, 15, 11); Line(8, 16, 16, 16); break;
            case "story":
                Poly(4, 3, 16, 3, 20, 7, 20, 21, 4, 21, 4, 3);
                Poly(15, 3, 15, 8, 20, 8); Line(8, 12, 16, 12); Line(8, 16, 14, 16); break;
            case "locate": case "ocr":
                Poly(3, 8, 3, 3, 8, 3); Poly(16, 3, 21, 3, 21, 8);
                Poly(21, 16, 21, 21, 16, 21); Poly(8, 21, 3, 21, 3, 16);
                Line(7, 9, 17, 9); Line(7, 13, 17, 13); Line(7, 17, 12, 17); break;
            case "history":
                path.AddArc(4, 4, 21, 21, -138, 300);
                Poly(3, 3, 3, 9, 9, 9); Poly(12, 8, 12, 13, 16, 15); break;
            case "settings":
                Line(4, 6, 20, 6); Line(4, 12, 20, 12); Line(4, 18, 20, 18); break;
            case "play":
                Poly(8, 4, 20, 12, 8, 20, 8, 4); break;
            case "pause":
                Line(8, 5, 8, 19); Line(16, 5, 16, 19); paint.StrokeWidth = 3; break;
            case "replay":
                path.AddArc(4, 4, 20, 20, -135, 310);
                Poly(3, 3, 3, 9, 9, 9); break;
            case "previous":
                Line(5, 5, 5, 19); Poly(19, 5, 8, 12, 19, 19, 19, 5); break;
            case "next":
                Line(19, 5, 19, 19); Poly(5, 5, 16, 12, 5, 19, 5, 5); break;
            case "branch":
                Poly(6, 19, 6, 6); Poly(6, 14, 16, 14, 18, 12, 18, 6); break;
            case "original":
                Poly(3, 9, 7, 9, 12, 5, 12, 19, 7, 15, 3, 15, 3, 9);
                path.AddArc(9, 7, 19, 17, -55, 110);
                path.AddArc(7, 3, 25, 21, -48, 96); break;
            case "import":
                Poly(4, 14, 4, 20, 20, 20, 20, 14); Line(12, 3, 12, 15);
                Poly(7, 10, 12, 15, 17, 10); break;
            case "chevron": case "chevron_right":
                Poly(9, 5, 16, 12, 9, 19); break;
            case "chevron_up":
                Poly(5, 15, 12, 8, 19, 15); break;
            case "chevron_down":
                Poly(5, 9, 12, 16, 19, 9); break;
            case "wave":
                // Static app mark, never animated or presented as live sound levels.
                Line(4, 9, 4, 15); Line(8, 5, 8, 19); Line(12, 8, 12, 16);
                Line(16, 3, 16, 21); Line(20, 9, 20, 15); break;
            case "close": case "hide":
                Line(6, 6, 18, 18); Line(18, 6, 6, 18); break;
            case "open":
                Poly(10, 4, 4, 4, 4, 20, 20, 20, 20, 14);
                Poly(14, 3, 21, 3, 21, 10); Line(12, 12, 21, 3); break;
            case "bookmark":
                Poly(6, 3, 18, 3, 18, 21, 12, 17, 6, 21, 6, 3); break;
            case "headphones":
                path.AddArc(4, 3, 20, 19, 180, 180);
                Poly(4, 11, 4, 19, 8, 19, 8, 12, 4, 12);
                Poly(20, 11, 20, 19, 16, 19, 16, 12, 20, 12); break;
            default:
                Outline(5, 5, 19, 19); Line(9, 12, 15, 12); break;
        }
        canvas.DrawPath(path, paint);
        if (state.Name == "settings")
        {
            paint.SetStyle(Paint.Style.Fill);
            canvas.DrawCircle(9, 6, 2.5f, paint); canvas.DrawCircle(16, 12, 2.5f, paint); canvas.DrawCircle(8, 18, 2.5f, paint);
        }
        if (state.Name == "branch")
        {
            paint.SetStyle(Paint.Style.Fill);
            canvas.DrawCircle(6, 5, 2, paint); canvas.DrawCircle(18, 5, 2, paint); canvas.DrawCircle(6, 19, 2, paint);
        }
        canvas.RestoreToCount(save);
    }

    private void Line(float x1, float y1, float x2, float y2) { path.MoveTo(x1, y1); path.LineTo(x2, y2); }
    private void Outline(float left, float top, float right, float bottom) => path.AddRect(left, top, right, bottom, global::Android.Graphics.Path.Direction.Cw!);
    private void Poly(params float[] points)
    {
        path.MoveTo(points[0], points[1]);
        for (int i = 2; i < points.Length; i += 2) path.LineTo(points[i], points[i + 1]);
    }

    public override void SetAlpha(int value) { alpha = Math.Clamp(value, 0, 255); InvalidateSelf(); }
    public override void SetColorFilter(ColorFilter? colorFilter) { paint.SetColorFilter(colorFilter); InvalidateSelf(); }
#pragma warning disable CS0672, CA1422
    public override int Opacity => (int)Format.Translucent;
#pragma warning restore CS0672, CA1422
    protected override void Dispose(bool disposing) { if (disposing) { paint.Dispose(); path.Dispose(); } base.Dispose(disposing); }

}

// A public registered peer avoids runtime lookup of a private nested ConstantState subclass.
// Only immutable icon values are shared; each NewDrawable owns a separate Paint and Path.
[Register("cn/pgrvoice/player/ui/PgrIconState")]
public sealed class PgrIconState : Drawable.ConstantState
{
    public string Name { get; }
    public Color Color { get; }
    public float Density { get; }
    public int SizeDp { get; }

    public PgrIconState() : this("archive", Color.White, 1, 20) { }
    public PgrIconState(string name, Color color, float density, int sizeDp)
    {
        Name = name; Color = color; Density = density; SizeDp = sizeDp;
    }
    public override global::Android.Content.PM.ConfigChanges ChangingConfigurations => 0;
    public override Drawable NewDrawable() => new PgrIconDrawable(this);
    public override Drawable NewDrawable(Resources? resources) => new PgrIconDrawable(this);
}
