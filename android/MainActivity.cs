using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Media.Projection;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using PgrVoice.AndroidApp.Ocr;
using PgrVoice.AndroidApp.Platform;
using PgrVoice.AndroidApp.Ui;
using PgrVoice.AndroidApp.Contracts;

namespace PgrVoice.AndroidApp;

[Activity(Label="战双剧情配音",MainLauncher=true,Exported=true,LaunchMode=LaunchMode.SingleTop,
    ConfigurationChanges=ConfigChanges.Orientation|ConfigChanges.ScreenSize|ConfigChanges.SmallestScreenSize|ConfigChanges.ScreenLayout|ConfigChanges.KeyboardHidden)]
public sealed partial class MainActivity : Activity
{
    const int ImportRequest=10,CaptureRequest=11,ExportRequest=12,OverlayRequest=13,RestoreRequest=14;
    AppSession session=null!;
    LinearLayout root=null!,content=null!,currentCard=null!,positionRow=null!,headingRow=null!,tabRow=null!;
    ScrollView pageScroll=null!;
    TextView status=null!,current=null!,title=null!,currentMeta=null!,offlineTag=null!;
    Button confirmPositionButton=null!,orientationButton=null!;
    ImageView headingIcon=null!;
    IReadOnlyList<MatchCandidate>? renderedLocateCandidates;
    TextView? autoPlaybackState;
    Button? autoPlaybackStopButton,autoPlaybackPermissionButton;
    Button? captureStatusButton;
    Action? captureStatusTick;
    bool captureStatusUiVisible,captureStatusTickScheduled;
    readonly List<Button> tabButtons=new();
    readonly string[] tabIcons={"archive","story","locate","branch","history","settings"};
    readonly int[] tabPages={0,1,2,6,3,4};
    Bitmap? archiveArt;
    SubtitleRegionOverlay? subtitleRegionOverlay;
    int styledTab=-1;
    int page,linePage,historyPage;
    string search="",exportText="";
    CancellationTokenSource? importCancellation;
    bool alive,returnToGameAfterCapture,captureRequestPending;
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);alive=true;
        session=AppSession.Get(this);
        returnToGameAfterCapture=savedInstanceState?.GetBoolean("return_to_game_after_capture",false)??false;
        captureRequestPending=savedInstanceState?.GetBoolean("capture_request_pending",false)??false;
        root=new LinearLayout(this){Orientation=Orientation.Vertical};root.SetPadding(Dp(16),Dp(28),Dp(16),Dp(20));
        root.SetBackgroundColor(PgrTheme.Background);
        SetContentView(root);
        root.SetOnApplyWindowInsetsListener(new SafeInsets(this));
        root.RequestApplyInsets();
        headingRow=Row(root);headingRow.SetGravity(GravityFlags.CenterVertical);headingRow.SetPadding(0,0,0,Dp(9));
        headingIcon=Icon("wave",PgrTheme.Red,26);headingRow.AddView(headingIcon,new LinearLayout.LayoutParams(Dp(32),Dp(36)){RightMargin=Dp(9)});
        title=Text("战双剧情配音",20);title.SetTypeface(Typeface.Default,TypefaceStyle.Bold);headingRow.AddView(title,new LinearLayout.LayoutParams(0,ViewGroup.LayoutParams.WrapContent,1));
        title.SetMaxLines(1);title.Ellipsize=global::Android.Text.TextUtils.TruncateAt.End;
        offlineTag=Text("离线配音",10);offlineTag.SetTextColor(PgrTheme.Secondary);headingRow.AddView(offlineTag);
        orientationButton=new Button(this){Text="横 / 竖屏",TextSize=12};PgrTheme.StyleButton(orientationButton,quiet:true);
        orientationButton.Click+=(_,_)=>Safe(ChooseMainOrientation);
        headingRow.AddView(orientationButton,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent,ViewGroup.LayoutParams.WrapContent){LeftMargin=Dp(6)});
        CreateChapterCategoryBar();
        pageScroll=new ScrollView(this){FillViewport=true};pageScroll.SetClipToPadding(false);pageScroll.VerticalScrollBarEnabled=false;
        content=new LinearLayout(this){Orientation=Orientation.Vertical};content.SetPadding(0,Dp(5),0,Dp(20));pageScroll.AddView(content);
        root.AddView(pageScroll,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent,0,1));
        currentCard=new LinearLayout(this){Orientation=Orientation.Vertical,Clickable=true,Focusable=true};currentCard.Background=PgrTheme.Surface(this,PgrTheme.Panel,PgrTheme.Border);
        currentCard.ContentDescription="当前台词和配音状态，点击查看完整内容";currentCard.Click+=(_,_)=>{if(session.HasSettingsSaveWarning)Info("设置尚未保存",session.Status);else if(session.Listening.IsPlaying)ShowPage(5);else Info("当前配音状态",(session.Engine?.Current is { } currentNode?currentNode.Speaker+"："+currentNode.Text+"\n\n":"")+session.Status);};
        root.AddView(currentCard,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent,ViewGroup.LayoutParams.WrapContent){TopMargin=Dp(4),BottomMargin=Dp(4)});
        currentMeta=Text("当前台词",11);currentMeta.SetTextColor(PgrTheme.Cyan);currentCard.AddView(currentMeta);
        current=Text("",14);current.SetMaxLines(2);current.Ellipsize=global::Android.Text.TextUtils.TruncateAt.End;currentCard.AddView(current);
        status=Text("",11);status.SetTextColor(PgrTheme.Secondary);status.SetMaxLines(1);status.Ellipsize=global::Android.Text.TextUtils.TruncateAt.End;currentCard.AddView(status);
        positionRow=Row(root);
        confirmPositionButton=Button(positionRow,"确认当前句",ConfirmFooterPosition);PgrTheme.StyleButton(confirmPositionButton,primary:true);
        tabRow=Row(root);string[] labels={"章节","剧情","定位","分支","历史","设置"};
        for(int i=0;i<labels.Length;i++){int targetPage=tabPages[i];var tab=Button(tabRow,labels[i],()=>ShowPage(targetPage));tab.TextSize=10;tabButtons.Add(tab);}
        session.Changed+=UpdateStatus;session.ContentChanged+=Render;session.CapturePermissionRequested+=RequestCapture;
        session.NavigationRequested+=OnNavigation;
        session.Listening.Changed+=OnListeningChanged;
        ApplyMainOrientation();AdjustLayout();UpdateStatus();HandleIntent(Intent);Render();
        RestoreOrStartOnboarding(savedInstanceState);
    }
    protected override void OnNewIntent(Intent? intent){base.OnNewIntent(intent);HandleIntent(intent);Render();}
    protected override void OnSaveInstanceState(Bundle outState)
    {
        outState.PutBoolean("return_to_game_after_capture",returnToGameAfterCapture);
        outState.PutBoolean("capture_request_pending",captureRequestPending);
        outState.PutBoolean("onboarding_active",onboardingActive);
        base.OnSaveInstanceState(outState);
    }
    public override void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig)
    {base.OnConfigurationChanged(newConfig);AdjustLayout();root.RequestApplyInsets();Render();}
    void AdjustLayout()
    {
        bool compact=CompactLayout;
        currentCard.Visibility=page==5?ViewStates.Gone:ViewStates.Visible;
        positionRow.Visibility=page==5?ViewStates.Gone:ViewStates.Visible;
        title.TextSize=compact?16:20;current.TextSize=compact?12:14;status.TextSize=compact?10:11;
        current.SetMaxLines(compact||page!=1?1:2);status.SetMaxLines(1);
        headingRow.SetPadding(0,0,0,Dp(compact?1:9));
        title.SetPadding(0,Dp(compact?1:7),0,Dp(compact?1:7));
        headingIcon.LayoutParameters=new LinearLayout.LayoutParams(Dp(compact?24:32),Dp(compact?24:36)){RightMargin=Dp(compact?5:9)};
        orientationButton.SetSingleLine(true);orientationButton.SetPadding(Dp(7),Dp(3),Dp(7),Dp(3));
        orientationButton.SetMinHeight(Dp(compact?36:44));orientationButton.SetMinimumHeight(Dp(compact?36:44));
        orientationButton.ContentDescription="切换主界面横竖屏，当前"+MainOrientationLabel;
        currentMeta.TextSize=compact?10:11;currentMeta.SetPadding(0,Dp(compact?0:3),0,Dp(compact?1:3));
        current.SetPadding(0,0,0,Dp(compact?1:6));status.SetPadding(0,0,0,Dp(compact?0:2));
        currentCard.SetPadding(Dp(13),Dp(compact?2:7),Dp(13),Dp(compact?2:7));
        if(currentCard.LayoutParameters is LinearLayout.LayoutParams cardLayout)
        {cardLayout.TopMargin=cardLayout.BottomMargin=Dp(compact?1:4);currentCard.LayoutParameters=cardLayout;}
        currentMeta.Visibility=compact&&page!=2?ViewStates.Gone:ViewStates.Visible;
        offlineTag.Visibility=compact?ViewStates.Gone:ViewStates.Visible;
        AdjustPositionButton();tabRow.SetPadding(0,Dp(compact?1:4),0,0);
        for(int i=0;i<tabButtons.Count;i++)StyleTab(i);
    }
    string MainOrientationLabel=>session.Settings.MainOrientation switch {"portrait"=>"竖屏","landscape"=>"横屏",_=>"跟随手机"};
    void ChooseMainOrientation()=>Choose("主界面方向（不改变游戏）",new[]{"竖屏","横屏","跟随手机"},index=>
    {
        session.Settings.MainOrientation=index switch {0=>"portrait",1=>"landscape",_=>"auto"};
        session.SaveSettings();ApplyMainOrientation();AdjustLayout();
    });
    void ApplyMainOrientation()=>RequestedOrientation=session.Settings.MainOrientation switch
    {"portrait"=>ScreenOrientation.Portrait,"landscape"=>ScreenOrientation.Landscape,_=>ScreenOrientation.Unspecified};
    void AdjustPositionButton()
    {
        bool compact=CompactLayout;
        var button=confirmPositionButton;
        button.TextSize=compact?12:14;button.SetSingleLine(true);
        button.Ellipsize=global::Android.Text.TextUtils.TruncateAt.End;
        button.SetPadding(Dp(5),Dp(compact?4:11),Dp(5),Dp(compact?4:11));
        button.SetMinHeight(Dp(compact?36:48));button.SetMinimumHeight(Dp(compact?36:48));
        if(button.LayoutParameters is LinearLayout.LayoutParams layout)
        {layout.TopMargin=Dp(compact?1:3);layout.BottomMargin=Dp(compact?1:5);button.LayoutParameters=layout;}
    }
    void HandleIntent(Intent? intent)
    {
        string? target=intent?.GetStringExtra("page");
        if(target=="Branch"){page=1;Window?.DecorView?.Post(ShowBranches);}
        else if(target=="Chapters")page=0;
        else if(target=="History")page=3;
        else if(target=="Settings")page=4;
        else if(target=="BranchSettings")page=6;
        else if(target=="Locate")page=2;
        else if(target=="Listening"){session.Listening.OpenLast();page=5;}
        else if(target=="Feedback")
        {
            page=1;intent?.RemoveExtra("page");
            var snapshot=session.TakePendingLineFeedback();
            if(snapshot!=null)Window?.DecorView?.Post(()=>Safe(()=>ShowLineFeedbackCategories(snapshot)));
        }
        else if(target=="Capture")
        {
            returnToGameAfterCapture=true;
            intent?.RemoveExtra("page");
            Window?.DecorView?.Post(RequestCapture);
        }
    }
    protected override void OnResume(){base.OnResume();if(session!=null){session.SetUiVisible(true);UpdateStatus();ResumeCaptureStatusUi();ResumeListeningUi();ResumeOnboarding();}}
    protected override void OnPause(){PauseOnboarding();PauseListeningUi();PauseCaptureStatusUi();session?.SetUiVisible(false);session?.SaveProgress();base.OnPause();}
    protected override void OnDestroy()
    {
        librarySearchCancellation?.Cancel();
        DestroyOnboarding();
        importCancellation?.Cancel();importDialog?.Dismiss();importDialog=null;
        alive=false;session.Changed-=UpdateStatus;session.ContentChanged-=Render;session.CapturePermissionRequested-=RequestCapture;session.NavigationRequested-=OnNavigation;
        subtitleRegionOverlay?.Dispose();subtitleRegionOverlay=null;
        session.Listening.Changed-=OnListeningChanged;
        PauseCaptureStatusUi();
        PauseListeningUi();
        base.OnDestroy();
        archiveArt?.Dispose();archiveArt=null;
    }
    void OnNavigation(OverlayCommand command){if(command==OverlayCommand.Branch)page=1;else if(command==OverlayCommand.History)page=3;else if(command==OverlayCommand.Chapters)page=0;}
    int Dp(int n)=>(int)(n*Resources!.DisplayMetrics!.Density+.5f);
    bool CompactLayout=>Resources!.Configuration!.Orientation==global::Android.Content.Res.Orientation.Landscape;
    TextView Text(string text,int size=16)
    {var v=new TextView(this){Text=text,TextSize=size};v.SetTextColor(PgrTheme.Foreground);v.SetPadding(0,Dp(CompactLayout?3:7),0,Dp(CompactLayout?3:7));v.SetLineSpacing(Dp(2),1);return v;}
    ImageView Icon(string name,Color color,int size=20)
    {var icon=new ImageView(this);icon.SetImageDrawable(new PgrIconDrawable(name,color,Resources!.DisplayMetrics!.Density,size));icon.ImportantForAccessibility=ImportantForAccessibility.No;return icon;}
    void ButtonIcon(Button button,string name,bool primary=false)
    {button.SetCompoundDrawablesWithIntrinsicBounds(new PgrIconDrawable(name,PgrTheme.Foreground,Resources!.DisplayMetrics!.Density,18),null,null,null);button.CompoundDrawablePadding=Dp(7);if(primary)PgrTheme.StyleButton(button,primary:true);}
    void StyleTab(int index)
    {
        var button=tabButtons[index];bool selected=tabPages[index]==page||(page==5&&index==0);
        PgrTheme.StyleButton(button,quiet:true);button.Selected=selected;button.SetTextColor(selected?PgrTheme.Foreground:PgrTheme.Secondary);
        var icon=new PgrIconDrawable(tabIcons[index],selected?PgrTheme.Red:PgrTheme.Secondary,Resources!.DisplayMetrics!.Density,CompactLayout?18:21);
        button.SetCompoundDrawablesWithIntrinsicBounds(CompactLayout?icon:null,CompactLayout?null:icon,null,null);
        button.CompoundDrawablePadding=Dp(CompactLayout?4:3);button.SetPadding(Dp(CompactLayout?6:0),Dp(CompactLayout?3:4),Dp(CompactLayout?6:0),Dp(CompactLayout?3:4));
        button.SetMinHeight(Dp(CompactLayout?36:52));button.SetMinimumHeight(Dp(CompactLayout?36:52));button.TextSize=CompactLayout?11:10;
        if(button.LayoutParameters is LinearLayout.LayoutParams layout)
        {layout.TopMargin=Dp(CompactLayout?1:3);layout.BottomMargin=Dp(CompactLayout?1:5);button.LayoutParameters=layout;}
        button.SetTypeface(Typeface.Default,selected?TypefaceStyle.Bold:TypefaceStyle.Normal);
    }
    static string ChapterNumber(string title)
    {var match=System.Text.RegularExpressions.Regex.Match(title,@"第\s*(\d+)\s*章");return match.Success?match.Groups[1].Value.PadLeft(2,'0'):"—";}
    static string PackTitle(string title)
    {
        return ChapterCatalog.Title(title);
    }
    static string ChapterTitle(string title)
    {return System.Text.RegularExpressions.Regex.Replace(PackTitle(title),@"^第\s*\d+\s*章[\s_·]*","").Trim();}
    void ArchiveHero(int chapterCount)
    {
        var hero=new FrameLayout(this);hero.ContentDescription="剧情档案";
        int height=CompactLayout?156:176;
        if(archiveArt==null)
        {using var stream=Assets!.Open("ui/story-archive-v2.png");using var options=new BitmapFactory.Options{InSampleSize=2};archiveArt=BitmapFactory.DecodeStream(stream,null,options);}
        var image=new ImageView(this);image.SetImageBitmap(archiveArt);image.SetScaleType(ImageView.ScaleType.CenterCrop);hero.AddView(image,new FrameLayout.LayoutParams(-1,-1));
        var shade=new View(this);shade.Background=new GradientDrawable(GradientDrawable.Orientation.LeftRight,new[]{Color.Argb(165,10,13,17).ToArgb(),Color.Argb(10,10,13,17).ToArgb()});hero.AddView(shade,new FrameLayout.LayoutParams(-1,-1));
        var body=new LinearLayout(this){Orientation=Orientation.Vertical};body.SetPadding(Dp(18),Dp(16),Dp(16),Dp(14));
        var eyebrow=Text("剧情档案",11);eyebrow.SetTextColor(Color.ParseColor("#C3C7CE"));eyebrow.SetPadding(0,0,0,Dp(6));body.AddView(eyebrow);
        var headline=Text("故事仍在回响",27);headline.SetTypeface(Typeface.Default,TypefaceStyle.Bold);headline.SetPadding(0,0,0,Dp(6));body.AddView(headline);
        var meta=Text($"{chapterCount:00} 个章节  /  本地配音库",11);meta.SetTextColor(Color.ParseColor("#C3C7CE"));meta.SetPadding(0,0,0,0);body.AddView(meta);
        body.AddView(new View(this),new LinearLayout.LayoutParams(1,0,1));
        if(session.Engine is { } engine)
        {
            var resume=new Button(this){Text="继续当前剧情",TextSize=12};PgrTheme.StyleButton(resume,primary:true,alignStart:true);ButtonIcon(resume,"story");
            resume.SetMaxLines(1);resume.Ellipsize=global::Android.Text.TextUtils.TruncateAt.End;resume.Click+=(_,_)=>Safe(()=>ShowPage(1));body.AddView(resume,new LinearLayout.LayoutParams(-2,Dp(48)));
        }
        hero.AddView(body,new FrameLayout.LayoutParams(-1,-1));content.AddView(hero,new LinearLayout.LayoutParams(-1,Dp(height)){BottomMargin=Dp(10)});
    }
    void Line(string text,int size=16)
    {
        var label=Text(text,CompactLayout&&size>=24?20:size);
        if(size<=14)label.SetTextColor(PgrTheme.Secondary);
        if(size>=17){label.SetTypeface(Typeface.Default,TypefaceStyle.Bold);label.SetPadding(0,Dp(CompactLayout?6:13),0,Dp(CompactLayout?4:9));}
        content.AddView(label);
    }
    LinearLayout Row(LinearLayout parent)
    {var row=new LinearLayout(this){Orientation=Orientation.Horizontal};parent.AddView(row);return row;}
    Button Button(LinearLayout parent,string label,Action click)
    {
        var b=new Button(this){Text=label,TextSize=14};PgrTheme.StyleButton(b,alignStart:parent.Orientation!=Orientation.Horizontal);
        b.Click+=(_,_)=>Safe(click);
        var layout=parent.Orientation==Orientation.Horizontal?new LinearLayout.LayoutParams(0,ViewGroup.LayoutParams.WrapContent,1):new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent,ViewGroup.LayoutParams.WrapContent);
        layout.SetMargins(parent.Orientation==Orientation.Horizontal?Dp(3):0,Dp(3),parent.Orientation==Orientation.Horizontal?Dp(3):0,Dp(5));
        parent.AddView(b,layout);return b;
    }
    LinearLayout Card(LinearLayout parent,bool selected=false)
    {
        var card=new LinearLayout(this){Orientation=Orientation.Vertical};card.SetPadding(Dp(14),Dp(9),Dp(14),Dp(10));
        card.Background=PgrTheme.Surface(this,PgrTheme.Panel,selected?PgrTheme.Red:PgrTheme.Border,selected?PgrTheme.Red:null);
        parent.AddView(card,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent,ViewGroup.LayoutParams.WrapContent){TopMargin=Dp(8),BottomMargin=Dp(7)});return card;
    }
    void StyleDialog(AlertDialog? dialog)
    {
        if(dialog==null)return;
        dialog.Window?.SetBackgroundDrawable(PgrTheme.Surface(this,PgrTheme.Raised));
        foreach(var which in new[]{(int)DialogButtonType.Positive,(int)DialogButtonType.Negative,(int)DialogButtonType.Neutral})
        {
            var button=dialog.GetButton(which);if(button==null)continue;
            button.SetTextColor(which==(int)DialogButtonType.Positive?PgrTheme.Cyan:PgrTheme.Secondary);button.SetMinimumHeight(Dp(48));button.SetAllCaps(false);
        }
    }
    void Safe(Action action){try{if(session.IsDeletingPackage){Info("请稍候","正在删除章节文件，请等待完成。");return;}action();}catch(Exception ex){Info("操作未完成",ex.Message);}}
    void Info(string title,string message)
    {var b=new AlertDialog.Builder(this);b.SetTitle(title);b.SetMessage(message);b.SetPositiveButton("知道了",(_,_)=>{});StyleDialog(b.Show());}
    void Confirm(string title,string message,Action action)
    {var b=new AlertDialog.Builder(this);b.SetTitle(title);b.SetMessage(message);b.SetNegativeButton("取消",(_,_)=>{});b.SetPositiveButton("确认",(_,_)=>Safe(action));StyleDialog(b.Show());}
    void Prompt(string title,string initial,Action<string> submit)
    {var edit=new EditText(this){Text=initial};PgrTheme.StyleInput(edit);var holder=new LinearLayout(this){Orientation=Orientation.Vertical};holder.SetPadding(Dp(20),Dp(8),Dp(20),Dp(4));holder.AddView(edit);var b=new AlertDialog.Builder(this);b.SetTitle(title);b.SetView(holder);b.SetNegativeButton("取消",(_,_)=>{});b.SetPositiveButton("保存",(_,_)=>Safe(()=>submit(edit.Text??"")));StyleDialog(b.Show());}
    void Choose(string title,string[] items,Action<int> selected)
    {if(items.Length==0){Info(title,"当前没有可选内容。");return;}var b=new AlertDialog.Builder(this);b.SetTitle(title);b.SetItems(items,(_,e)=>Safe(()=>selected(e.Which)));b.SetNegativeButton("取消",(_,_)=>{});StyleDialog(b.Show());}
    void ShowPage(int index){if(index==5)session.Listening.OpenLast();page=index;linePage=0;Render();}
    void UpdateStatus()
    {
        if(!alive)return;
        RunOnUiThread(()=>
        {
            if(!alive)return;
            if(page==2&&!ReferenceEquals(renderedLocateCandidates,session.Candidates)){Render();return;}
            var e=session.Engine;string mode=session.AutoPlaybackRunning?"自动播放中":session.ClickFollowRunning?"点按跟随中":"手动播放";
            string meta=e?.Current is { } n?$"{mode}  /  {n.Speaker}":mode+"  /  当前台词";
            if(page==2)meta="播放器当前位置"+(e?.Current is { } located?" · "+located.Speaker:"");
            if(currentMeta.Text!=meta)currentMeta.Text=meta;
            string confirmLabel=page==2&&session.Candidates.Count>0?"采用识别结果":"确认当前句";
            if(confirmPositionButton.Text!=confirmLabel)confirmPositionButton.Text=confirmLabel;
            string text=e?.Current==null?"尚未选句":session.CurrentGameLineText;if(current.Text!=text)current.Text=text;
            string statusText=session.SettingsSaveWarning??session.Status;
            if(status.Text!=statusText)status.Text=statusText;
            currentMeta.SetTextColor(session.AutoPlaybackRunning||session.ClickFollowRunning?PgrTheme.Cyan:PgrTheme.Secondary);
            if(session.Listening.IsPlaying)
            {
                currentMeta.Text="正在听书 · 点击返回播放器";currentMeta.SetTextColor(PgrTheme.Cyan);
                current.Text=session.Listening.Current?.Text??session.Listening.Status;
                status.Text=session.SettingsSaveWarning??session.Listening.PositionText;
            }
            if(autoPlaybackState!=null)
            {
                autoPlaybackState.Text=session.AutoPlaybackRunning?"已开启 · 按当前路线推进":"未开启 · 请从游戏内悬浮控制开始";
                autoPlaybackState.SetTextColor(session.AutoPlaybackRunning?PgrTheme.Cyan:PgrTheme.Secondary);
            }
            if(autoPlaybackStopButton!=null)autoPlaybackStopButton.Enabled=session.AutoPlaybackRunning;
            if(autoPlaybackPermissionButton!=null)autoPlaybackPermissionButton.Text=GameAdvanceAccessibilityService.IsConnected?"自动点击权限 · 已连接":"开启自动点击权限";
            UpdateCaptureStatusButton();QueueCaptureStatusTick();
        });
    }
    void UpdateCaptureStatusButton()
    {
        if(captureStatusButton==null)return;
        string label=session.Screen.CaptureActive?"停止屏幕捕获":"授权屏幕捕获";
        if(captureStatusButton.Text!=label)captureStatusButton.Text=label;
    }
    void ResumeCaptureStatusUi(){captureStatusUiVisible=true;UpdateCaptureStatusButton();QueueCaptureStatusTick();}
    void PauseCaptureStatusUi(){captureStatusUiVisible=false;CancelCaptureStatusTick();}
    void CancelCaptureStatusTick()
    {
        if(captureStatusTick!=null)pageScroll?.RemoveCallbacks(captureStatusTick);
        captureStatusTickScheduled=false;
    }
    void QueueCaptureStatusTick()
    {
        if(!alive||!captureStatusUiVisible||page!=4||captureStatusButton==null||captureStatusTickScheduled)return;
        // Capture start/stop completes asynchronously; only observe state while this page is visible.
        captureStatusTick??=()=>{captureStatusTickScheduled=false;if(!alive||!captureStatusUiVisible||page!=4)return;UpdateCaptureStatusButton();QueueCaptureStatusTick();};
        captureStatusTickScheduled=true;pageScroll.PostDelayed(captureStatusTick,500);
    }
    void Render()
    {
        if(!alive)return;
        RunOnUiThread(()=>
        {
            if(!alive)return;CancelCaptureStatusTick();captureStatusButton=null;autoPlaybackState=null;autoPlaybackStopButton=null;autoPlaybackPermissionButton=null;ClearListeningViews();content.RemoveAllViews();
            chapterCategoryBar.Visibility=page==0?ViewStates.Visible:ViewStates.Gone;
            bool pageChanged=styledTab!=page;
            if(pageChanged)
            {
                styledTab=page;
                AdjustLayout();
            }
            switch(page){case 0:Chapters();break;case 1:Story();break;case 2:Locate();break;case 3:History();break;case 5:ListeningPage();break;case 6:BranchSettingsPage();break;default:SettingsPage();break;}
            if(pageChanged)pageScroll.Post(()=>{if(alive)pageScroll.ScrollTo(0,0);});
            UpdateStatus();
        });
    }
    void Chapters()
    {
        Button(content,"搜索已导入章节",ShowLibrarySearch);
        var packages=session.Packages.List();
        string categoryName=UpdateChapterCategory(packages);
        var visiblePackages=packages.Where(p=>ChapterCatalog.Category(p.PackId,p.Title)==categoryName).ToArray();
        if(visiblePackages.Length>0)ArchiveHero(packages.Count);
        ListeningEntrance();
        var heading=Row(content);heading.SetGravity(GravityFlags.CenterVertical);
        var label=Text("章节配音包",19);label.SetTypeface(Typeface.Default,TypefaceStyle.Bold);heading.AddView(label,new LinearLayout.LayoutParams(0,-2,1));
        var import=new Button(this){Text=importCancellation!=null?"取消导入":"导入 ZIP",TextSize=12};PgrTheme.StyleButton(import,quiet:true);ButtonIcon(import,"import");import.Click+=(_,_)=>Safe(()=>{if(importCancellation!=null)importCancellation.Cancel();else SelectFile(ImportRequest,"application/zip");});heading.AddView(import,new LinearLayout.LayoutParams(-2,Dp(48)));
        Line("可长按多选 ZIP，一次导入多个章节。",11);
        if(visiblePackages.Length==0)
        {
            var empty=Card(content);var caption=Text("暂无章节",18);caption.SetTypeface(Typeface.Default,TypefaceStyle.Bold);empty.AddView(caption);
            var help=Text("本分类还没有导入章节，可点“导入 ZIP”添加。",14);help.SetTextColor(PgrTheme.Secondary);empty.AddView(help);
        }
        foreach(var pack in visiblePackages)
        {
            bool selected=session.Engine?.Pack.Id==pack.PackId;
            var card=new LinearLayout(this){Orientation=Orientation.Horizontal,Clickable=true,Focusable=true};card.SetGravity(GravityFlags.CenterVertical);card.SetPadding(Dp(14),Dp(14),Dp(12),Dp(14));
            card.Background=PgrTheme.Surface(this,selected?PgrTheme.Raised:PgrTheme.Panel,PgrTheme.Border,selected?PgrTheme.Red:null);
            card.ContentDescription=(selected?"查看当前章：":"打开章节：")+PackTitle(pack.Title);card.Click+=(_,_)=>Safe(()=>{session.LoadPack(pack.PackId);ShowPage(1);});
            var number=Text(ChapterNumber(pack.Title),32);number.SetTypeface(Typeface.Create("sans-serif-condensed",TypefaceStyle.Bold),TypefaceStyle.Bold);number.SetTextColor(selected?PgrTheme.Foreground:PgrTheme.Secondary);number.SetPadding(0,0,0,0);card.AddView(number,new LinearLayout.LayoutParams(Dp(53),-2));
            var details=new LinearLayout(this){Orientation=Orientation.Vertical};
            var chapter=Text(ChapterTitle(pack.Title),17);chapter.SetTypeface(Typeface.Default,TypefaceStyle.Bold);chapter.SetPadding(0,0,0,Dp(5));details.AddView(chapter);
            var progress=Text(session.Progress.GetSummary(pack.PackId) is { Length:>0 } summary?summary:"尚未开始 · 点击查看剧情",11);progress.SetTextColor(PgrTheme.Secondary);progress.SetMaxLines(2);progress.Ellipsize=global::Android.Text.TextUtils.TruncateAt.End;progress.SetPadding(0,0,0,0);details.AddView(progress);
            card.AddView(details,new LinearLayout.LayoutParams(0,-2,1));card.AddView(Icon("chevron",selected?PgrTheme.Red:PgrTheme.Secondary,18),new LinearLayout.LayoutParams(Dp(22),Dp(28)));
            content.AddView(card,new LinearLayout.LayoutParams(-1,-2){TopMargin=Dp(5),BottomMargin=Dp(4)});
            var manage=new Button(this){Text="管理文件 · "+ChapterTitle(pack.Title),TextSize=12};
            manage.ContentDescription="管理章节文件："+PackTitle(pack.Title);PgrTheme.StyleButton(manage,quiet:true);
            manage.Click+=(_,_)=>Safe(()=>ManagePackage(pack.PackId));
            content.AddView(manage,new LinearLayout.LayoutParams(-1,Dp(42)){BottomMargin=Dp(10)});
        }
        Line("章节按顺序逐个导入，更新时保留已有进度。",11);
    }
    bool NeedPack(){if(session.Engine!=null)return false;Line("请先在“章节”中导入并打开配音包。");return true;}
    void SectionSelector()
    {
        var e=session.Engine!;var sections=e.Pack.Chapters.SelectMany(c=>c.Sections).ToList();
        var selected=sections.FirstOrDefault(s=>s.Id==session.SectionId)??sections[0];
        Button(content,"小节："+SectionDisplay.Title(sections,selected.Id),()=>ChooseDisplayedSection(e.Pack,sections,"选择游戏当前小节",
            ()=>ReferenceEquals(e,session.Engine),section=>
        {session.Command(x=>x.PauseForBrowse());session.SectionId=section.Id;linePage=0;Render();}));
    }
    void Controls()
    {
        var row=Row(content);ButtonIcon(Button(row,"上一句",()=>session.Command(e=>e.Previous(),true)),"previous");ButtonIcon(Button(row,"重播",()=>session.Command(e=>e.Replay(),true)),"replay",true);ButtonIcon(Button(row,"下一句",()=>session.Command(e=>e.Next(true),true)),"next");
        row=Row(content);ButtonIcon(Button(row,"暂停",()=>session.Command(e=>e.PauseForBrowse())),"pause");ButtonIcon(Button(row,"分支",ShowBranches),"branch");ButtonIcon(Button(row,"游戏原声",()=>session.Command(e=>e.EnterOriginal())),"original");
        if(session.Engine?.Mode==RunMode.Original)Line("原声时段：配音停止、位置冻结。点选当前游戏台词，确认续接后恢复。",14);
    }
    void Story()
    {
        if(NeedPack())return;
        Line("剧情 / 第 "+ChapterNumber(session.Engine!.Pack.Title)+" 章",11);Line(ChapterTitle(session.Engine.Pack.Title),24);Controls();SectionSelector();
        Button(content,"内置剧情文本 · 当前小节",ShowStoryReference);
        var action=Row(content);Button(action,"保存书签",AddBookmark);Button(action,"撤销纠偏",()=>session.Command(e=>{if(!e.UndoCorrection())Info("撤销纠偏",e.NavigationError);}));
        Button(content,"反馈这句",FeedbackCurrentGameLine);
        if(session.Engine.Notice.Length>0)Line(session.Engine.Notice,13);
        RenderLines(false);
    }
    string? renderedGapDirectoryKey;
    void RenderLines(bool searching)
    {
        var engine=session.Engine!;
        var lines=engine.Pack.Nodes.Where(n=>!n.Archived&&n.Kind=="line"&&n.SectionId==session.SectionId&&
            (!searching||string.IsNullOrWhiteSpace(search)||n.Text.Contains(search,StringComparison.OrdinalIgnoreCase)||n.Speaker.Contains(search,StringComparison.OrdinalIgnoreCase))).ToList();
        if(!searching && engine.Mode==RunMode.Gap)
        {
            string key=engine.Pack.Id+":"+session.SectionId+":"+engine.CurrentId;
            if(renderedGapDirectoryKey!=key)
            {
                var visibleIds=lines.Select(n=>n.Id).ToHashSet(StringComparer.Ordinal);
                var previous=engine.History.Take(engine.HistoryPosition+1).LastOrDefault(v=>visibleIds.Contains(v.NodeId));
                int nearby=previous==null?-1:lines.FindIndex(n=>n.Id==previous.NodeId);
                if(nearby>=0)linePage=nearby/45;
                renderedGapDirectoryKey=key;
            }
            Line("此处等待核对。下面仍是完整台词，可按游戏画面确认一句；列表顺序不代表已核实的下一句。",13);
        }
        else if(!searching)renderedGapDirectoryKey=null;
        const int size=45;int pages=Math.Max(1,(lines.Count+size-1)/size);linePage=Math.Clamp(linePage,0,pages-1);
        Line($"台词 · {lines.Count} 句 · 第 {linePage+1}/{pages} 页",14);
        foreach(var node in lines.Skip(linePage*size).Take(size))
        {
            string notice=engine.Pack.AudioNotice(node);
            bool selected=node.Id==engine.CurrentId;
            var line=new LinearLayout(this){Orientation=Orientation.Horizontal,Clickable=true,Focusable=true,Selected=selected};line.SetPadding(Dp(11),Dp(13),Dp(11),Dp(13));line.SetGravity(GravityFlags.Top);
            line.Background=PgrTheme.Surface(this,selected?PgrTheme.Selection:PgrTheme.Panel,selected?PgrTheme.Red:PgrTheme.Panel,selected?PgrTheme.Red:null);
            line.ContentDescription=node.Speaker+"："+node.Text;line.Click+=(_,_)=>Safe(()=>ConfirmNode(node));
            var marker=Icon(selected?"wave":"story",selected?PgrTheme.Red:PgrTheme.Secondary,18);line.AddView(marker,new LinearLayout.LayoutParams(Dp(22),Dp(24)){RightMargin=Dp(10),TopMargin=Dp(2)});
            var body=new LinearLayout(this){Orientation=Orientation.Vertical};
            var speaker=Text(string.IsNullOrWhiteSpace(node.Speaker)?"旁白":node.Speaker,11);speaker.SetTextColor(selected?PgrTheme.Red:PgrTheme.Secondary);speaker.SetPadding(0,0,0,Dp(5));speaker.SetTypeface(Typeface.Default,TypefaceStyle.Bold);body.AddView(speaker);
            var dialogue=Text(node.Text,15);dialogue.SetPadding(0,0,0,0);dialogue.SetLineSpacing(Dp(3),1);body.AddView(dialogue);
            if(notice.Length>0){var warning=Text(notice,11);warning.SetTextColor(PgrTheme.Secondary);body.AddView(warning);}
            line.AddView(body,new LinearLayout.LayoutParams(0,-2,1));content.AddView(line,new LinearLayout.LayoutParams(-1,-2){BottomMargin=Dp(5)});
        }
        var row=Row(content);if(linePage>0)Button(row,"上一页",()=>{linePage--;Render();});if(linePage+1<pages)Button(row,"下一页",()=>{linePage++;Render();});
    }
    void ConfirmFooterPosition()
    {
        if(page!=2||session.Candidates.Count==0){session.ConfirmCurrent();return;}
        var candidates=session.Candidates.ToArray();var engine=session.Engine;
        if(candidates.Length==1){ConfirmOcrCandidate(candidates[0],engine);return;}
        Choose("选择与游戏一致的识别候选",candidates.Select(c=>c+"\n"+c.Context).ToArray(),i=>ConfirmOcrCandidate(candidates[i],engine));
    }
    void ConfirmOcrCandidate(MatchCandidate candidate,PlaybackEngine? expectedEngine)
    {
        bool StillValid()
        {
            if(ReferenceEquals(session.Engine,expectedEngine)&&expectedEngine!=null&&
                session.Candidates.Any(c=>ReferenceEquals(c,candidate))&&
                expectedEngine.Pack.ById.TryGetValue(candidate.Node.Id,out var node)&&ReferenceEquals(node,candidate.Node))return true;
            Info("识别结果已失效","当前章节、模式或识别结果已改变。请重新识别，或搜索选择游戏中的台词。");Render();return false;
        }
        ConfirmNode(candidate.Node,StillValid);
    }
    void ConfirmNode(Node node,Func<bool>? canApply=null)
    {
        if(canApply!=null&&!canApply())return;
        if(node.Kind=="choice")
        {
            Confirm("定位游戏中的分支菜单",node.Text+"\n\n"+session.Engine!.LocationContext(node),()=>
            {if(canApply==null||canApply())session.Command(e=>{if(!e.OpenGameMenu(node.Id,node.SectionId))throw new InvalidOperationException(e.NavigationError);});});return;
        }
        bool original=session.Engine?.Mode==RunMode.Original;
        Confirm(original?"结束原声并从这里续接？":"确认游戏当前台词",node.Speaker+"："+node.Text+"\n\n"+session.Engine!.LocationContext(node)+"\n\n请确保与游戏当前画面一致。",()=>
        {if(canApply==null||canApply())session.ConfirmLine(node.Id,original);});
    }
    void ShowBranches()
    {
        if(session.Engine is not { } e)return;
        if(e.Mode==RunMode.Original){Info("原声时段","先在剧情或定位页选择续接台词。当前不会改变路线。");return;}
        if(e.Mode==RunMode.Choice)
        {
            var options=e.AvailableOptions.ToList();
            var labels=options.Select(o=>o.ToString()).Concat(new[]{"返回上级菜单","人物、话题与分支目录"}).ToArray();
            Choose("当前分支与导航",labels,i=>
            {
                if(i==options.Count)ReturnToParentMenu();
                else if(i==options.Count+1)ShowBranchDirectory();
                else Confirm("确认分支",options[i].Label+"\n"+options[i].Reason,()=>session.Command(x=>x.SelectBranch(i),true));
            });return;
        }
        if(e.Mode==RunMode.Gap&&e.ResumeMenus.Count>0)
        {
            var menus=e.ResumeMenus;
            var labels=menus.Select(m=>m.Text).Concat(new[]{"返回上级菜单","人物、话题与分支目录"}).ToArray();
            Choose("续接菜单与导航",labels,i=>
            {
                if(i==menus.Count)ReturnToParentMenu();
                else if(i==menus.Count+1)ShowBranchDirectory();
                else session.Command(x=>x.SelectContinuation(i));
            });return;
        }
        ShowBranchDirectory();
    }
    void ReturnToParentMenu() => session.Command(x=>{if(!x.ReturnToStoryMenu())Info("返回菜单",x.NavigationError);});
    void ShowBranchDirectory()
    {
        if(session.Engine is not { } e)return;
        if(e.Mode==RunMode.Original){Info("原声时段","先在剧情或定位页选择续接台词。当前不会改变路线。");return;}
        var targets=e.GetStoryMenus(session.SectionId);
        var labels=new List<string>{"返回上级菜单","重选最近一次分支"};labels.AddRange(targets.Select(t=>t.Label+" · "+t.Status));
        Choose("人物、话题与分支",labels.ToArray(),i=>
        {
            if(i==0)ReturnToParentMenu();
            else if(i==1)session.Command(x=>{if(!x.ReselectLastChoice())Info("重选分支",x.NavigationError);});
            else Confirm("定位游戏当前菜单",targets[i-2].Label+"\n"+targets[i-2].Preview,()=>session.Command(x=>{if(!x.OpenGameMenu(targets[i-2].MenuId,session.SectionId))Info("分支定位",x.NavigationError);}));
        });
    }
    void Locate()
    {
        renderedLocateCandidates=session.Candidates;
        if(NeedPack())return;
        Line("字幕识别",11);Line("定位当前台词",24);Line("识别后点“采用这句”或“采用识别结果”，核对台词和路线后确认。",13);
        var row=Row(content);ButtonIcon(Button(row,"授权屏幕捕获",RequestCapture),"locate");ButtonIcon(Button(row,"OCR 定位",RequestManualOcr),"locate",true);
        OcrScopeSelector();
        Button(content,"编辑框选区域（先切回游戏）",EditRegion);
        SectionSelector();
        var recognized=Card(content);recognized.Clickable=true;recognized.Focusable=true;
        recognized.ContentDescription="点击采用这段识别原文";
        recognized.Click+=(_,_)=>Safe(()=>
        {
            if(!session.PrepareRecognizedTextCandidates())
            {Info("无法选择对应台词",session.Status);return;}
            // 这里只允许采用原文检索所得的候选，不能退回确认旧的播放位置。
            if(page!=2||session.Candidates.Count==0)
            {Info("识别候选已变化","请再次点击识别原文，选择与游戏一致的台词。");return;}
            ConfirmFooterPosition();
        });
        var recognizedTitle=Text("上次识别原文",12);recognizedTitle.SetTextColor(PgrTheme.Cyan);recognized.AddView(recognizedTitle);
        recognized.AddView(Text(session.OcrText,15));
        var chooseRecognized=Text("点这里选择对应台词",13);chooseRecognized.SetTextColor(PgrTheme.Cyan);
        chooseRecognized.SetTypeface(Typeface.Default,TypefaceStyle.Bold);recognized.AddView(chooseRecognized);
        string resultState=session.Candidates.Count==0?"点原文可重新选择对应台词，核对内容和路线后再确认；实际播放位置见下方。":"识别原文不代表播放位置。请核对候选后采用；下方显示播放器当前位置。";
        var resultNotice=Text(resultState,12);resultNotice.SetTextColor(PgrTheme.Secondary);recognized.AddView(resultNotice);
        var candidateEngine=session.Engine;
        foreach(var candidate in session.Candidates)Button(content,"采用这句 · "+candidate+"\n"+candidate.Context,()=>ConfirmOcrCandidate(candidate,candidateEngine));
        var edit=new EditText(this){Hint="搜索台词或角色",Text=search};edit.SetSingleLine(true);PgrTheme.StyleInput(edit);
        content.AddView(edit,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent,ViewGroup.LayoutParams.WrapContent){TopMargin=Dp(12),BottomMargin=Dp(4)});
        Button(content,"搜索当前小节",()=>{search=edit.Text??"";linePage=0;Render();});
        Button(content,"跨章节搜索",ShowLibrarySearch);
        RenderLines(true);
    }
    void OcrScopeSelector()
    {
        Button(content,"定位范围："+(session.Settings.OcrFullScreen?"全画面":"框选区域"),()=>
            Choose("选择定位范围",new[]{"全画面：无需框选","框选区域：仅识别保存的字幕区域"},i=>
            {session.SetOcrFullScreen(i==0);Render();}));
        Line(session.Settings.OcrFullScreen
            ?"全画面无需框选，识别结果仍需核对后采用。编辑区域只会保存框选范围，不会切换当前设置。"
            :"仅识别框选区域，请将角色名和完整对白框入。识别结果仍需核对后采用。",13);
    }
    void History()
    {
        if(NeedPack())return;
        var e=session.Engine!;Line("剧情记录",11);Line("书签与履历",24);Line($"已记录 {e.History.Count} 条履历，随时回到熟悉的故事。",13);
        ButtonIcon(Button(content,"为当前位置保存书签",AddBookmark),"archive",true);
        foreach(var mark in session.Progress.Bookmarks(e.Pack.Id))
        {
            Button(content,"★ "+mark.Label+"\n"+mark.SectionTitle,()=>Choose("书签："+mark.Label,new[]{"恢复到这里（静音）","删除书签"},i=>
            {if(i==0)session.Command(x=>{if(!x.RestoreBookmark(mark))Info("恢复失败",x.NavigationError);});else Confirm("删除书签",mark.Label,()=>{session.Progress.RemoveBookmark(e.Pack.Id,mark.Id);Render();});}));
        }
        Line("最近分支",17);
        foreach(var choice in e.RecentChoices.Take(15))Button(content,choice.Label,()=>Confirm("重新选择分支",choice.Label,()=>session.Command(x=>{if(!x.ReselectChoice(choice.Sequence))Info("重选失败",x.NavigationError);})));
        Line("台词履历",17);
        int count=e.History.Count;historyPage=Math.Clamp(historyPage,0,Math.Max(0,(count-1)/40));
        foreach(int index in Enumerable.Range(0,count).Reverse().Skip(historyPage*40).Take(40))
        {
            var visit=e.History[index];var node=e.Pack.ById[visit.NodeId];
            Button(content,$"{index+1}. {node.Speaker}：{node.Text}",()=>Choose("台词履历",new[]{"试听（不改变进度）","恢复到这里（静音）","反馈这句"},i=>
            {if(i==0)session.PreviewNode(node);else if(i==1)session.Command(x=>{if(!x.RestoreVisit(index,false))Info("恢复失败",x.NavigationError);});else BeginLineFeedback(e.Pack,node,"游戏历史",RecordedFeedbackContext(e,index));}));
        }
        var row=Row(content);if(historyPage>0)Button(row,"较新记录",()=>{historyPage--;Render();});if((historyPage+1)*40<count)Button(row,"更早记录",()=>{historyPage++;Render();});
    }
    void AddBookmark()
    {if(session.Engine?.Current==null){Info("保存书签","请先选择当前台词。");return;}Prompt("书签名称",session.Engine.Current.Speaker, label=>{var e=session.Engine!;session.Progress.SaveBookmark(e.Pack.Id,e.CreateBookmark(label));Render();});}
    void SettingsPage()
    {
        Line("控制台",11);Line("播放与识别",24);
        Button(content,"重新打开首次使用引导",StartOnboarding);
        Line("无需绑定客户端或选择服务器。回到你正在玩的战双，使用悬浮控制即可。",13);
        Line("悬浮控制",17);var overlayControls=Row(content);
        ButtonIcon(Button(overlayControls,"开启悬浮控制",ShowOverlay),"play");
        ButtonIcon(Button(overlayControls,"关闭悬浮控制",session.HideOverlay),"close");
        var compactButtons=new Switch(this){Text="收起时显示小按钮",Checked=session.Settings.ShowCompactButtons,TextSize=14};
        compactButtons.SetTextColor(PgrTheme.Foreground);compactButtons.SetMinimumHeight(Dp(48));
        compactButtons.SetPadding(Dp(12),Dp(8),Dp(12),Dp(8));compactButtons.Background=PgrTheme.Surface(this,PgrTheme.Panel);
        compactButtons.CheckedChange+=(_,e)=>session.SetCompactButtonsVisible(e.IsChecked);
        content.AddView(compactButtons,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent,ViewGroup.LayoutParams.WrapContent){TopMargin=Dp(6),BottomMargin=Dp(3)});
        var compactControls=new Switch(this){Text="显示台词悬浮条（关闭后只留球）",Checked=session.Settings.ShowCompactControls,TextSize=14};
        compactControls.SetTextColor(PgrTheme.Foreground);compactControls.SetMinimumHeight(Dp(48));
        compactControls.SetPadding(Dp(12),Dp(8),Dp(12),Dp(8));compactControls.Background=PgrTheme.Surface(this,PgrTheme.Panel);
        compactControls.CheckedChange+=(_,e)=>session.SetCompactControlsVisible(e.IsChecked);
        content.AddView(compactControls,new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent,ViewGroup.LayoutParams.WrapContent){TopMargin=Dp(3),BottomMargin=Dp(6)});
        Line("点按跟随",17);
        var tapCard=Card(content);
        tapCard.AddView(Text("普通台词不依赖 OCR。先把配音对齐游戏当前句，再到游戏里设置“下一句区域”，开启“点按跟随”。每次你轻点框内，游戏和配音各前进一步。",13));
        tapCard.AddView(Text("等游戏文字完整后再轻点一次，等下一句出现，不要快速连点。补齐文字或转发时连续点击可能错位，可用上一句／下一句小按钮校正。人物互动和未确认的路线仍需手动选择。",13));
        Button(tapCard,"分支线设置 · "+(session.Settings.AutoPlayConfirmBranch?"选择后确认对白":session.Settings.AutoPlayDefaultBranchEnabled?"自动播放默认分支"+(session.Settings.DefaultBranchOption==2?"二":"一"):session.Settings.BranchAutoFollow?"分支配音跟随":"分支期间暂停配音"),()=>ShowPage(6));
        tapCard.AddView(Text("分支、人物互动和回访话题的处理方式，统一在下方“分支”页设置。",13));
        Button(tapCard,"点按跟随的辅助点击权限",ExplainAutoPlaybackAccessibility);
        Button(tapCard,"回到游戏设置点按跟随",()=>ReturnToGameForAutoPlayback(true));
        Line("自动播放",17);
        var autoCard=Card(content);
        autoPlaybackState=Text("未开启 · 请从游戏内悬浮控制开始",14);autoCard.AddView(autoPlaybackState);
        var autoDescription=Text("先核对当前句，点“从这句开始”直接重播，不重复 OCR。需要纠偏时选“重新 OCR 定位”，人工采用候选后再开启。之后按已确认的路线顺序，每句自然播完才点击一次游戏下一句。",13);
        autoDescription.SetTextColor(PgrTheme.Secondary);autoCard.AddView(autoDescription);
        var autoBoundary=Text("开启“确认选后的对白，再继续播放”时，遇到选项才暂停；先在游戏选择，再点原选项与选后对白卡片，直接继续自动播放，汇合时不再确认。关闭该模式、且未开启默认分支时，到分支会暂停并在顶部提示。先在游戏中选择，再展开通知确认相同台词或选项，即可开启点按跟随，不需回控制页重复开启。支线期间由你轻点下一句区域推进；回到已核实共同线后，按通知核对台词并确认恢复自动播放。也可在“分支”页选择支线期间暂停配音；未知连接仍暂停。",13);
        autoBoundary.SetTextColor(PgrTheme.Secondary);autoCard.AddView(autoBoundary);
        autoPlaybackPermissionButton=Button(autoCard,"开启自动点击权限",ExplainAutoPlaybackAccessibility);ButtonIcon(autoPlaybackPermissionButton,"settings");
        ButtonIcon(Button(autoCard,"回到游戏配置 / 开始",ReturnToGameForAutoPlayback),"play",true);
        autoPlaybackStopButton=Button(autoCard,"停止自动播放",()=>session.StopAutoPlayback());ButtonIcon(autoPlaybackStopButton,"pause");
        var autoHelp=Text("保持屏幕捕获授权有效，在所选游戏前台设置“下一句区域”，再点“自动播放”核对起点。取消核对不会播放或点击。切出游戏、锁屏或换屏后，重新核对位置；自动播放中上下句和重播继续沿用当前模式；需要时单独 OCR 定位。普通手动播放和 OCR 定位不需要无障碍权限。",13);
        autoHelp.SetTextColor(PgrTheme.Secondary);autoCard.AddView(autoHelp);
        Line("识别与声音",17);
        Line("OCR 用于主动定位；分支辅助由你确认选项，不靠 OCR 猜测分支，普通点按不逐句识别。感觉发热时可减少定位操作、使用台词列表选句，也可保持不变。充电、游戏等都可能发热，请自行判断；软件不会按温度弹窗或自动调整。",13);
        Button(content,"声音："+(session.Settings.VoicePriority?"配音优先":"同时播放"),()=>Choose("声音策略",new[]{"同时播放：优先保留游戏音乐与音效","配音优先：请求游戏临时降低音量"},i=>
        {session.Settings.VoicePriority=i==1;session.ApplyAudioSettings();Render();}));
        Line("不同手机与游戏的音频焦点行为有差异，请实际试听；此设置不会修改游戏内音量。",13);
        Line("配音音量",15);var volume=new SeekBar(this){Max=100,Progress=(int)(session.Settings.Volume*100)};volume.SetMinimumHeight(Dp(48));volume.ProgressChanged+=(_,e)=>{if(e.FromUser){session.Settings.Volume=e.Progress/100f;session.ApplyAudioSettings();}};content.AddView(volume);
        Button(content,"角色音量",ShowSpeakerVolumes);
        Line(session.Engine?.Pack.FixedVoiceStatus ?? "角色固定声线：等待配音包，未开启",13);
        Button(content,"识别引擎："+(session.Settings.OcrEngine==OcrEngineKind.Paddle?"PP-OCRv5":"ML Kit 中文（对照）"),()=>Choose("本地识别引擎",new[]{"PP-OCRv5 mobile（默认）","ML Kit 中文（测试对照）"},i=>
        {session.ChangeOcrEngine(i==0?OcrEngineKind.Paddle:OcrEngineKind.MlKitChinese);Render();}));
        captureStatusButton=Button(content,session.Screen.CaptureActive?"停止屏幕捕获":"授权屏幕捕获",()=>{if(session.Screen.CaptureActive)session.Screen.Stop();else RequestCapture();UpdateCaptureStatusButton();});
        OcrScopeSelector();
        Button(content,"编辑框选区域",EditRegion);
        Line("本地存档与诊断",17);
        Button(content,"导出当前章节存档",()=>
        {if(session.Engine==null)return;exportText=System.Text.Json.JsonSerializer.Serialize(session.Engine.ExportNavigation(),Json.Options);SaveFile("剧情存档.json");});
        Button(content,"恢复当前章节存档",()=>{if(session.Engine!=null)SelectFile(RestoreRequest,"application/json");});
        Button(content,"导出本次诊断",()=>{exportText=session.Diagnostics.Export();SaveFile("配音诊断.json");});
        Line(session.Diagnostics.Summary,13);
        Button(content,"停止配音和识别",()=>{session.Stop();VoiceForegroundService.StopAll(this);});
        Button(content,"使用说明与配置范围",()=>Info("使用与测试范围","Android 10 及以上，64 位。\n优化目标：骁龙 888 + 8 GB；推荐目标：8 Gen 2 / 8 Gen 3 + 12 GB。\n\n这些是开发目标，不能代表已实测最低配置。模拟器无法验证游戏同开性能、混音和发热。X Fold5 与骁龙 888 的完整测试结果请以交付说明为准。\n\nOCR 定位：默认识别全画面，无需框选；也可在定位页或设置页切换为框选区域。核对候选后选择开始台词，不会持续识别每句。识别不准时，可以直接从台词列表选句。\n\n点按跟随：需要辅助点击权限。确认当前句并设置触碰区域后，每次轻点框内，游戏和配音各推进一句。可在独立“分支”页选择“分支配音跟随”，或选择“分支期间暂停配音”，由你自己玩到共同线再核对续播。先设置一次下一句区域；普通二选一会在顶部通知，先在游戏里选好，再展开通知并点与画面相同的台词，从选中的句子开始播放，使用已保存区域跟随。也可切到“按选项”核对同名项。长句可点“全文”或长按核对；收起保留等待，取消才退出本次跟随。通知外仍可操作游戏；已核实的3D人物与话题也使用顶部通知，多段对话由你对照游戏选择，聊完返回菜单后再次通知，已聊人物可再次选择。\n\n自动播放：需屏幕捕获授权、辅助点击权限、游戏在前台及有效下一句区域。先核对当前句，点“从这句开始”直接重播，不重复 OCR；需要时选“重新 OCR 定位”，人工采用候选后再开启。取消核对不会播放或点击。之后按已确认路线顺序，配音自然播完才点下一句。默认使用“确认选后的对白，再继续播放”：遇到已知选项暂停，你先在游戏里选择，再展开顶部通知，点对应原选项与选后对白卡片，从这句直接续播，不补读被跳过的选项或台词；嵌套汇合或回到共同剧情都不再额外确认，下一处选项才再次提示。软件不识别你是否选错，选错可从悬浮分支页重选最近一次选项或重新定位。关闭选后对白确认及默认分支自动播放、且选择分支配音跟随时，普通二选一先停自动点击并显示顶部通知；游戏里选好后展开，按实际出现的台词确认，或切到“按选项”核对同名项，确认这一次就会使用已保存区域开启点按跟随，不必再回悬浮控制重复开启。顶部会明确提示分支期间使用点按跟随；等字幕完整后，你每轻点一次下一句区域，游戏和配音各推进一句。回到已核实共同线时通知具体台词，展开核对一致后恢复自动播放。两种模式交替开启，不会同时点击。暂停分支模式会在已核实汇合时提供共同线台词供你核对；未知连接仍停下。上述常规处理用于未开启默认分支的自动播放。开启“自动播放时使用默认分支”后，软件直接按默认路线配音；请你在游戏点同一项。顶部显示“进入分支一／二：选项内容”，约1.5秒后消失，不等待识别或再次手动续接。自动播放期间用悬浮上一句、当前句、下一句调整配音后会继续自动播放，不重复确认起点；调整当下不点击游戏。已开启默认分支时，在已确认的支线内暂停后也可核对当前句重新开启，继续当前路线，默认项仅用于下一处选择。悬浮标识始终按当前路线显示；内层汇合仍保留父分支标识。切出游戏、换屏或未知连接仍暂停核对。\n\n内外屏与横竖屏分别保存框选区域。使用框选定位时，请将角色名和对白完整框入，尽量避开动态背景。编辑框选区域不会切换定位范围；游戏的下一句触碰区域请在游戏前台另行设置。\n\n无账号、无云端 OCR，无需 root。普通播放和 OCR 定位都不需要无障碍权限。"));
    }
    void ExplainAutoPlaybackAccessibility()
    {
        var dialog=new AlertDialog.Builder(this);dialog.SetTitle("开启自动点击权限");
        dialog.SetMessage("可选的“点按跟随”和“自动播放”需要辅助点击权限。点按跟随只在你轻点指定区域后，同坐标点击游戏一次；自动播放则在配音结束后点击。\n\n普通小按钮播放和 OCR 定位不需要。系统设置中请开启“剧情自动下一句”，再回到游戏配置并手动开始。仅开启权限不会播放或点击，也不读取游戏台词。");
        dialog.SetNegativeButton("暂不开启",(_,_)=>{});
        dialog.SetPositiveButton("前往系统设置",(_,_)=>Safe(()=>StartActivity(new Intent(global::Android.Provider.Settings.ActionAccessibilitySettings))));
        StyleDialog(dialog.Show());
    }
    void ReturnToGameForAutoPlayback()=>ReturnToGameForAutoPlayback(false);
    void ReturnToGameForAutoPlayback(bool clickFollow)
    {
        ShowOverlay();
        if(!global::Android.Provider.Settings.CanDrawOverlays(this))return;
        if(ReturnToGame())Toast.MakeText(this,"回到游戏后，展开悬浮控制，设置下一句区域并点“"+(clickFollow?"点按跟随":"自动播放")+"”。",ToastLength.Long)?.Show();
    }
    bool ReturnToGame()
    {
        // 只退回此前的任务，不查找、启动或绑定某个游戏客户端。
        if(MoveTaskToBack(true))return true;
        Toast.MakeText(this,"请从最近任务切回游戏，再使用悬浮控制。",ToastLength.Long)?.Show();
        return false;
    }
    void ShowOverlay()
    {
        if(!global::Android.Provider.Settings.CanDrawOverlays(this))
        {StartActivityForResult(new Intent(global::Android.Provider.Settings.ActionManageOverlayPermission,global::Android.Net.Uri.Parse("package:"+PackageName)),OverlayRequest);return;}
        RequestNotifications();session.ShowOverlay();
    }
    void RequestNotifications()
    {if(Build.VERSION.SdkInt>=BuildVersionCodes.Tiramisu&&CheckSelfPermission("android.permission.POST_NOTIFICATIONS")!=Permission.Granted)RequestPermissions(new[]{"android.permission.POST_NOTIFICATIONS"},15);}
    void RequestCapture()
    {
        if(!alive||captureRequestPending)return;
        session.PrepareCapture();RequestNotifications();
        var manager=(MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        captureRequestPending=true;
        try { StartActivityForResult(manager.CreateScreenCaptureIntent(),CaptureRequest); }
        catch { captureRequestPending=false;returnToGameAfterCapture=false;throw; }
    }
    void RequestManualOcr()
    {
        if(!session.Screen.CaptureActive){RequestCapture();return;}
        if(!ReturnToGame())return;
        if(global::Android.Provider.Settings.CanDrawOverlays(this))
        {session.ShowOverlay();session.RequestOcr(true);}
        else session.RequestOcr();
    }
    void EditRegion()
    {
        subtitleRegionOverlay?.Dispose();subtitleRegionOverlay=null;
        if(!session.Screen.CaptureActive){RequestCapture();return;}
        if(!ReturnToGame())return;
        session.RequestPreview((bitmap,selection)=>
        {
            if(!alive){bitmap.Dispose();return;}
            if(global::Android.Provider.Settings.CanDrawOverlays(this))
            {
                subtitleRegionOverlay?.Dispose();
                var picker=new SubtitleRegionOverlay(this,bitmap,session.Settings.RegionFor(selection.Display.RegionKey));
                subtitleRegionOverlay=picker;
                picker.Closed+=()=>{if(ReferenceEquals(subtitleRegionOverlay,picker))subtitleRegionOverlay=null;};
                picker.Saved+=region=>
                {
                    if(!alive)return;
                    try
                    {
                        session.ShowOverlay();
                        session.SetRegion(selection,region);
                        Toast.MakeText(this,session.Status,ToastLength.Long)?.Show();
                    }
                    catch(Exception ex)
                    {
                        session.Diagnostics.Log("字幕区域保存",ex.Message);
                        Toast.MakeText(this,"字幕区域未保存："+ex.Message,ToastLength.Long)?.Show();
                    }
                };
                try { picker.Show(); }
                catch(Exception ex)
                {
                    picker.Dispose();
                    session.Diagnostics.Log("字幕区域悬浮窗口",ex.Message);
                    Toast.MakeText(this,"无法显示字幕框选窗口，请检查悬浮权限后重试。",ToastLength.Long)?.Show();
                }
                return;
            }
            StartActivity(new Intent(this,typeof(MainActivity)).AddFlags(ActivityFlags.NewTask|ActivityFlags.SingleTop));
            var editor=new RegionEditor(this,bitmap,session.Settings.RegionFor(selection.Display.RegionKey));
            editor.LayoutParameters=new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent,(int)(Resources!.DisplayMetrics!.HeightPixels*.60));
            var builder=new AlertDialog.Builder(this);builder.SetTitle("框选角色名和完整对白，避开 NEXT 和波形");builder.SetView(editor);
            builder.SetNegativeButton("取消",(_,_)=>{});builder.SetPositiveButton("保存",(_,_)=>session.SetRegion(selection,editor.Region));
            var dialog=builder.Create();
            dialog!.DismissEvent+=(_,_)=>{editor.Dispose();bitmap.Dispose();};dialog.Show();StyleDialog(dialog);
        });
    }
    void SelectFile(int request,string mime)
    {
        if(request==ImportRequest&&(deletingArchive||importCancellation!=null||session.PackagesBusy)){Info("请稍候","章节文件正在处理中，请等待完成。");return;}
        var intent=new Intent(Intent.ActionOpenDocument);intent.AddCategory(Intent.CategoryOpenable);intent.SetType("*/*");
        if(request==ImportRequest)
        {
            intent.AddFlags(ActivityFlags.GrantReadUriPermission|ActivityFlags.GrantWriteUriPermission|ActivityFlags.GrantPersistableUriPermission);
            intent.PutExtra(Intent.ExtraAllowMultiple,true);
        }
        intent.PutExtra(Intent.ExtraMimeTypes,new[]{mime,"application/octet-stream","application/x-zip-compressed"});StartActivityForResult(intent,request);
    }
    void SaveFile(string name)
    {var intent=new Intent(Intent.ActionCreateDocument);intent.AddCategory(Intent.CategoryOpenable);intent.SetType("application/json");intent.PutExtra(Intent.ExtraTitle,name);StartActivityForResult(intent,ExportRequest);}
    protected override async void OnActivityResult(int requestCode,Result resultCode,Intent? data)
    {
        base.OnActivityResult(requestCode,resultCode,data);
        global::Android.Util.Log.Info("PgrVoice",$"ActivityResult request={requestCode} result={(int)resultCode} hasData={data!=null} hasUri={data?.Data!=null} flags={(data is null?0:(int)data.Flags)} authority={data?.Data?.Authority??""}");
        try
        {
            if(await HandleOnboardingResult(requestCode,resultCode,data))return;
            if(requestCode==CaptureRequest)
            {
                captureRequestPending=false;
                bool returnToGame=returnToGameAfterCapture;returnToGameAfterCapture=false;
                if(resultCode==Result.Ok&&data!=null)
                {
                    session.Screen.StartCapture(resultCode,data);
                    if(returnToGame)
                    {
                        session.ShowOverlay();
                        if(ReturnToGame())Toast.MakeText(this,"已授权屏幕捕获。等游戏画面稳定后，点悬浮控制的“识别定位”。",ToastLength.Long)?.Show();
                    }
                }
                else session.CaptureDenied();
                return;
            }
            if(requestCode==OverlayRequest){if(global::Android.Provider.Settings.CanDrawOverlays(this))session.ShowOverlay();return;}
            if(resultCode!=Result.Ok||data==null)return;
            if(requestCode==ImportRequest)
            {
                await ImportSelectedZipsAsync(data);
                return;
            }
            if(data.Data==null)return;
            if(requestCode==ExportRequest)
            {
                if(string.IsNullOrEmpty(exportText))throw new InvalidOperationException("导出已中断，请回到设置页重新选择要导出的内容。");
                using(var output=ContentResolver!.OpenOutputStream(data.Data,"wt")??throw new IOException("无法打开所选保存位置，请选择其他位置后重试。"))
                using(var writer=new StreamWriter(output))
                {await writer.WriteAsync(exportText);await writer.FlushAsync();}
                Toast.MakeText(this,"已导出",ToastLength.Short)?.Show();
            }
            else if(requestCode==RestoreRequest)
            {
                using var input=ContentResolver!.OpenInputStream(data.Data)!;
                var snapshot=await System.Text.Json.JsonSerializer.DeserializeAsync<NavigationSnapshot>(input,Json.Options)??throw new InvalidDataException("存档为空。");
                Confirm("恢复剧情存档","只恢复配音位置，游戏画面不会改变。",()=>session.Command(e=>{if(!e.ImportNavigation(snapshot))throw new InvalidDataException(e.NavigationError);}));
            }
        }
        catch(Exception ex) when(requestCode==ExportRequest&&(ex is Java.Lang.SecurityException||ex is UnauthorizedAccessException))
        {
            Info("未能导出","所选保存位置没有授予写入权限，文件尚未保存成功。\n\n请重新导出，在文件选择器左侧选择“内部存储”（有时显示设备名称），进入 Download 或 Documents 文件夹后保存。\n\n不要把刚才可能生成的空文件当作备份。");
        }
        catch(Exception ex){Info("操作未完成",ex.Message);}
    }
    sealed class SafeInsets(MainActivity owner) : Java.Lang.Object,View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View? view,WindowInsets? insets)
        {
            if(view==null||insets==null)return insets!;
            int left,top,right,bottom;
            if(OperatingSystem.IsAndroidVersionAtLeast(30))
            {var bars=insets.GetInsets(WindowInsets.Type.SystemBars()|WindowInsets.Type.DisplayCutout());left=bars.Left;top=bars.Top;right=bars.Right;bottom=bars.Bottom;}
            else
            {
#pragma warning disable CA1422
                left=insets.SystemWindowInsetLeft;top=insets.SystemWindowInsetTop;right=insets.SystemWindowInsetRight;bottom=insets.SystemWindowInsetBottom;
#pragma warning restore CA1422
            }
            int horizontal=owner.Dp(owner.CompactLayout?12:16),vertical=owner.Dp(owner.CompactLayout?4:10);
            view.SetPadding(left+horizontal,top+vertical,right+horizontal,bottom+vertical);
            return insets;
        }
    }
}
