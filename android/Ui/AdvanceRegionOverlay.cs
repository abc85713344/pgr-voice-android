using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Views;
using Android.Widget;

namespace PgrVoice.AndroidApp.Ui;

/// <summary>在物理屏幕上取点，所有选择动作由本窗口消费，不透传给游戏。</summary>
public sealed class AdvanceRegionOverlay : IDisposable
{
    readonly IWindowManager windows;
    readonly FrameLayout root;
    readonly SelectionView selection;
    bool showing,disposed;
    public AdvanceRegionOverlay(Context context,int width,int height,Action<ScreenRegion?> completed)
    {
        windows=context.GetSystemService(Context.WindowService)!.JavaCast<IWindowManager>();
        root=new FrameLayout(context);
        selection=new SelectionView(context,width,height);
        root.AddView(selection,new FrameLayout.LayoutParams(-1,-1));
        var panel=new LinearLayout(context){Orientation=Orientation.Vertical};
        panel.SetPadding(20,12,20,12);panel.SetBackgroundColor(Color.Argb(235,22,25,30));
        var help=new TextView(context){Text="拖出游戏“下一句”的触碰范围\n点按跟随：你点框内哪里就触碰哪里。自动播放：点击框中心。避开选项和系统栏。",TextSize=15};
        help.SetTextColor(Color.White);panel.AddView(help);
        var row=new LinearLayout(context){Orientation=Orientation.Horizontal};panel.AddView(row);
        var save=new Button(context){Text="保存区域"};var cancel=new Button(context){Text="取消"};
        row.AddView(save,new LinearLayout.LayoutParams(0,-2,1));row.AddView(cancel,new LinearLayout.LayoutParams(0,-2,1));
        save.Click+=(_,_)=>{if(selection.Region is { } region){Dispose();completed(region);}else help.Text="请先在游戏的下一句区域拖出一个方框。";};
        cancel.Click+=(_,_)=>{Dispose();completed(null);};
        root.AddView(panel,new FrameLayout.LayoutParams(-1,-2,GravityFlags.Top));
    }
    public void Show()
    {
        var p=new WindowManagerLayoutParams(-1,-1,WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable|WindowManagerFlags.LayoutInScreen,Format.Translucent){Gravity=GravityFlags.Top|GravityFlags.Left};
        windows.AddView(root,p);showing=true;
    }
    public void Dispose(){if(disposed)return;disposed=true;try{if(showing)windows.RemoveView(root);}finally{showing=false;selection.Dispose();root.Dispose();}}

    [Register("cn/pgrvoice/player/AdvanceSelectionView")]
    public sealed class SelectionView : View
    {
        readonly Paint paint=new(PaintFlags.AntiAlias);
        readonly int displayWidth,displayHeight;
        float startX,startY;
        public ScreenRegion? Region{get;private set;}
        public SelectionView(Context context,int width,int height):base(context){displayWidth=width;displayHeight=height;}
        public SelectionView(IntPtr handle,JniHandleOwnership ownership):base(handle,ownership){}
        protected override void OnDraw(Canvas canvas)
        {
            canvas.DrawColor(Color.Argb(35,0,0,0));
            if(Region is not { } r)return;
            int[] pos=new int[2];GetLocationOnScreen(pos);
            using var bounds=new RectF(r.Left*displayWidth-pos[0],r.Top*displayHeight-pos[1],(r.Left+r.Width)*displayWidth-pos[0],(r.Top+r.Height)*displayHeight-pos[1]);
            paint.Color=PgrTheme.Red;paint.SetStyle(Paint.Style.Stroke);paint.StrokeWidth=3;
            canvas.DrawRect(bounds,paint);canvas.DrawCircle(bounds.CenterX(),bounds.CenterY(),9,paint);
        }
        public override bool OnTouchEvent(MotionEvent? e)
        {
            if(e==null||displayWidth<=0||displayHeight<=0)return false;
            float x=Math.Clamp(e.RawX/displayWidth,0,1),y=Math.Clamp(e.RawY/displayHeight,0,1);
            if(e.Action==MotionEventActions.Down){startX=x;startY=y;return true;}
            if(e.Action is MotionEventActions.Move or MotionEventActions.Up)
            {
                float w=Math.Abs(x-startX),h=Math.Abs(y-startY);
                Region=w*displayWidth>=12&&h*displayHeight>=12?new(Math.Min(x,startX),Math.Min(y,startY),w,h):null;
                Invalidate();if(e.Action==MotionEventActions.Up)PerformClick();
            }
            return true;
        }
        public override bool PerformClick(){base.PerformClick();return true;}
        protected override void Dispose(bool disposing){if(disposing)paint.Dispose();base.Dispose(disposing);}
    }
}
