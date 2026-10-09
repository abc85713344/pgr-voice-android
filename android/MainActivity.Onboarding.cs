using Android.App;
using Android.Content;
using AudioManager = Android.Media.AudioManager;
using Android.OS;
using Android.Views;
using Android.Widget;
using PgrVoice.AndroidApp.Platform;
using PgrVoice.AndroidApp.Ui;
using OperationCanceledException = System.OperationCanceledException;

namespace PgrVoice.AndroidApp;

public sealed partial class MainActivity
{
    bool onboardingActive;
    AlertDialog? onboardingDialog;
    OnboardingPreview? onboardingPreview;
    OnboardingSelection? onboardingSelection;
    CancellationTokenSource? onboardingImportCancellation;
    TextView? onboardingTrialMessage;
    Button? onboardingHeard;
    string onboardingNotice="";
    OnboardingProgress Guide=>session.Onboarding.Progress;

    void RestoreOrStartOnboarding(Bundle? saved)
    {
        if(session.Onboarding.OfferAutomatically){StartOnboarding();return;}
        if(Guide.Status==OnboardingStatus.InProgress&&
            (saved?.GetBoolean("onboarding_active",false)==true||Guide.PendingExternal!=null))
        { onboardingActive=true;session.PauseForOnboarding();RenderOnboarding(); }
    }
    void StartOnboarding()
    {
        if(session.PackagesBusy||importCancellation!=null){Info("请稍候","请等章节文件处理完成后再打开引导。");return;}
        StopOnboardingPreview();session.PauseForOnboarding();session.Onboarding.Begin();
        session.Settings.OnboardingShown=true;session.SaveSettings();
        onboardingActive=true;onboardingNotice="";RenderOnboarding();
    }
    void ResumeOnboarding()
    { if(onboardingActive)UpdateOnboardingTrial(); }
    void PauseOnboarding()
    { onboardingPreview?.Stop();UpdateOnboardingTrial();if(onboardingActive)session.Onboarding.Save(); }
    void StopOnboardingPreview()
    {
        if(onboardingPreview!=null){onboardingPreview.Changed-=UpdateOnboardingTrial;onboardingPreview.Dispose();onboardingPreview=null;}
        onboardingTrialMessage=null;onboardingHeard=null;
    }
    void DestroyOnboarding()
    {
        StopOnboardingPreview();onboardingImportCancellation?.Cancel();
        onboardingDialog?.Dismiss();onboardingDialog=null;
    }
    void CloseOnboarding(bool skip)
    {
        onboardingActive=false;session.Onboarding.Exit(skip);onboardingImportCancellation?.Cancel();
        StopOnboardingPreview();onboardingDialog?.Dismiss();onboardingDialog=null;
        // 暂停状态保留；关闭教学不恢复任何之前的播放或自动点击。
    }
    void RenderOnboarding()
    {
        if(!alive||!onboardingActive)return;
        StopOnboardingPreview();onboardingDialog?.Dismiss();onboardingDialog=null;
        var outer=new LinearLayout(this){Orientation=Orientation.Vertical};outer.SetPadding(Dp(20),Dp(20),Dp(20),Dp(16));
        outer.SetBackgroundColor(PgrTheme.Background);
        outer.AddView(Text($"首次使用 · 第 {(int)Guide.Step+1}/4 步",13));
        var scroll=new ScrollView(this){FillViewport=true};
        var box=new LinearLayout(this){Orientation=Orientation.Vertical};scroll.AddView(box);
        outer.AddView(scroll,new LinearLayout.LayoutParams(-1,0,1));
        if(onboardingNotice.Length>0)box.AddView(Text(onboardingNotice,13));
        if(onboardingImportCancellation!=null)
        {
            box.AddView(Text("正在导入章节",24));
            box.AddView(Text("文件较大时需要等一会儿。导入完成后会回到选章与试听。",15));
            Button(box,"取消本次导入",()=>{onboardingImportCancellation?.Cancel();});
        }
        else switch(Guide.Step)
        {
            case OnboardingStep.Purpose:
                box.AddView(Text("你准备怎样听剧情？",24));
                box.AddView(Text("先选用途，再选一章试听。全程不会自动开始连续播放。",15));
                Button(box,"听剧情",()=>{Guide.SelectPurpose(OnboardingPurpose.Listening);SaveAndRenderOnboarding();});
                box.AddView(Text("打开听书，独立保存收听位置，无需打开游戏。",13));
                Button(box,"边玩边听",()=>{Guide.SelectPurpose(OnboardingPurpose.Game);SaveAndRenderOnboarding();});
                box.AddView(Text("先试听，再按需开启悬浮控制，回到正在玩的游戏。",13));
                break;
            case OnboardingStep.Package:BuildOnboardingPackages(box);break;
            case OnboardingStep.Trial:BuildOnboardingTrial(box);break;
            case OnboardingStep.Finish:BuildOnboardingFinish(box);break;
        }
        var navigation=Row(outer);
        if(onboardingImportCancellation==null&&Guide.Step!=OnboardingStep.Purpose)Button(navigation,"上一步",()=>{Guide.Back();SaveAndRenderOnboarding();});
        Button(navigation,"暂时跳过",()=>CloseOnboarding(true));Button(navigation,"关闭引导",()=>CloseOnboarding(false));
        outer.AddView(Text("关闭后可在“设置 → 重新打开首次使用引导”继续。",12));
        onboardingDialog=new AlertDialog.Builder(this).SetView(outer)!.SetCancelable(true)!.Create();
        onboardingDialog!.CancelEvent+=(_,_)=>CloseOnboarding(false);
        onboardingDialog.Show();StyleDialog(onboardingDialog);
        onboardingDialog.Window?.SetLayout(ViewGroup.LayoutParams.MatchParent,ViewGroup.LayoutParams.MatchParent);
    }
    void SaveAndRenderOnboarding()
    { session.Onboarding.Save();onboardingNotice="";RenderOnboarding(); }
    void BuildOnboardingPackages(LinearLayout box)
    {
        box.AddView(Text("选一章配音",24));
        box.AddView(Text("已有章节可以直接选择。也可从手机文件中选 ZIP，软件会完成导入，不用先解压。",15));
        Button(box,"从手机导入章节 ZIP",BeginOnboardingImport);
        var packages=session.Packages.List();
        if(packages.Count==0)box.AddView(Text("还没有导入章节。请准备一章配音 ZIP，或暂时跳过后再导入。",14));
        foreach(var package in packages)
            Button(box,PackTitle(package.Title),()=>ChooseOnboardingChapter(package.PackId));
    }
    void ChooseOnboardingChapter(string packId)
    {
        Safe(()=>
        {
            var pack=session.Packages.Load(packId);
            if(pack.Chapters.Count==1){SelectOnboardingSample(pack,pack.Chapters[0]);return;}
            Choose("选择要试听的章节",pack.Chapters.Select(c=>PackTitle(c.Title)).ToArray(),i=>SelectOnboardingSample(pack,pack.Chapters[i]));
        });
    }
    void SelectOnboardingSample(Pack pack,Chapter chapter,string? after=null)
    {
        var sections=chapter.Sections.Select(s=>s.Id).ToHashSet();
        var lines=pack.Nodes.Where(n=>n.Kind=="line"&&!n.Archived&&sections.Contains(n.SectionId)).ToList();
        var playable=lines.Where(n=>pack.ResolveAudio(n) is { } path&&File.Exists(path)).ToList();
        var choices=playable.Count>0?playable:lines;
        if(choices.Count==0){onboardingNotice="本章没有可试听的台词，请换一章。";RenderOnboarding();return;}
        int index=after==null?0:(choices.FindIndex(n=>n.Id==after)+1)%choices.Count;
        Guide.SelectLine(pack.Id,chapter.Id,choices[index].Id);SaveAndRenderOnboarding();
    }
    bool OnboardingMediaAudible()
    {
        var manager=(AudioManager)GetSystemService(AudioService)!;
        return manager.GetStreamVolume(global::Android.Media.Stream.Music)>0;
    }
    float OnboardingVolume()=>SpeakerVolume.Apply(session.Settings.Volume,session.Settings.SpeakerVolumes,onboardingSelection?.Node.Speaker);
    void BuildOnboardingTrial(LinearLayout box)
    {
        box.AddView(Text("试听这一句",24));
        try{onboardingSelection=session.SelectedOnboardingLine();}
        catch(Exception ex){onboardingSelection=null;box.AddView(Text("章节暂不可用："+ex.Message,14));}
        if(onboardingSelection is not { } selection)
        {
            box.AddView(Text("所选章节或台词已经变化，请重新选择。",14));
            Button(box,"重新选章",()=>{Guide.Step=OnboardingStep.Package;SaveAndRenderOnboarding();});return;
        }
        box.AddView(Text(PackTitle(selection.Chapter.Title),16));
        box.AddView(Text(selection.Node.Speaker+"："+selection.Node.Text,18));
        box.AddView(Text("只播放画面上的一句，播完就停；不会改动游戏配音或听书位置。",13));
        onboardingPreview=new(new AndroidAudioPlayer(this),OnboardingVolume,OnboardingMediaAudible);
        onboardingPreview.Changed+=UpdateOnboardingTrial;
        onboardingTrialMessage=Text(onboardingPreview.Message,14);box.AddView(onboardingTrialMessage);
        Button(box,"试听这一句",()=>onboardingPreview?.Start(selection.AudioPath));
        Button(box,"换一句试听",()=>SelectOnboardingSample(selection.Pack,selection.Chapter,selection.Node.Id));
        onboardingHeard=Button(box,"我听到了",()=>
        {if(Guide.ConfirmHeard(onboardingPreview?.CanAcknowledge==true))SaveAndRenderOnboarding();});
        onboardingHeard.Enabled=false;
        Button(box,"没有声音",()=>
        {
            onboardingPreview?.Stop();UpdateOnboardingTrial();
            if(onboardingTrialMessage!=null)onboardingTrialMessage.Text="请检查手机媒体音量、蓝牙/耳机输出、下面两项音量和本句音频，再点“试听这一句”。本步骤会保留。";
        });
        box.AddView(Text("配音总音量",14));
        var total=new SeekBar(this){Max=100,Progress=(int)(session.Settings.Volume*100)};
        total.ProgressChanged+=(_,e)=>{if(e.FromUser){onboardingPreview?.Stop();session.Settings.Volume=e.Progress/100f;session.ApplyAudioSettings();UpdateOnboardingTrial();}};box.AddView(total);
        box.AddView(Text("本句角色音量 · "+selection.Node.Speaker,14));
        var role=new SeekBar(this){Max=100,Progress=SpeakerVolume.GetPercent(session.Settings.SpeakerVolumes,selection.Node.Speaker)};
        role.ProgressChanged+=(_,e)=>{if(e.FromUser){onboardingPreview?.Stop();session.SetSpeakerVolume(selection.Node.Speaker,e.Progress);UpdateOnboardingTrial();}};box.AddView(role);
        Button(box,"打开系统声音设置",()=>{PauseOnboarding();StartActivity(new Intent(global::Android.Provider.Settings.ActionSoundSettings));});
        box.AddView(Text(File.Exists(selection.AudioPath)?"本句音频文件已就绪。":"本句音频缺失，请换一句或返回上一步重新导入本章。",13));
    }
    void UpdateOnboardingTrial()
    {
        if(!alive||!onboardingActive)return;
        if(onboardingTrialMessage!=null&&onboardingPreview!=null)onboardingTrialMessage.Text=onboardingPreview.Message;
        if(onboardingHeard!=null)onboardingHeard.Enabled=onboardingPreview?.CanAcknowledge==true;
    }
    void BuildOnboardingFinish(LinearLayout box)
    {
        box.AddView(Text("已完成试听",24));
        if(Guide.Purpose==OnboardingPurpose.Listening)
        {
            box.AddView(Text("打开所选章节后，点“继续收听”开始听书。收听位置单独保存。",16));
            Button(box,"完成并打开听书",()=>FinishOnboarding(false));
        }
        else
        {
            box.AddView(Text("打开所选章节后，先选与游戏当前画面一致的台词。需要在游戏里控制时，再开启悬浮控制。",16));
            box.AddView(Text("悬浮控制可手动播放。OCR 定位和辅助点击在你选择对应功能时再授权。",14));
            Button(box,global::Android.Provider.Settings.CanDrawOverlays(this)?"显示悬浮控制":"按需开启悬浮控制",BeginOnboardingOverlay);
            Button(box,"完成并打开游戏配音",()=>FinishOnboarding(false));
            Button(box,"完成并返回游戏",()=>FinishOnboarding(true));
        }
    }
    void FinishOnboarding(bool returnToGame)
    {
        Safe(()=>
        {
            session.OpenOnboardingChapter();onboardingActive=false;StopOnboardingPreview();
            onboardingDialog?.Dismiss();onboardingDialog=null;
            page=Guide.Purpose==OnboardingPurpose.Listening?5:1;Render();
            if(returnToGame)ReturnToGame();
        });
    }
    void BeginOnboardingOverlay()
    {
        if(global::Android.Provider.Settings.CanDrawOverlays(this)){session.ShowOverlay();onboardingNotice="悬浮控制已开启；完成后可返回游戏。";RenderOnboarding();return;}
        Guide.BeginExternal("overlay");session.Onboarding.Save();
        try{StartActivityForResult(new Intent(global::Android.Provider.Settings.ActionManageOverlayPermission,global::Android.Net.Uri.Parse("package:"+PackageName)),Guide.ExternalRequestCode);}
        catch{Guide.EndExternal();session.Onboarding.Save();throw;}
    }
    void BeginOnboardingImport()
    {
        if(onboardingImportCancellation!=null||session.PackagesBusy)return;
        Guide.BeginExternal("import");session.Onboarding.Save();
        var intent=new Intent(Intent.ActionOpenDocument);intent.AddCategory(Intent.CategoryOpenable);intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraMimeTypes,new[]{"application/zip","application/octet-stream","application/x-zip-compressed"});
        intent.AddFlags(ActivityFlags.GrantReadUriPermission|ActivityFlags.GrantPersistableUriPermission);
        try{StartActivityForResult(intent,Guide.ExternalRequestCode);}
        catch{Guide.EndExternal();session.Onboarding.Save();throw;}
    }
    async Task<bool> HandleOnboardingResult(int request,Result result,Intent? data)
    {
        if(request<0x4000||request>=0x8000)return false;
        string kind=(request&1)==0?"import":"overlay";
        long epoch=Guide.ExternalEpoch;
        if(!alive||!onboardingActive||request!=Guide.ExternalRequestCode||!Guide.AcceptExternal(kind,epoch))return true;
        Guide.EndExternal();session.Onboarding.Save();
        if(kind=="overlay")
        {
            if(global::Android.Provider.Settings.CanDrawOverlays(this)){session.ShowOverlay();onboardingNotice="悬浮控制已开启；完成后可返回游戏。";}
            else onboardingNotice="尚未开启悬浮权限，仍可完成引导并使用主界面。";
            RenderOnboarding();return true;
        }
        if(result!=Result.Ok||data?.Data==null){onboardingNotice="已取消选择文件，可以继续选择已有章节或重新导入。";RenderOnboarding();return true;}
        using var cancellation=new CancellationTokenSource();onboardingImportCancellation=cancellation;
        onboardingNotice="正在导入所选 ZIP，请稍候…";RenderOnboarding();
        try
        {
            var uri=data.Data;
            ImportedZipSource? source=null;
            try{source=await Task.Run(()=>ImportedZipAccess.Read(ApplicationContext!,uri));}catch(Exception ex){session.Diagnostics.Log("引导ZIP",ex.Message);}
            cancellation.Token.ThrowIfCancellationRequested();
            using var input=ContentResolver!.OpenInputStream(uri)??throw new IOException("无法读取所选 ZIP。");
            var installed=await session.ImportAsync(input,cancellation.Token,source,openAfterImport:false);
            if(ReferenceEquals(onboardingImportCancellation,cancellation))onboardingImportCancellation=null;
            if(!alive||!onboardingActive||Guide.Epoch!=epoch)return true;
            if(installed!=null)
            {
                if(source!=null)ImportedZipAccess.PersistGrant(ApplicationContext!,uri,data.Flags);
                onboardingNotice="导入完成，请选择要试听的章节。";ChooseOnboardingChapter(installed.PackId);
            }
            else{onboardingNotice="导入已取消，可重新选择 ZIP。";RenderOnboarding();}
        }
        catch(Exception ex)
        {
            if(ReferenceEquals(onboardingImportCancellation,cancellation))onboardingImportCancellation=null;
            if(alive&&onboardingActive&&Guide.Epoch==epoch){onboardingNotice=ex is OperationCanceledException?"导入已取消，可重新选择 ZIP。":"导入未完成："+ex.Message;RenderOnboarding();}
        }
        finally{if(ReferenceEquals(onboardingImportCancellation,cancellation))onboardingImportCancellation=null;}
        return true;
    }
}
