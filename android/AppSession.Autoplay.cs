using Android.OS;
using PgrVoice.AndroidApp.Platform;
using PgrVoice.AndroidApp.Ui;
using PgrVoice.Following;
using System.Text.Json;

namespace PgrVoice.AndroidApp;

public sealed partial class AppSession
{
    readonly AutoPlaybackCycle autoPlay=new();
    long autoServiceToken,autoCaptureSession,autoLastSequence=-1;
    long autoCaptureControlGeneration,autoCaptureControlEpoch;
    ScreenDisplayState autoDisplay=ScreenDisplayState.Unavailable;
    AdvanceTapTarget? autoTarget;
    string? autoPendingNode,autoPendingFrame;
    int autoConsensus;
    Action? autoTimer;
    Action? autoSilentTimer;
    sealed record AutoStartConfirmation(PlaybackEngine Engine,Node Node,long Epoch,long AudioGeneration,
        string Navigation,ScreenDisplayState Display,long CaptureSession,bool CaptureActive,string GamePackage,
        GameAdvanceAvailability Foreground,Node? CommonCandidate=null)
    {
        // 系统失焦事件先同步作废，再排主线程收窗；已排队的确认不能抢在收窗之前生效。
        public volatile bool Invalidated;
    }
    AutoStartConfirmation? autoStartConfirmation;
    long autoStartServiceToken;
    Action? autoStartWatchdog;
    AdvanceRegionOverlay? advanceSelector;
    public bool AutoPlaybackRunning=>autoPlay.Running;
    public string AutoPlaybackPhaseName=>autoPlay.Phase.ToString();
    bool AutoNeedsConsensus=>autoPlay.Phase==AutoPlaybackPhase.CheckingCurrent;
    // 本次用户启动时的前台身份，仅用于切出停止；不保存、不要求选服。
    string AutoGamePackage = "";
    public void StopAutoPlayback()=>PauseAutoPlayback("自动播放已停止，请核对游戏位置后再开始。",false);
    void PauseAutoPlaybackForOverlayExit(string reason)
    {
        // 确认窗口已关闭、延迟起播尚未执行时也必须撤销，不能只检查 Running。
        if(autoPlay.Running||autoStartConfirmation!=null||manualBranchConfirmation!=null||branchCommonWait!=null||ConfirmedBranchWaiting||SourceChoiceWaiting)PauseAutoPlayback(reason,false);
    }
    void ResetAutoConsensus(){autoPendingNode=autoPendingFrame=null;autoConsensus=0;autoLastSequence=-1;}
    void CancelAutoPlayback()
    {
        overlay.ClearBranchEntered();
        CancelSourceChoiceWait();
        CancelConfirmedBranchWait();
        CancelManualBranchFollow();
        ClearDefaultAutoPlayback();
        ClearBranchAutoReturn();
        CancelAutoStartConfirmation();
        bool wasRunning=autoPlay.Running||autoServiceToken!=0;
        autoCaptureControlGeneration=autoCaptureControlEpoch=0;
        autoPlay.Stop();
        if(autoTimer!=null){main.RemoveCallbacks(autoTimer);autoTimer=null;}
        if(autoSilentTimer!=null){main.RemoveCallbacks(autoSilentTimer);autoSilentTimer=null;}
        autoServiceToken=0;autoTarget=null;ResetAutoConsensus();
        if(wasRunning)GameAdvanceAccessibilityService.CancelSession();
        if(wasRunning)overlay.SetCaptureHidden(false);
        if(advanceSelector!=null){advanceSelector.Dispose();advanceSelector=null;overlay.Show();}
    }
    void PauseAutoPlayback(string reason,bool showNotice=true)
    {
        CancelAutoPlayback();ocrEpoch++;ocrSchedule.Reset();pendingFrame.Clear();manualOcr=false;playGeneration++;
        audio.Stop();Engine?.PauseForBrowse();Status=reason;Notify();
        Diagnostics.Log("自动播放暂停",reason);
        if(showNotice&&overlay.CanShow)overlay.ShowAutoPlaybackPaused(reason);
    }
    void StartAutoPlayback()
    {
        if(Listening.IsPlaying)Listening.Pause();
        Invalidate();audio.Stop();Engine?.PauseForBrowse();
        overlay.ClearAutoPlaybackPaused();
        if(Engine==null){PauseAutoPlayback("请先打开与游戏一致的章节。");return;}
        if(Engine.Mode==RunMode.Original){PauseAutoPlayback("原声时段请先选择续接台词，再开启自动播放。");return;}
        var foreground=GameAdvanceAccessibilityService.GetForegroundAvailability();
        if(uiVisible||!foreground.CanTap||string.IsNullOrWhiteSpace(foreground.ForegroundPackage))
        {PauseAutoPlayback(uiVisible?"请回到游戏，在悬浮控制中开始。":foreground.Reason);return;}
        AutoGamePackage=foreground.ForegroundPackage;
        if(Settings.AutoPlayConfirmBranch && SourceChoicePolicy.TryAt(Engine,out var sourceOffer) && sourceOffer!=null)
        {
            if(!PrepareAutoPlaybackEnvironment())return;
            if(autoTarget is { } sourceTarget && autoDisplay.RegionKey is { } sourceKey)
                BeginSourceChoiceWait(Engine,sourceOffer,sourceTarget,sourceKey);
            return;
        }
        if(Settings.AutoPlayConfirmBranch && Engine.Current is { Kind:"choice" } && Engine.Mode==RunMode.Choice)
        {
            if(!PrepareAutoPlaybackEnvironment())return;
            var branchTarget=autoTarget; var branchKey=autoDisplay.RegionKey;
            if(branchTarget!=null && branchKey!=null && BeginConfirmedBranchWait(Engine,branchTarget,branchKey))return;
            PauseAutoPlayback("这处选择的对白或条件尚未核实，请按画面手动定位。");return;
        }
        if(Engine.Current is not { Kind:"line" } node){RequestOcr(true);return;}
        ShowAutoStartConfirmation(Engine,node);
    }
    void ShowAutoStartConfirmation(PlaybackEngine engine,Node source,Node? commonCandidate=null)
    {
        CancelAutoStartConfirmation();
        var foreground=GameAdvanceAccessibilityService.GetAvailability(AutoGamePackage);
        if(uiVisible||!overlay.CanShow||!foreground.CanTap)
        {PauseAutoPlayback(uiVisible?"请回到游戏，在悬浮控制中开始。":!overlay.CanShow?"请先允许显示悬浮控制。":foreground.Reason);return;}
        // 每次开始都冻结当下导航；共同线候选尚未进入，不把提示当作已经确认的位置。
        var node=commonCandidate??source;
        var confirmation=new AutoStartConfirmation(engine,source,ocrEpoch,playGeneration,
            JsonSerializer.Serialize(engine.ExportNavigation(),Json.Options),Screen.DisplayState,Screen.SessionId,Screen.CaptureActive,
            AutoGamePackage,foreground,commonCandidate);
        autoStartConfirmation=confirmation;
        // 与分支等待相同：只监听游戏前台/屏幕，不发送触碰；确认后的180ms延迟仍保留此票据。
        autoStartServiceToken=GameAdvanceAccessibilityService.BeginSession(confirmation.GamePackage,
            foreground.DisplayWidth,foreground.DisplayHeight,foreground.Rotation);
        if(autoStartServiceToken==0)
        {PauseAutoPlayback(GameAdvanceAccessibilityService.LastFailureReason);return;}
        autoStartWatchdog=()=>
        {
            if(!ReferenceEquals(autoStartConfirmation,confirmation))return;
            if(!AutoStartEnvironmentValid(confirmation))
            {PauseAutoPlayback("确认期间游戏、屏幕或位置已变化，请重新核对当前句再开始。");return;}
            if(autoStartWatchdog!=null)main.PostDelayed(autoStartWatchdog,400);
        };
        main.PostDelayed(autoStartWatchdog,400);
        Status=commonCandidate==null?"请核对游戏当前台词；从这句开始会重播当前句，不再重复 OCR。":
            "已到共同线，请核对提示中的台词后继续。";Notify();
        overlay.ShowAutoPlaybackStart((commonCandidate==null?"":"如果游戏当前显示下面这句台词，已进入共同线，可以继续自动播放。\n\n")+BrowseSectionTitle(engine,node.SectionId)+"\n\n"+
            (string.IsNullOrWhiteSpace(node.Speaker)?"旁白":node.Speaker)+"："+node.Text,
            // 系统先关闭悬浮确认框，再验证游戏焦点；旧窗口或重复确认不能重启当前会话。
            ()=>main.PostDelayed(()=>Post(()=>ConfirmAutoPlaybackStart(confirmation,false)),180),
            ()=>main.PostDelayed(()=>Post(()=>ConfirmAutoPlaybackStart(confirmation,true)),180),
            ()=>Post(()=>
            {
                if(!ReferenceEquals(autoStartConfirmation,confirmation))return;
                CancelAutoStartConfirmation();Status="已取消开始，尚未点击游戏。";Notify();
            }),isCommonReturn:commonCandidate!=null);
    }
    void CancelAutoStartConfirmation()
    {
        // 先清对象与所有者，再撤服务；同步取消事件不能抓到旧确认或影响下一轮。
        autoStartConfirmation=null;
        bool hadSession=autoStartServiceToken!=0;
        autoStartServiceToken=0;
        if(autoStartWatchdog!=null)main.RemoveCallbacks(autoStartWatchdog);
        autoStartWatchdog=null;
        overlay.ClearAutoStartConfirmation();
        if(hadSession)GameAdvanceAccessibilityService.CancelSession("自动播放起点确认已结束。");
    }
    bool AutoStartEnvironmentValid(AutoStartConfirmation confirmation)
    {
        var engine=confirmation.Engine;
        if(confirmation.Invalidated||autoStartServiceToken==0||!ReferenceEquals(Engine,engine)||
            !ReferenceEquals(engine.Current,confirmation.Node)||ocrEpoch!=confirmation.Epoch||
            playGeneration!=confirmation.AudioGeneration||
            JsonSerializer.Serialize(engine.ExportNavigation(),Json.Options)!=confirmation.Navigation||
            Screen.DisplayState!=confirmation.Display||Screen.SessionId!=confirmation.CaptureSession||
            Screen.CaptureActive!=confirmation.CaptureActive||AutoGamePackage!=confirmation.GamePackage||uiVisible||!overlay.CanShow)return false;
        var state=GameAdvanceAccessibilityService.GetAvailability(confirmation.GamePackage);
        return state.CanTap&&state.DisplayWidth==confirmation.Foreground.DisplayWidth&&
            state.DisplayHeight==confirmation.Foreground.DisplayHeight&&state.Rotation==confirmation.Foreground.Rotation;
    }
    void ConfirmAutoPlaybackStart(AutoStartConfirmation confirmation,bool locate)
    {
        if(!ReferenceEquals(autoStartConfirmation,confirmation))return;
        bool current=AutoStartEnvironmentValid(confirmation);
        CancelAutoStartConfirmation(); // 一次性消费等待票据，正常起播随后建立独立的播放票据。
        var engine=confirmation.Engine;
        if(!current)
        {Status="确认期间位置或屏幕已变化，请重新核对当前句再开始。";Notify();return;}
        if(locate){RequestOcr(true);return;}
        Invalidate();audio.Stop();
        var node=confirmation.CommonCandidate??confirmation.Node;
        if(confirmation.CommonCandidate!=null)
        {
            if(!ReferenceEquals(CommonLineAfterBranch(engine),node))
            {PauseAutoPlayback("共同线候选已变化，请重新核对游戏当前台词。");return;}
        }
        else
        {
            // 保留单句核对、未核实路线等限制；不能用重新定位来抹去这些限制。
            if(!engine.ConfirmCurrentPosition()){PauseAutoPlayback(engine.NavigationError);return;}
            if(!CanStartConfirmedAutoRoute(engine,out var reason)){PauseAutoPlayback(reason);return;}
        }
        if(!CanAutoPlayLine(engine.Pack,node))
        {PauseAutoPlayback("当前句没有可用配音，自动播放已暂停，尚未点击游戏。");return;}
        if(!PrepareAutoPlaybackEnvironment())return;
        autoPlay.Start(node.Id,SystemClock.ElapsedRealtime());
        if(!autoPlay.PreparePlayback(autoPlay.Epoch,node.Id,SystemClock.ElapsedRealtime()))
        {PauseAutoPlayback("起点确认已失效，请重新开始。");return;}
        if(confirmation.CommonCandidate==null&&!BindConfirmedAutoRoute(engine,engine,node))return;
        overlay.SetExpanded(false);overlay.SetCaptureHidden(false);
        StartAutoPlaybackTimer();
        SectionId=node.SectionId;
        Diagnostics.Log("自动播放人工起点",node.Id+"；不运行开局 OCR。");
        if(confirmation.CommonCandidate!=null)
        {
            if(!engine.ConfirmGameLine(node.Id)){PauseAutoPlayback(engine.NavigationError);return;}
        }
        else engine.Replay();
        ContentChanged?.Invoke();Notify();
    }
    bool PrepareAutoPlaybackEnvironment()
    {
        var state=GameAdvanceAccessibilityService.GetAvailability(AutoGamePackage);
        if(uiVisible||!state.CanTap){PauseAutoPlayback(uiVisible?"请回到游戏，在悬浮控制中开启自动播放。":state.Reason);return false;}
        if(!Screen.CaptureActive){PauseAutoPlayback("请先授权屏幕捕获，再开启自动播放。");return false;}
        var key=Screen.DisplayState.RegionKey;
        if(key==null||Settings.AdvanceTargetFor(key) is not { } target||!MatchesTarget(target,state))
        {PauseAutoPlayback("请先在游戏中点击“下一句区域”，设置此屏幕的触碰范围。");return false;}
        autoServiceToken=GameAdvanceAccessibilityService.BeginSession(AutoGamePackage,state.DisplayWidth,state.DisplayHeight,state.Rotation);
        if(autoServiceToken==0){PauseAutoPlayback(GameAdvanceAccessibilityService.LastFailureReason);return false;}
        autoTarget=target;autoDisplay=Screen.DisplayState;autoCaptureSession=Screen.SessionId;
        return true;
    }
    void StartAutoPlaybackTimer()
    {
        if(autoTimer!=null)main.RemoveCallbacks(autoTimer);
        long epoch=autoPlay.Epoch;
        autoTimer=()=>
        {
            if(!autoPlay.Running||autoPlay.Epoch!=epoch)return;
            if(!CheckAutoPlaybackEnvironment())return;
            if(autoPlay.TimedOut(SystemClock.ElapsedRealtime())){PauseAutoPlayback(AutoPlaybackTimeoutReason);return;}
            if(autoTimer!=null)main.PostDelayed(autoTimer,750);
        };
        main.PostDelayed(autoTimer,750);
    }
    bool MatchesTarget(AdvanceTapTarget target,GameAdvanceAvailability state)=>target.IsValid&&
        target.DisplayWidth==state.DisplayWidth&&target.DisplayHeight==state.DisplayHeight&&target.Rotation==state.Rotation;
    string AutoPlaybackTimeoutReason=>autoPlay.Phase==AutoPlaybackPhase.CheckingCurrent?
        "15 秒内未能确认当前台词，自动播放已暂停，尚未点击游戏。\n请等字幕完整显示后重试，或用 OCR 定位／台词页手动确认位置。":
        "等待播放启动或点击结果超时，已暂停；请核对游戏位置。";
    bool CheckAutoPlaybackEnvironment()
    {
        if(!autoPlay.Running)return false;
        if(autoPlay.TimedOut(SystemClock.ElapsedRealtime()))
        {PauseAutoPlayback(AutoPlaybackTimeoutReason);return false;}
        if(uiVisible||!Screen.CaptureActive||Screen.SessionId!=autoCaptureSession||Screen.DisplayState!=autoDisplay)
        {PauseAutoPlayback("游戏画面或屏幕授权已变化，请重新定位后开启自动播放。");return false;}
        var state=GameAdvanceAccessibilityService.GetAvailability(AutoGamePackage);
        if(!state.CanTap||autoTarget==null||!MatchesTarget(autoTarget,state))
        {PauseAutoPlayback(state.CanTap?"触碰区域对应的屏幕已变化，请重新设置。":state.Reason);return false;}
        return true;
    }
    void HandleAutoPlaybackObservation(PlaybackEngine engine,FollowObservation observation)
    {
        if(autoPlay.Phase!=AutoPlaybackPhase.CheckingCurrent||!CheckAutoPlaybackEnvironment())return;
        if(!observation.IsStable||observation.Sequence<=autoLastSequence)return;
        autoLastSequence=observation.Sequence;
        var blocks=observation.Blocks.Where(b=>double.IsFinite(b.Score)&&b.Score>=.80).ToList();
        var all=Matcher.FindAll(engine,SectionId,blocks);
        if(all.Any(c=>c.Node.Kind=="choice"&&c.Score>=.94))
        {PauseAutoPlayback("画面包含分支选项，请先在游戏中选择，再核对选后的台词开始自动播放。");return;}
        var exact=all.Where(c=>c.Node.Kind=="line"&&
            Matcher.Normalize(c.Evidence)==Matcher.Normalize(c.Node.Text)&&Matcher.Normalize(c.Node.Text).Length>=2&&
            (Matcher.Normalize(c.Node.Text).Length>3||blocks.Any(b=>!string.IsNullOrWhiteSpace(c.Node.Speaker)&&Matcher.Normalize(b.Text)==Matcher.Normalize(c.Node.Speaker))))
            .GroupBy(c=>c.Node.Id).Select(g=>g.First()).ToList();
        // 同文候选由人确认；距离加分不是识别置信度，不能直接使用排名第一项。
        if(exact.Count!=1){ResetAutoConsensus();Status="当前台词不够明确，等待完整字幕；也可停止后手动定位。";return;}
        var node=exact[0].Node;
        if(autoPendingNode==node.Id&&autoPendingFrame==observation.FrameKey)autoConsensus++;
        else{autoPendingNode=node.Id;autoPendingFrame=observation.FrameKey;autoConsensus=1;}
        if(autoConsensus<2){Status="已识别当前台词，正在核对起始位置。";return;}
        var probe=new PlaybackEngine(engine.Pack);
        if(!probe.ImportNavigation(engine.ExportNavigation())||!probe.ConfirmGameLine(node.Id))
        {PauseAutoPlayback("当前台词的路线尚不能确认，请按游戏画面手动核对。");return;}
        if(autoTarget is { } sourceTarget && autoDisplay.RegionKey is { } sourceKey &&
            TryWaitAtSourceChoicePreview(engine,probe,sourceTarget,sourceKey))return;
        if(!CanStartConfirmedAutoRoute(probe,out var routeReason))
        {PauseAutoPlayback(routeReason);return;}
        if(!CanAutoPlayLine(engine.Pack,node))
        {PauseAutoPlayback("当前句没有可用配音，自动播放已暂停。");return;}
        // 重定位前换代，废弃所有初始 OCR；真实引擎只在已武装播放阶段发声。
        autoPlay.Start(node.Id,SystemClock.ElapsedRealtime());
        long epoch=autoPlay.Epoch;
        if(!autoPlay.PreparePlayback(epoch,node.Id,SystemClock.ElapsedRealtime())){PauseAutoPlayback("定位状态已变化，请重新开始。");return;}
        if(!BindConfirmedAutoRoute(probe,engine,node))return;
        ocrEpoch++;ocrSchedule.Reset();
        if(!engine.ConfirmGameLine(node.Id)){PauseAutoPlayback(engine.NavigationError);return;}
        SectionId=node.SectionId;
        // Start换代后也更新唯一的环境检查定时器。
        if(autoTimer!=null)main.RemoveCallbacks(autoTimer);
        autoTimer=()=>
        {
            if(!autoPlay.Running||autoPlay.Epoch!=epoch)return;
            if(!CheckAutoPlaybackEnvironment())return;
            if(autoPlay.TimedOut(SystemClock.ElapsedRealtime())){PauseAutoPlayback("等待播放启动或点击结果超时，已暂停；不会重复补点。");return;}
            if(autoTimer!=null)main.PostDelayed(autoTimer,750);
        };
        main.PostDelayed(autoTimer,750);
        overlay.SetCaptureHidden(false);
        Diagnostics.Log("自动播放定位",node.Id);ContentChanged?.Invoke();Notify();
    }
    void OnAutoAudioCompleted(long ticket)
    {
        long epoch=autoPlay.Epoch;
        if(!autoPlay.CompleteAudio(epoch,ticket,SystemClock.ElapsedRealtime()))return;
        Diagnostics.Log("自动播放自然结束",$"node={autoPlay.NodeId}; audio={ticket}");
        AdvanceAfterAutoLine(epoch);
    }
    // 只将非空的纯标点正文当作停顿；不从角色名、音频状态或去标点后的空串推断。
    // 已声明音频却丢失文件时仍提示缺音，已有音频则正常播放。
    static bool IsSilentPunctuation(Node node)=>node.Kind=="line"&&string.IsNullOrWhiteSpace(node.Audio)&&
        !string.IsNullOrWhiteSpace(node.Text)&&node.Text.Any(char.IsPunctuation)&&
        node.Text.All(c=>char.IsWhiteSpace(c)||char.IsPunctuation(c));
    static bool CanAutoPlayLine(Pack pack,Node node)=>IsSilentPunctuation(node)||
        pack.ResolveAudio(node) is { } file&&File.Exists(file);
    void StartAutoSilentPause(PlaybackEngine owner,Node node,long request)
    {
        audio.Stop();
        if(!CheckAutoPlaybackEnvironment())return;
        long epoch=autoPlay.Epoch;
        if(owner.CurrentId!=node.Id||!AutoLinePermitted(owner)||
            !autoPlay.BeginSilentPause(epoch,node.Id,SystemClock.ElapsedRealtime()))
        {PauseAutoPlayback("当前停顿不属于已确认的播放路线，自动播放已暂停。");return;}
        Status="标点停顿 · 稍后自动继续";Notify();
        Diagnostics.Log("自动播放标点停顿",node.Id);
        if(autoSilentTimer!=null)main.RemoveCallbacks(autoSilentTimer);
        autoSilentTimer=()=>
        {
            if(autoPlay.Epoch!=epoch||request!=playGeneration||!ReferenceEquals(Engine,owner)||owner.CurrentId!=node.Id)return;
            if(!CheckAutoPlaybackEnvironment())return;
            if(!autoPlay.CompleteSilentPause(epoch,node.Id,SystemClock.ElapsedRealtime()))return;
            autoSilentTimer=null;
            AdvanceAfterAutoLine(epoch);
        };
        main.PostDelayed(autoSilentTimer,AutoPlaybackCycle.SilentPauseMilliseconds);
    }
    void AdvanceAfterAutoLine(long epoch)
    {
        if(!autoPlay.Running||autoPlay.Epoch!=epoch)return;
        if(!CheckAutoPlaybackEnvironment()||Engine is not { } engine||engine.CurrentId!=autoPlay.NodeId)return;
        if(TryAdvanceAutoIntoSourceChoice(engine,epoch))return;
        if(TryPlayPendingDefaultDisplayAnchor(engine,epoch))return;
        var decision=EvaluateAutoNext(engine);
        if(!decision.Allowed||decision.Next==null)
        {
            if(!AutomaticBranchRoutesEnabled&&!Settings.BranchAutoFollow&&decision.Code=="choice"&&engine.Current?.NextId is { } menuId&&
                autoTarget is { } branchTarget&&autoDisplay.RegionKey is { } branchKey&&
                TryWaitForCommonAfterBranch(engine,menuId,branchTarget,branchKey,true))return;
            if(TryAdvanceAutoIntoBranch(engine,epoch))return;
            string nextStep=decision.Code switch
            {
                "choice" or "branch" or "next-branch" => Settings.AutoPlayConfirmBranch ?
                    "此处选项或选后对白尚不能可靠确认；请按游戏当前台词手动定位。" : Settings.AutoPlayDefaultBranchEnabled ?
                    "此处无法使用所设默认分支，对应选项、正文或条件需要确认；请在游戏中继续，并手动核对配音路线。" : Settings.BranchAutoFollow ?
                    "请在游戏中手动继续，出现选项后在游戏和配音中选择同一分支；支线内请用点按跟随或手动播放。" :
                    "分支期间配音保持暂停；此处没有可靠的共同续接句，请自行继续游戏，到共同线后手动定位再恢复。",
                "end" => "本段已播放完毕。请手动继续游戏，确认新的共同剧情起点后再开启。",
                _ => "请在游戏中手动继续，并通过台词页或 OCR 定位核对新的起点。"
            };
            PauseAutoPlayback(decision.Reason+"\n本次尚未点击游戏下一句。\n"+nextStep);return;
        }
        var next=decision.Next;
        if(!CanAutoPlayLine(engine.Pack,next)){PauseAutoPlayback("下一句缺少配音，尚未点击游戏。\n请手动继续这句，或更新章节配音包；到有配音的已核台词后再定位开启。");return;}
        var target=autoTarget!;
        if(overlay.ContainsPoint(target.CenterX,target.CenterY)){PauseAutoPlayback("悬浮控制挡住了下一句区域，请挪开悬浮球后重新开始。");return;}
        if(!autoPlay.BeginTap(epoch,next.Id,SystemClock.ElapsedRealtime())){PauseAutoPlayback("点击请求已变化，请重新定位。");return;}
        long tapTicket=autoPlay.TapTicket;
        Diagnostics.Log("自动播放单击",$"from={engine.CurrentId}; next={next.Id}; ticket={tapTicket}");
        _=AdvanceOnceAsync(engine,epoch,tapTicket,next.Id,autoServiceToken,target);
    }
    async Task AdvanceOnceAsync(PlaybackEngine engine,long epoch,long tapTicket,string nextId,long token,AdvanceTapTarget target)
    {
        try
        {
            var result=await GameAdvanceAccessibilityService.TapAsync(token,target.CenterX,target.CenterY);
            Post(()=>
            {
                if(autoPlay.Epoch!=epoch||!autoPlay.Running)return;
                if(!result.Completed){PauseAutoPlayback(result.Reason+" 不会重复补点，请核对游戏位置。");return;}
                if(!ReferenceEquals(Engine,engine)||!CheckAutoPlaybackEnvironment())return;
                if(!autoPlay.GestureCompleted(epoch,tapTicket,nextId,SystemClock.ElapsedRealtime()))return;
                // 给游戏一次短暂的转场时间；不再运行 OCR，也不自动重试触碰。
                main.PostDelayed(()=>Post(()=>
                {
                    if(autoPlay.Epoch!=epoch||!autoPlay.Running)return;
                    if(!CheckAutoPlaybackEnvironment())return;
                    var gate=EvaluateAutoNext(engine);
                    if(!gate.Allowed||gate.Next?.Id!=nextId||!autoPlay.PreparePlayback(epoch,nextId,SystemClock.ElapsedRealtime()))
                    {PauseAutoPlayback("剧情路线已变化，自动播放已暂停。");return;}
                    if(!AdvanceAutoLine(engine,nextId)){PauseAutoPlayback("无法安全推进到下一句，请重新定位。");return;}
                    Diagnostics.Advance();Diagnostics.Log("自动播放下一句",nextId);Notify();
                }),450);
            });
        }
        catch(Exception ex){Post(()=>{if(autoPlay.Epoch==epoch&&autoPlay.Running)PauseAutoPlayback("点击未完成："+ex.Message);});}
    }
    void SelectAdvanceRegion()
    {
        Invalidate();audio.Stop();Engine?.PauseForBrowse();
        var state=GameAdvanceAccessibilityService.GetForegroundAvailability();
        if(uiVisible||!state.CanTap){PauseAutoPlayback(uiVisible?"请在游戏前台设置下一句区域。":state.Reason);return;}
        var displayKey=state.DisplayKey;
        if(displayKey==null){PauseAutoPlayback("暂时无法确认屏幕，请回到游戏后重试。");return;}
        AutoGamePackage=state.ForegroundPackage??"";
        string selectedPackage=AutoGamePackage;
        long selectionEpoch=autoPlay.Epoch;
        overlay.Hide();
        advanceSelector=new(context,state.DisplayWidth,state.DisplayHeight,region=>Post(()=>
        {
            // 已取消的旧框选不能清除新窗口、覆盖新客户端状态或保存旧区域。
            if(AutoGamePackage!=selectedPackage||autoPlay.Epoch!=selectionEpoch)return;
            advanceSelector=null;overlay.Show();
            var current=GameAdvanceAccessibilityService.GetAvailability(AutoGamePackage);
            if(region==null){Status="已取消设置触碰区域。";Notify();return;}
            if(AutoGamePackage!=selectedPackage||autoPlay.Epoch!=selectionEpoch||!current.CanTap||current.DisplayKey!=displayKey||
                current.DisplayWidth!=state.DisplayWidth||current.DisplayHeight!=state.DisplayHeight||current.Rotation!=state.Rotation)
            {PauseAutoPlayback("设置期间游戏或屏幕已变化，请重新框选触碰范围。");return;}
            var r=region;
            Settings.SetAdvanceTarget(displayKey,new("",state.DisplayWidth,state.DisplayHeight,state.Rotation,r.Left,r.Top,r.Width,r.Height));
            SaveSettings();Status="区域已保存，自动播放和点按跟随共用。开启分支辅助后，确认分支会自动启用点按跟随，不必重新框选。";Notify();overlay.ShowNotice(Status);
        }));
        try{advanceSelector.Show();}
        catch{advanceSelector.Dispose();advanceSelector=null;overlay.Show();throw;}
    }
}
