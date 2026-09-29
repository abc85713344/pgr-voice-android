using Android.Content;
using Android.Graphics;
using Android.Views;

namespace PgrVoice.AndroidApp.Ui;

// 用户在当前捕获画面上拖出字幕框；编辑器不保留截图到磁盘。
public sealed class RegionEditor : View
{
    readonly Bitmap bitmap;
    readonly Paint paint = new(PaintFlags.AntiAlias);
    RectF displayed = new();
    ScreenRegion region;
    float startX, startY;
    public ScreenRegion Region => region.Clamp();
    public RegionEditor(Context context, Bitmap bitmap, ScreenRegion initial) : base(context)
    { this.bitmap = bitmap; region = initial; SetMinimumHeight(320); }
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float scale = Math.Min(Width / (float)bitmap.Width, Height / (float)bitmap.Height);
        float w = bitmap.Width * scale, h = bitmap.Height * scale;
        displayed.Set((Width-w)/2,(Height-h)/2,(Width+w)/2,(Height+h)/2);
        canvas.DrawColor(PgrTheme.Background);
        canvas.DrawBitmap(bitmap, null, displayed, null);
        paint.Color = Color.Argb(70, 0, 0, 0); paint.SetStyle(Paint.Style.Fill);
        canvas.DrawRect(displayed,paint);
        paint.Color = PgrTheme.Cyan; paint.StrokeWidth=PgrTheme.Dp(Context!,2); paint.SetStyle(Paint.Style.Stroke);
        canvas.DrawRect(displayed.Left+region.Left*w,displayed.Top+region.Top*h,
            displayed.Left+(region.Left+region.Width)*w,displayed.Top+(region.Top+region.Height)*h,paint);
    }
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if(e==null || displayed.Width()<=0)return false;
        float x=Math.Clamp((e.GetX()-displayed.Left)/displayed.Width(),0,1);
        float y=Math.Clamp((e.GetY()-displayed.Top)/displayed.Height(),0,1);
        if(e.Action==MotionEventActions.Down){startX=x;startY=y;Parent?.RequestDisallowInterceptTouchEvent(true);return true;}
        if(e.Action is MotionEventActions.Move or MotionEventActions.Up)
        {region=new(Math.Min(startX,x),Math.Min(startY,y),Math.Max(.05f,Math.Abs(x-startX)),Math.Max(.05f,Math.Abs(y-startY)));Invalidate();if(e.Action==MotionEventActions.Up)PerformClick();return true;}
        return true;
    }
    public override bool PerformClick(){base.PerformClick();return true;}
    protected override void Dispose(bool disposing){if(disposing)paint.Dispose();base.Dispose(disposing);}
}
