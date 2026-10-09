using Android.Content;
using Android.Graphics;
using Android.OS;
using PgrVoice.AndroidApp.Ocr;
using PgrVoice.AndroidApp.Platform;
using PgrVoice.Following;
using PgrVoice.Packages;
using PgrVoice.AndroidApp.Contracts;
using Path = System.IO.Path;
using OperationCanceledException = System.OperationCanceledException;

namespace PgrVoice.AndroidApp;

/// <summary>所有剧情操作归入 Android 主线程。后台 OCR 只返回观察，不能直接修改剧情。</summary>
public sealed partial class AppSession
{
    static AppSession? instance;
    public static AppSession Get(Context context) => instance ??= new(context.ApplicationContext!);
    readonly Context context;
    readonly Handler main = new(Looper.MainLooper!);
    readonly string settingsFile;
    readonly StateSaveQueue saveQueue = new();
    readonly OcrEngineManager ocr;
    readonly IAudioOutput audio;
    readonly OverlayController overlay;
    long ocrEpoch;
    readonly SubtitleStability stability = new();
    readonly OcrInferenceSchedule ocrSchedule = new();
    int framePosted;
    long frameRevision, sequence, captureSession;
    int frameWidth, frameHeight;
    ScreenDisplayState frameDisplayState = ScreenDisplayState.Unavailable;
    byte[]? lastMask;
    bool manualOcr, manualOcrOverlay;
    bool uiVisible;
    string? notificationPage;
    long playGeneration;
    sealed record FrameRequest(Action<Bitmap,RegionSelectionContext>? Preview = null, bool OverlayResult = false);
    readonly PendingGameFrameRequest<FrameRequest> pendingFrame = new();
    long frameRequestId, regionGeneration;
    bool narrationRegion;
    readonly int[] regionPixels = new int[SubtitleRegionSelector.SampleWidth*SubtitleRegionSelector.SampleHeight];
    public PlaybackEngine? Engine { get; private set; }
    public IChapterStorage Packages { get; }
    public IScreenInput Screen { get; }
    public ProgressStore Progress { get; }
    public AppSettings Settings { get; }
    public ListeningSession Listening { get; }
    public SessionDiagnostics Diagnostics { get; }
    string statusText = "先导入章节 ZIP，再选择游戏当前台词。";
    string? settingsSaveWarning;
    public string? SettingsSaveWarning => settingsSaveWarning;
    public bool HasSettingsSaveWarning => settingsSaveWarning != null;
    public string Status { get => statusText + (settingsSaveWarning == null ? "" : "；" + settingsSaveWarning); private set => statusText = value; }
    public string OcrText { get; private set; } = "尚未识别";
    public IReadOnlyList<MatchCandidate> Candidates { get; private set; } = Array.Empty<MatchCandidate>();
#if DEBUG
    // 保留旧调试脚本的失败提示，发布版不再提供持续 OCR 跟随入口。
    public bool IsArmed => false;
    public void SetMode(FollowMode mode)
    {
        if(mode != FollowMode.Manual) throw new NotSupportedException("持续 OCR 自动跟随已移除，请使用一次性定位或点按跟随。");
        Invalidate();audio.Stop();Engine?.PauseForBrowse();Notify();
    }
#endif
    public string SectionId { get; set; } = "";
    public bool IsImporting { get; private set; }
    public (int Width,int Height) CaptureSize=>(frameWidth,frameHeight);
    public long ObservedFrameCount { get; private set; }
    public event Action? Changed;
    public event Action? ContentChanged;
    public event Action<OverlayCommand>? NavigationRequested;
    public event Action? CapturePermissionRequested;
    LineFeedbackSnapshot? pendingLineFeedback;
    public LineFeedbackSnapshot? TakePendingLineFeedback()
    { var snapshot=pendingLineFeedback;pendingLineFeedback=null;return snapshot; }

    AppSession(Context context)
    {
        this.context=context;
        string root=context.FilesDir!.AbsolutePath;
        var onboardingInstall=OnboardingInstallSnapshot.Capture(root);
        settingsFile=Path.Combine(root,"settings.json");
        try { Settings=Json.ReadWithBackup<AppSettings>(settingsFile,out _); } catch { Settings=new(); }
        Settings.Normalize();
        Onboarding=new(root,onboardingInstall,Settings.OnboardingShown);
        Packages=new AndroidChapterStorage(Path.Combine(root,"chapters")); Progress=new(Path.Combine(root,"progress")); Progress.Load();
        Screen=new AndroidScreenInput(context);
        Diagnostics=new(Path.Combine(root,"diagnostics"));
        _=Task.Run(()=>{try{Packages.CleanAbandonedImports();}catch(Exception ex){Diagnostics.Log("导入清理",ex.Message);}});
        CoreDiagnostics.Message+=(kind,text)=>Diagnostics.Log(kind,text);
        ocr=new(context); audio=new AndroidAudioPlayer(context); overlay=new(context);
        overlay.SetCompactButtonsVisible(Settings.ShowCompactButtons);
        overlay.SetCompactControlsVisible(Settings.ShowCompactControls);
        overlay.CompactButtonsChanged+=SetCompactButtonsVisible;
        overlay.CompactControlsChanged+=SetCompactControlsVisible;
        Listening=new(context,Packages,PrepareListening,Diagnostics,
            node=>SpeakerVolume.Apply(Settings.Volume,Settings.SpeakerVolumes,node?.Speaker));
        audio.PlaybackCompleted+=ticket=>Post(()=>OnAutoAudioCompleted(ticket));
        GameAdvanceAccessibilityService.SessionInvalidated+=message=>Post(CaptureFollowSessionInvalidation(message));
        overlay.Command+=OnOverlayCommand;
        overlay.CaptureInteraction+=message=>Diagnostics.Log("定位取消触摸",message);
        overlay.CaptureCancelRequested+=generation=>Post(()=>
        {
            if(CancelBranchFromCaptureControl(generation))return;
            bool current=autoPlay.Phase==AutoPlaybackPhase.CheckingCurrent&&
                generation==autoCaptureControlGeneration&&autoCaptureControlEpoch==autoPlay.Epoch;
            Diagnostics.Log("开局取消请求",$"control={generation}; current={autoCaptureControlGeneration}; autoEpoch={autoPlay.Epoch}; accepted={current}");
            if(current)
                PauseAutoPlayback("已取消开局定位，尚未点击游戏。请手动选句或 OCR 定位后再开始。");
        });
        overlay.BrowseRequested+=OnOverlayBrowseRequested;
        overlay.BrowseRefreshRequested+=OnOverlayBrowseRefreshRequested;
        overlay.BrowseItemSelected+=OnOverlayBrowseSelected;
        overlay.Error+=message=>Post(()=>ReportError(message));
        audio.Error+=message=>Post(()=>ReportError(message));
        audio.Interrupted+=message=>Post(()=>{if(autoPlay.Running){PauseAutoPlayback(message+" 请核对游戏位置后重新开启。");return;}Invalidate();Engine?.PauseForBrowse();Status=message;Notify();});
        Screen.FrameAvailable+=OnFrame;
        Screen.DisplayChanged+=state=>Post(()=>OnDisplayChanged(state));
        Screen.CaptureStopped+=message=>Post(()=>{if(BranchFollowRunning){StopBranchFollow(message+"；请手动确认分支。");return;}branchResumeIntent=null;if(ClickFollowRunning)return;if(autoPlay.Running){PauseAutoPlayback(message+"；请重新授权后开启，或使用点按跟随与手动播放。");return;}Invalidate();Engine?.PauseForBrowse();Status=message+"；手动播放仍可使用。";Notify();});
        VoiceForegroundService.StopRequested+=()=>Post(StopGame);
        if(Settings.LastPackId is { } id) try { LoadPack(id); } catch(Exception ex){ReportError(ex.Message);}
        ApplyAudioSettings();
    }
    public void Post(Action action)
    {
        if(Looper.MyLooper()==Looper.MainLooper) Guard(action); else main.Post(()=>Guard(action));
    }
    void Guard(Action action){try{action();}catch(Exception ex){ReportError(ex.Message);}}
    void Notify(){Changed?.Invoke();overlay.UpdateClickFollow(ClickFollowRunning||BranchFollowRunning);overlay.UpdateCurrentLine(Engine?.Current?.Speaker??"",OverlayCurrentLineText());overlay.UpdateStatus(Status,Engine?.Mode==RunMode.Paused,autoPlay.Running);RefreshOverlayBrowseIfNeeded();VoiceForegroundService.UpdateNotification(Status,notificationPage);}
    public void SetCompactButtonsVisible(bool enabled)
    {
        Settings.ShowCompactButtons=enabled;overlay.SetCompactButtonsVisible(enabled);SaveSettings();ContentChanged?.Invoke();Notify();
    }
    public void SetCompactControlsVisible(bool enabled)
    {
        Settings.ShowCompactControls=enabled;overlay.SetCompactControlsVisible(enabled);SaveSettings();ContentChanged?.Invoke();Notify();
    }
    void ReportError(string message){overlay.SetCaptureHidden(false);if(BranchFollowRunning)StopBranchFollow(message);else if(ClickFollowRunning)StopClickFollow(message);else if(autoPlay.Running)PauseAutoPlayback(message);else{Status=message;Notify();}Diagnostics.Error(message);}
    void Invalidate(bool clearCandidates=true,bool clearPreview=true,bool preserveBranchIntent=false){CancelBranchAnchorRequest();ResetOverlayBrowseRequests();CancelBranchFollow(preserveBranchIntent);CancelClickFollow();CancelAutoPlayback();ocrEpoch++;ocrSchedule.Reset();manualOcr=false;manualOcrOverlay=false;playGeneration++;if(clearPreview){pendingFrame.Clear();overlay.SetCaptureHidden(false);}if(clearCandidates){Candidates=Array.Empty<MatchCandidate>();notificationPage=null;}}
    public void SetUiVisible(bool visible)
    {
        uiVisible=visible;
        overlay.UpdateAppForeground(visible);
        if(visible){Invalidate(false);Engine?.PauseForBrowse();Notify();}
    }
    public void ChangeOcrEngine(OcrEngineKind kind)
    {
        Invalidate();Engine?.PauseForBrowse();Settings.OcrEngine=kind;SaveSettings();
        Status="识别引擎已切换，请重新定位并确认当前位置。";Notify();
    }
    public void SetOcrFullScreen(bool enabled)
    {
        Invalidate();Engine?.PauseForBrowse();Settings.OcrFullScreen=enabled;SaveSettings();
        lastMask=null;stability.Reset();regionGeneration++;
        Status=enabled?"已切换全画面定位，无需框选；识别后请核对候选。":"已切换框选定位，使用当前屏幕保存的字幕框。";
        ContentChanged?.Invoke();Notify();
    }
    public void SaveSettings()
    {
        string? previousWarning = settingsSaveWarning;
        try { Json.Save(settingsFile,Settings); settingsSaveWarning = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException or ArgumentException)
        {
            settingsSaveWarning = "设置尚未保存，本次已生效；请检查手机可用存储空间后重新设置";
            Diagnostics.Log("设置保存失败",ex.Message);
        }
        // 音量等入口没有单独刷新状态；仅警告变化时通知，避免每次滑动重建界面。
        if (settingsSaveWarning != previousWarning) Notify();
    }
    public void ApplyAudioSettings()
    {
        audio.SetVolume(SpeakerVolume.Apply(Settings.Volume,Settings.SpeakerVolumes,playingSpeaker));
        audio.Strategy=Settings.VoicePriority?AudioStrategy.VoicePriority:AudioStrategy.Simultaneous;
        Listening.RefreshVolume();
        SaveSettings();
    }
    public void SetSpeakerVolume(string? speaker,int percent)
    {
        SpeakerVolume.Set(Settings.SpeakerVolumes,speaker,percent);
        ApplyAudioSettings();
    }
    public void LoadPack(string id)
    {
        if(IsDeletingPackage)throw new InvalidOperationException("正在删除章节，请等待完成。");
        if(Listening.IsPlaying)Listening.Pause();
        Invalidate();audio.Stop();
        if(Engine!=null){SaveProgress();saveQueue.Flush();}
        var next=new PlaybackEngine(Packages.Load(id));
        Progress.TryRestore(next); Engine=next;
        SectionId=next.Current?.SectionId??next.Pack.Chapters.SelectMany(c=>c.Sections).First().Id;
        next.PlayRequested+=node=>PlayNode(next,node);
        next.StopRequested+=()=>{if(ReferenceEquals(Engine,next)){playGeneration++;audio.Stop();}};
        next.Changed+=()=>{if(!ReferenceEquals(Engine,next))return;SaveProgress();Notify();};
        Settings.LastPackId=id;SaveSettings();
        Status=next.Current==null?"请选择与游戏画面一致的台词。":"已恢复进度，确认位置后再开始。";
        if(Progress.LastError.Length>0)Status=Progress.LastError;
        ContentChanged?.Invoke();Notify();
    }
    public void SaveProgress()
    {
        if(Engine?.Current==null)return;
        var pack=Engine.Pack;var snapshot=Engine.ExportNavigation();
        _=saveQueue.Enqueue(()=>Progress.Save(pack,snapshot),failure=>{if(failure!=null)Post(()=>ReportError(failure.Message));});
    }
    public void PreviewNode(Node node)
    {
        if(Listening.IsPlaying)Listening.Pause();
        if(Engine==null||Engine.Mode==RunMode.Original)return;
        Invalidate();Engine.PauseForBrowse();PlayNode(Engine,node);Status="试听，不改变剧情位置。";Notify();
    }
    public async Task<InstalledPackage?> ImportAsync(Stream stream,CancellationToken token,ImportedZipSource? source=null,bool openAfterImport=true)
    {
        if(PackagesBusy)throw new InvalidOperationException("章节文件正在处理中，请等待完成。");
        // 导入原子替换音频目录前释放听书文件，更新后再次选择本章恢复独立进度。
        if(Listening.Pack!=null)Listening.Stop();
        IsImporting=true;Status="正在读取章节 ZIP…";ContentChanged?.Invoke();Notify();
        try
        {
            var progress=new Progress<PackageImportProgress>(p=>Post(()=>{Status=$"{p.Stage} · {p.CompletedFiles}/{p.TotalFiles} · {p.Bytes/1048576} MB";Notify();}));
            var installed=await Packages.ImportAsync(stream,token,progress);
            if(source!=null){Settings.ImportedArchives[installed.PackId]=source;SaveSettings();}
            else{Settings.ImportedArchives.Remove(installed.PackId);SaveSettings();}
            // 进度单独保存，原包只在所有校验完成后切换。
            Listening.PackageUpdated(installed.PackId);
            if(openAfterImport)LoadPack(installed.PackId);
            // 导入与加载均成功后，让章节页显示刚导入的章节；批量时跟随最后成功项。
            if(openAfterImport)
            {
                var importedPack=Engine!.Pack;
                Settings.ChapterCategory=ChapterCatalog.Category(importedPack.Id,importedPack.Title);
                SaveSettings();
            }
            Status=(installed.IsUpdate?"章节更新完成。":"章节导入完成。")+statusText;
            return installed;
        }
        catch(OperationCanceledException){Status="已取消导入，原章节与进度保留。";return null;}
        finally{IsImporting=false;ContentChanged?.Invoke();Notify();}
    }
    public void Command(Action<PlaybackEngine> action,bool confirmAfter=false)
    {
        if(Listening.IsPlaying)Listening.Pause();
        if(Engine==null){Status="请先导入并打开一个章节。";Notify();return;}
        Invalidate();action(Engine);
        if(Engine.Current!=null)SectionId=Engine.Current.SectionId;
        if(Engine.Mode==RunMode.Original)Status="游戏原声：配音已停止、位置已冻结；选择当前台词后续接。";
        else if(Engine.Mode==RunMode.Paused)Status="已暂停。核对游戏位置后，点确认位置继续。";
        else if(Engine.Mode==RunMode.Choice)Status="请在分支中选择与游戏一致的选项。";
        else if(Engine.Mode==RunMode.Gap)Status="这段内容需要核对，请定位当前台词或续接菜单。";
        else if(Engine.Mode==RunMode.End)Status="当前剧情已结束。";
        ContentChanged?.Invoke();Notify();
    }
    public void ConfirmLine(string id,bool resumeOriginal=false) => Command(engine=>
    {
        if(!engine.ConfirmGameLine(id,resumeOriginal))throw new InvalidOperationException(engine.NavigationError);
        overlay.ClearAutoPlaybackPaused();
        SectionId=engine.Current!.SectionId;
    },true);
    public void ConfirmCurrent()
    {
        if(Listening.IsPlaying)Listening.Pause();
        Invalidate();
        if(Engine?.Current?.Kind!="line"){Status="请先选择当前台词。";Notify();return;}
        if(Engine.Mode==RunMode.Original){Status="原声时段需要选择续接台词。";Notify();return;}
        if(!Engine.ConfirmCurrentPosition()){Status=Engine.NavigationError;Notify();return;}
        overlay.ClearAutoPlaybackPaused();
        Status="位置已确认，可用点按跟随、上一句、下一句或重播。";
        Notify();
    }
    public void PrepareCapture(){if(Listening.IsPlaying)Listening.Pause();Invalidate();Engine?.PauseForBrowse();lastMask=null;stability.Reset();Status="正在请求屏幕捕获授权，授权后请再次确认当前位置。";Notify();}
    public void CaptureDenied(){Invalidate();Status="未授权屏幕捕获，可继续使用手动播放。";Notify();}
    public void RequestOcr(bool overlayResult = false)
    {
        if(Listening.IsPlaying)Listening.Pause();
        Invalidate();Engine?.PauseForBrowse();
        overlay.ClearAutoPlaybackPaused();
        if(Engine==null){Status="请先打开与游戏一致的章节，再进行 OCR 定位。";Notify();return;}
        if(!Screen.CaptureActive){Status="定位需要屏幕捕获，请授权后再次点击 OCR 定位。";CapturePermissionRequested?.Invoke();Notify();return;}
        QueueFrameRequest(new(OverlayResult:overlayResult));Status=(Settings.OcrFullScreen?"全画面定位：":"框选定位：")+(overlayResult?"等待游戏画面稳定，结果会显示在悬浮定位页。":"等待游戏画面稳定，识别后请确认候选。");Notify();
    }
    public void RequestPreview(Action<Bitmap,RegionSelectionContext> callback)
    {
        if(!Screen.CaptureActive){Status="先授权屏幕捕获，再框选字幕区域。";CapturePermissionRequested?.Invoke();Notify();return;}
        Invalidate();Engine?.PauseForBrowse();QueueFrameRequest(new(callback));Status="等待游戏画面稳定以设置字幕框。";Notify();
    }
    void QueueFrameRequest(FrameRequest request)
    {
        overlay.SetExpanded(false);
        overlay.SetCaptureHidden(true);
        long id=++frameRequestId;
        pendingFrame.Set(Screen.SessionId,SystemClock.ElapsedRealtime(),request);
        main.PostDelayed(()=>Guard(()=>
        {
            if(id!=frameRequestId||!pendingFrame.IsPendingFor(Screen.SessionId))return;
            if(pendingFrame.Poll(SystemClock.ElapsedRealtime(),Screen.SessionId,Screen.DisplayState,
                frameWidth,frameHeight,uiVisible,out _)==GameFrameRequestResult.Expired)FrameRequestExpired();
        }),10_050);
    }
    void FrameRequestExpired(){CancelBranchAnchorRequest();overlay.SetCaptureHidden(false);Status="没有等到稳定的游戏画面，请回到游戏后重试定位或框选。";Notify();}
    public void SetRegion(int width,int height,ScreenRegion region)
    {
        SetRegion(new(frameDisplayState,captureSession,width,height),region);
    }
    public void SetRegion(RegionSelectionContext selection,ScreenRegion region)
    {
        if(!Screen.CaptureActive||frameDisplayState!=selection.Display||
            !selection.Matches(Screen.DisplayState,Screen.SessionId,frameWidth,frameHeight))
        {Status="框选期间屏幕形态或捕获会话已变化，请重新取图框选。";Notify();return;}
        Settings.DisplayRegions[selection.Display.RegionKey!]=region.Clamp();SaveSettings();Invalidate();lastMask=null;stability.Reset();
        Status=Settings.OcrFullScreen?"字幕框已保存；当前仍为全画面定位，可在定位范围中切换为框选区域。":"当前屏幕的字幕区域已保存，可重新 OCR 定位。";Notify();
    }
    public void ShowOverlay(){if(Listening.IsPlaying)Listening.Pause();VoiceForegroundService.EnsureStarted(context);overlay.Show();}
    public void HideOverlay(){if(ClickFollowRunning||BranchFollowRunning)StopClickFollow("悬浮控制已隐藏，点按跟随已停止。");else branchResumeIntent=null;PauseAutoPlaybackForOverlayExit("悬浮控制已隐藏，自动播放已暂停。");overlay.Hide();}
    public void Stop(){Listening.Stop();StopGame();}
    void StopGame(){Invalidate();audio.Stop();Engine?.PauseForBrowse();overlay.Hide();VoiceForegroundService.StopAll(context);_=UnloadOcrAfterStopAsync();SaveProgress();Status="已停止，进度已保存。";Notify();}
    void PrepareListening()
    {
        if(PackagesBusy)throw new InvalidOperationException("章节文件正在处理中，请完成后再开始听书。");
        StopGame();
    }
    async Task UnloadOcrAfterStopAsync()
    {
        try { await ocr.UnloadAsync().ConfigureAwait(false); }
        catch(Exception ex) { Diagnostics.Log("识别资源释放",ex.Message); }
    }
    void OnOverlayCommand(OverlayCommand command)
    {
        Post(()=>
        {
            switch(command)
            {
                case OverlayCommand.Next:ManualOverlayStep(e=>e.Next(true));break;
                case OverlayCommand.Previous:ManualOverlayStep(e=>e.Previous());break;
                case OverlayCommand.Replay:ManualOverlayStep(e=>e.Replay());break;
                case OverlayCommand.Pause:if(Engine?.Mode==RunMode.Paused)ConfirmCurrent();else Command(e=>e.PauseForBrowse());break;
                case OverlayCommand.Original:Command(e=>e.EnterOriginal());break;
                case OverlayCommand.Ocr:if(Screen.CaptureActive)RequestOcr(true);else OpenApp("Capture");break;
                case OverlayCommand.Hide:HideOverlay();break;
                case OverlayCommand.AutoPlay:if(autoPlay.Running)PauseAutoPlayback("自动播放已暂停。",false);else StartAutoPlayback();break;
                case OverlayCommand.AdvanceRegion:SelectAdvanceRegion();break;
                case OverlayCommand.ClickFollow:if(ClickFollowRunning||BranchFollowRunning)StopClickFollow();else StartClickFollow();break;
                case OverlayCommand.ConfirmPosition:ConfirmCurrent();break;
                case OverlayCommand.Feedback:OpenOverlayLineFeedback();break;
                default:NavigationRequested?.Invoke(command);OpenApp(command.ToString());break;
            }
        });
    }
    void OpenOverlayLineFeedback()
    {
        var engine=Engine;
        if(engine?.Current is not {Kind:"line"} node){Status="请先定位要反馈的台词，或到历史记录选择句子。";Notify();return;}
        var recent=engine.History.Take(Math.Clamp(engine.HistoryPosition+1,0,engine.History.Count)).TakeLast(4)
            .Select(v=>engine.Pack.ById.GetValueOrDefault(v.NodeId)).OfType<Node>();
        string version=context.PackageManager?.GetPackageInfo(context.PackageName!,global::Android.Content.PM.PackageInfoFlags.MetaData)?.VersionName??"未知版本";
        pendingLineFeedback=LineFeedback.Capture(engine.Pack,node,version,"Android","游戏悬浮控制",recent);
        // 先终止点击/点按/旧回调，再打开主界面；反馈完成不会自行恢复。
        Invalidate();audio.Stop();engine.PauseForBrowse();
        Status="已暂停，正在填写这句反馈。";Notify();OpenApp("Feedback");
    }
    void OpenApp(string page)
    {
        PauseAutoPlaybackForOverlayExit("已打开主界面，自动播放已暂停。");
        // 包括 OCR 异步完成后的定位导航，先收起面板，避免遮住应用中的分支等对话框。
        overlay.SetExpanded(false);
        overlay.UpdateAppForeground(true);
        var intent=new Intent(context,typeof(MainActivity));intent.AddFlags(ActivityFlags.NewTask|ActivityFlags.SingleTop);intent.PutExtra("page",page);context.StartActivity(intent);
    }
    string? CurrentFrameKey=>lastMask==null?null:$"{captureSession}:{frameDisplayState.Revision}:{frameWidth}x{frameHeight}:{regionGeneration}:{frameRevision}";
    bool HasConfirmedRegion=>frameWidth>0&&frameHeight>0&&frameDisplayState==Screen.DisplayState&&
        Settings.HasRegion(frameDisplayState.RegionKey);
    void OnDisplayChanged(ScreenDisplayState state)
    {
        if(BranchFollowRunning){StopBranchFollow("屏幕形态已变化，分支跟随已停止。请手动核对。");return;}
        branchResumeIntent=null;
        if(ClickFollowRunning){CheckClickFollowEnvironment();return;}
        if(state!=Screen.DisplayState||!Screen.CaptureActive||frameWidth==0)return;
        if(autoPlay.Running)PauseAutoPlayback("屏幕形态已变化，自动播放已停止。请为当前屏幕核对字幕框和点击区域，再重新定位开启。");
        // 尚未取图的主动请求等待游戏方向稳定；已经取得的截图仍严格绑定显示版本。
        Invalidate(clearPreview:!pendingFrame.IsPendingFor(Screen.SessionId));Engine?.PauseForBrowse();lastMask=null;stability.Reset();
        Status=Settings.OcrFullScreen?"屏幕形态已变化；全画面定位无需重新框选，请重新确认位置。":
            state.RegionKey==null?"暂时无法确认物理屏幕，请稍后重新定位；手动播放仍可使用。":
            Settings.HasRegion(state.RegionKey)?"屏幕形态已变化，已找回此屏幕的字幕框；请重新确认位置。":
            "屏幕形态已变化；下次 OCR 定位前请为当前屏幕重新框选字幕。";
        Notify();
    }
    void OnFrame(CapturedFrame frame)
    {
        if(Interlocked.CompareExchange(ref framePosted,1,0)!=0){frame.Dispose();Diagnostics.Discard();return;}
        main.Post(()=>{try{HandleFrame(frame);}catch(Exception ex){ReportError(ex.Message);}finally{frame.Dispose();Volatile.Write(ref framePosted,0);}});
    }
    void HandleFrame(CapturedFrame frame)
    {
        if(BranchFollowRunning){HandleBranchFollowFrame(frame);return;}
        if(ClickFollowRunning)return;
        ObservedFrameCount++;
        Diagnostics.Frame(frame.IsRepeatedSample);
        if(!Screen.CaptureActive||frame.SessionId!=Screen.SessionId||frame.DisplayState!=Screen.DisplayState){Diagnostics.Discard();return;}
        if(captureSession!=frame.SessionId||frame.Width!=frameWidth||frame.Height!=frameHeight||frameDisplayState!=frame.DisplayState)
        {
            bool resized=frameWidth>0;
            if(autoPlay.Running)PauseAutoPlayback("捕获画面尺寸已变化，自动播放已停止。请核对当前屏幕的字幕框和点击区域后重新开启。");
            Invalidate(clearPreview:!pendingFrame.IsPendingFor(frame.SessionId));lastMask=null;stability.Reset();
            captureSession=frame.SessionId;frameWidth=frame.Width;frameHeight=frame.Height;
            frameDisplayState=frame.DisplayState;
            Diagnostics.Log("显示形态",$"profile={frameDisplayState.RegionKey??"unavailable"}; revision={frameDisplayState.Revision}; capture={frameWidth}x{frameHeight}");
            if(resized){Engine?.PauseForBrowse();Status=Settings.OcrFullScreen?
                "屏幕或捕获尺寸已变化；全画面定位无需重新框选，请确认位置。":Settings.HasRegion(frameDisplayState.RegionKey)?
                "屏幕或捕获尺寸已变化，已找回对应字幕框；请确认位置。":
                "屏幕或捕获尺寸已变化，请为当前屏幕框选字幕并确认位置。";Notify();}
        }
        var requestState=pendingFrame.Poll(SystemClock.ElapsedRealtime(),frame.SessionId,frame.DisplayState,
            frame.Width,frame.Height,uiVisible,out var frameRequest);
        if(requestState==GameFrameRequestResult.Expired){FrameRequestExpired();return;}
        if(requestState==GameFrameRequestResult.Waiting)return;
        if(requestState==GameFrameRequestResult.Ready)
        {
            if(frameRequest!.Preview is { } preview)
            {
                overlay.SetCaptureHidden(false);
                Status="截图已准备，请框选字幕区域。";notificationPage="Locate";Notify();
                preview(frame.Bitmap.Copy(Bitmap.Config.Argb8888!,false)!,new(frameDisplayState,captureSession,frameWidth,frameHeight));return;
            }
            manualOcr=true; manualOcrOverlay=frameRequest.OverlayResult;
        }
        if(autoPlay.Running&&!CheckAutoPlaybackEnvironment())return;
        if(autoPlay.Running&&autoPlay.Phase!=AutoPlaybackPhase.CheckingCurrent)return;
        // 自动播放由人工核对起点；OCR 仅服务主动定位，不持续识别下一句。
        if(!autoPlay.Running&&!manualOcr)return;
        if(Engine==null)return;
        var region=Settings.OcrFullScreen?new ScreenRegion(0,0,1,1):Settings.RegionFor(frameDisplayState.RegionKey);
        bool central=false;
        if(!Settings.OcrFullScreen)
        {
            using(var sampleBitmap=Bitmap.CreateScaledBitmap(frame.Bitmap,SubtitleRegionSelector.SampleWidth,SubtitleRegionSelector.SampleHeight,true)!)
                sampleBitmap.GetPixels(regionPixels,0,SubtitleRegionSelector.SampleWidth,0,0,SubtitleRegionSelector.SampleWidth,SubtitleRegionSelector.SampleHeight);
            central=SubtitleRegionSelector.TrySelectNarration(regionPixels,SubtitleRegionSelector.SampleWidth,SubtitleRegionSelector.SampleHeight,out var centralRegion);
            if(central)region=new((float)centralRegion.X,(float)centralRegion.Y,(float)centralRegion.Width,(float)centralRegion.Height);
        }
        if(central!=narrationRegion)
        {
            narrationRegion=central;regionGeneration++;lastMask=null;stability.Reset();ocrSchedule.Reset();ResetAutoConsensus();
            Diagnostics.Log("识别区域",Settings.OcrFullScreen?"全画面定位":central?"临时使用黑底居中旁白区域":"恢复用户字幕区域");
        }
        int x=(int)(region.Left*frame.Width),y=(int)(region.Top*frame.Height);
        int w=Math.Clamp((int)(region.Width*frame.Width),1,frame.Width-x),h=Math.Clamp((int)(region.Height*frame.Height),1,frame.Height-y);
        var crop=Bitmap.CreateBitmap(frame.Bitmap,x,y,w,h)!;
        // 全画面裁切可能返回输入位图；它由 CapturedFrame 负责释放。
        using var ownedCrop=ReferenceEquals(crop,frame.Bitmap)?null:crop;
        byte[] mask=TextMask(crop);var observationGate=stability.Observe(mask);lastMask=mask;frameRevision=stability.Revision;
        long sample=++sequence;
        if(observationGate.Changed)
        {
            ResetAutoConsensus();
        }
        bool requested=manualOcr, showOcrInOverlay=manualOcrOverlay;
        long now=SystemClock.ElapsedRealtime();
        if(!ocrSchedule.TryBegin(now,ocrEpoch,CurrentFrameKey!,observationGate.Stable,requested,false,out var ticket))return;
        manualOcr=false;
        Diagnostics.Log("OCR 定位范围",$"{(Settings.OcrFullScreen?"全画面":"框选区域")}; input={w}x{h}; capture={frame.Width}x{frame.Height}");
        Bitmap input;
        long modelRequest;
        try { modelRequest=ocr.CreateRequest(); input=crop.Copy(Bitmap.Config.Argb8888!,false)!; }
        catch { ocrSchedule.Fail(ticket); throw; }
        long epoch=ocrEpoch,revision=frameRevision,recognizedSession=frame.SessionId,recognizedRegion=regionGeneration;string frameKey=CurrentFrameKey!,section=SectionId;
        var engine=Engine;var kind=Settings.OcrEngine;var displayAtRecognition=frame.DisplayState;
        _=Task.Run(async()=>
        {
            try
            {
                var result=await ocr.RecognizeAsync(kind,input,modelRequest);
                Diagnostics.Ocr(result.Elapsed.TotalMilliseconds);
                Post(()=>
                {
                    if(!Screen.CaptureActive||recognizedSession!=Screen.SessionId||!ReferenceEquals(Engine,engine)||epoch!=ocrEpoch||displayAtRecognition!=Screen.DisplayState||recognizedRegion!=regionGeneration||(!requested&&revision!=frameRevision)) {ocrSchedule.Abandon(ticket);Diagnostics.Discard();return;}
                    OcrText=result.Text;
                    var blocks=result.Blocks.Select(b=>new OcrBlock{Text=b.Text,Score=b.Confidence,Box=b.Polygon.Select(p=>new double[]{p.X,p.Y}).ToArray()}).ToList();
                    RememberOcrEvidence(engine,section,blocks);
                    Candidates=Matcher.Find(engine,section,blocks);
                    if(requested)
                    {
                        overlay.SetCaptureHidden(false);
                        ocrSchedule.Complete(ticket,!result.IsTruncated,false,result.Elapsed.TotalMilliseconds);
                        if(TryHandleBranchAnchorResult(blocks,w,h,result.IsTruncated,out var anchorNotice)){Notify();return;}
                        Status=anchorNotice??(Candidates.Count==0?"未找到明确候选，可切换定位范围或从台词页手动选句。":"识别完成，请核对台词与路线后采用候选。");
                        notificationPage="Locate";ContentChanged?.Invoke();
                        if(showOcrInOverlay&&overlay.CanShow){overlay.Show();overlay.ShowBrowsePage(OverlayBrowsePage.Locate);}
                        else OpenApp("Locate");
                    }
                    else
                    {
                        var observation=new FollowObservation(epoch,sample,frameKey,!result.IsTruncated,blocks,section);
                        if(autoPlay.Running)HandleAutoPlaybackObservation(engine,observation);
                        ocrSchedule.Complete(ticket,!result.IsTruncated,AutoNeedsConsensus,result.Elapsed.TotalMilliseconds);
                        if(ocrSchedule.Slow)statusText+=" 本次定位耗时较长，也可停止后手动选择当前句。";
                    }
                    Notify();
                });
            }
            catch(OperationCanceledException){Post(()=>{ocrSchedule.Fail(ticket);if(epoch==ocrEpoch)overlay.SetCaptureHidden(false);});}
            catch(Exception ex){Post(()=>{ocrSchedule.Fail(ticket);if(Screen.CaptureActive&&recognizedSession==Screen.SessionId&&epoch==ocrEpoch&&displayAtRecognition==Screen.DisplayState&&recognizedRegion==regionGeneration)ReportError("识别失败："+ex.Message);});}
            finally{input.Dispose();Post(()=>ocrSchedule.Abandon(ticket));}
        });
    }
    static byte[] TextMask(Bitmap bitmap)
    {
        using var scaled=bitmap.Width==384&&bitmap.Height==96?null:Bitmap.CreateScaledBitmap(bitmap,384,96,true)!;
        var small=scaled??bitmap;
        var pixels=new int[384*96];small.GetPixels(pixels,0,384,0,0,384,96);
        var mask=new byte[pixels.Length];
        for(int i=0;i<pixels.Length;i++)
        {int r=(pixels[i]>>16)&255,g=(pixels[i]>>8)&255,b=pixels[i]&255;mask[i]=(byte)(Math.Min(r,Math.Min(g,b))>155&&Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b))<75?1:0);}
        return mask;
    }
}
