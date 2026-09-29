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
    sealed record AutoStartConfirmation(PlaybackEngine Engine,Node Node,long Epoch,long AudioGeneration,
        string Navigation,ScreenDisplayState Display,long CaptureSession);
    AutoStartConfirmation? autoStartConfirmation;
    AdvanceRegionOverlay? advanceSelector;
    public bool AutoPlaybackRunning=>autoPlay.Running;
    public string AutoPlaybackPhaseName=>autoPlay.Phase.ToString();
    bool AutoNeedsConsensus=>autoPlay.Phase==AutoPlaybackPhase.CheckingCurrent;
    string AutoGamePackage
    {
        get
        {
#if DEBUG
            if(Settings.DebugAutoPlayPackage=="cn.pgrvoice.autoplayfixture")return Settings.DebugAutoPlayPackage;
#endif
            return GameAdvanceAccessibilityService.OfficialGamePackage;
        }
    }
    public void StopAutoPlayback()=>PauseAutoPlayback("自动播放已停止，请核对游戏位置后再开始。",false);
    void PauseAutoPlaybackForOverlayExit(string reason)
    {
        // 确认窗口已关闭、延迟起播尚未执行时也必须撤销，不能只检查 Running。
        if(autoPlay.Running||autoStartConfirmation!=null)PauseAutoPlayback(reason,false);
    }
    void ResetAutoConsensus(){autoPendingNode=autoPendingFrame=null;autoConsensus=0;autoLastSequence=-1;}
    void CancelAutoPlayback()
    {
        autoStartConfirmation=null;
        bool wasRunning=autoPlay.Running||autoServiceToken!=0;
        autoCaptureControlGeneration=autoCaptureControlEpoch=0;
        autoPlay.Stop();
        if(autoTimer!=null){main.RemoveCallbacks(autoTimer);autoTimer=null;}
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
        if(Engine.Current is not { Kind:"line" } node){RequestOcr(true);return;}
        // 每次开始都请用户核对当下画面，不把已恢复的存档或上次确认当作新的授权。
        var engine=Engine;
        var confirmation=new AutoStartConfirmation(engine,node,ocrEpoch,playGeneration,
            JsonSerializer.Serialize(engine.ExportNavigation(),Json.Options),Screen.DisplayState,Screen.SessionId);
        autoStartConfirmation=confirmation;
        Status="请核对游戏当前台词；从这句开始会重播当前句，不再重复 OCR。";Notify();
        overlay.ShowAutoPlaybackStart(BrowseSectionTitle(engine,node.SectionId)+"\n\n"+
            (string.IsNullOrWhiteSpace(node.Speaker)?"旁白":node.Speaker)+"："+node.Text,
            // 系统先关闭悬浮确认框，再验证游戏焦点；旧窗口或重复确认不能重启当前会话。
            ()=>main.PostDelayed(()=>Post(()=>ConfirmAutoPlaybackStart(confirmation,false)),180),
            ()=>main.PostDelayed(()=>Post(()=>ConfirmAutoPlaybackStart(confirmation,true)),180),
            ()=>Post(()=>
            {
                if(!ReferenceEquals(autoStartConfirmation,confirmation))return;
                autoStartConfirmation=null;Status="已取消开始，尚未点击游戏。";Notify();
            }));
    }
    void ConfirmAutoPlaybackStart(AutoStartConfirmation confirmation,bool locate)
    {
        if(!ReferenceEquals(autoStartConfirmation,confirmation))return;
        autoStartConfirmation=null; // 一次性消费，重复回调不产生任何新播放或点击。
        var engine=confirmation.Engine;
        if(!ReferenceEquals(Engine,engine)||!ReferenceEquals(engine.Current,confirmation.Node)||
            ocrEpoch!=confirmation.Epoch||playGeneration!=confirmation.AudioGeneration||
            JsonSerializer.Serialize(engine.ExportNavigation(),Json.Options)!=confirmation.Navigation||
            Screen.DisplayState!=confirmation.Display||Screen.SessionId!=confirmation.CaptureSession||uiVisible)
        {Status="确认期间位置或屏幕已变化，请重新核对当前句再开始。";Notify();return;}
        if(locate){RequestOcr(true);return;}
        Invalidate();audio.Stop();
        // 保留单句核对、未核实路线等限制；不能用重新定位来抹去这些限制。
        if(!engine.ConfirmCurrentPosition()){PauseAutoPlayback(engine.NavigationError);return;}
        var decision=engine.EvaluateCommonAutoPlayNext();
        if(!decision.CurrentIsCommon){PauseAutoPlayback(decision.Reason);return;}
        var node=confirmation.Node;
        if(engine.Pack.ResolveAudio(node) is not { } file||!File.Exists(file))
        {PauseAutoPlayback("当前句没有可用配音，自动播放已暂停，尚未点击游戏。");return;}
        if(!PrepareAutoPlaybackEnvironment())return;
        autoPlay.Start(node.Id,SystemClock.ElapsedRealtime());
        if(!autoPlay.PreparePlayback(autoPlay.Epoch,node.Id,SystemClock.ElapsedRealtime()))
        {PauseAutoPlayback("起点确认已失效，请重新开始。");return;}
        overlay.SetExpanded(false);overlay.SetCaptureHidden(false);
        StartAutoPlaybackTimer();
        SectionId=node.SectionId;
        Diagnostics.Log("自动播放人工起点",node.Id+"；不运行开局 OCR。");
        engine.Replay();ContentChanged?.Invoke();Notify();
    }
    bool PrepareAutoPlaybackEnvironment()
    {
        var state=GameAdvanceAccessibilityService.GetAvailability(AutoGamePackage);
        if(uiVisible||!state.CanTap){PauseAutoPlayback(uiVisible?"请回到游戏，在悬浮控制中开启自动播放。":state.Reason);return false;}
        if(!Screen.CaptureActive){PauseAutoPlayback("请先授权屏幕捕获，再开启共同线自动播放。");return false;}
        var key=Screen.DisplayState.RegionKey;
        if(key==null||!Settings.AdvanceTargets.TryGetValue(key,out var target)||!MatchesTarget(target,state))
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
        target.GamePackage==AutoGamePackage&&target.DisplayWidth==state.DisplayWidth&&target.DisplayHeight==state.DisplayHeight&&target.Rotation==state.Rotation;
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
        {PauseAutoPlayback("画面包含分支选项，请先手动选择；分支线内不能自动播放。");return;}
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
        if(!probe.ImportNavigation(engine.ExportNavigation())||!probe.ConfirmGameLine(node.Id)||!probe.EvaluateCommonAutoPlayNext().CurrentIsCommon)
        {PauseAutoPlayback("当前是分支线或尚未核实的段落，不能自动播放；回到共同线后可重新开启。");return;}
        if(engine.Pack.ResolveAudio(node) is not { } file||!File.Exists(file))
        {PauseAutoPlayback("当前句没有可用配音，自动播放已暂停。");return;}
        // 重定位前换代，废弃所有初始 OCR；真实引擎只在已武装播放阶段发声。
        autoPlay.Start(node.Id,SystemClock.ElapsedRealtime());
        long epoch=autoPlay.Epoch;
        if(!autoPlay.PreparePlayback(epoch,node.Id,SystemClock.ElapsedRealtime())){PauseAutoPlayback("定位状态已变化，请重新开始。");return;}
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
        if(!CheckAutoPlaybackEnvironment()||Engine is not { } engine||engine.CurrentId!=autoPlay.NodeId)return;
        var decision=engine.EvaluateCommonAutoPlayNext();
        if(!decision.Allowed||decision.Next==null)
        {
            string nextStep=decision.Code switch
            {
                "choice" or "branch" or "next-branch" => "请在游戏中手动继续，出现选项后在游戏和配音中选择同一分支；支线内请用点按跟随或手动播放。",
                "end" => "本段已播放完毕。请手动继续游戏，确认新的共同剧情起点后再开启。",
                _ => "请在游戏中手动继续，并通过台词页或 OCR 定位核对新的起点。"
            };
            PauseAutoPlayback(decision.Reason+"\n本次尚未点击游戏下一句。\n"+nextStep);return;
        }
        var next=decision.Next;
        if(engine.Pack.ResolveAudio(next) is not { } file||!File.Exists(file)){PauseAutoPlayback("下一句缺少配音，尚未点击游戏。\n请手动继续这句，或更新章节配音包；到有配音的共同剧情后再定位开启。");return;}
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
                    var gate=engine.EvaluateCommonAutoPlayNext();
                    if(!gate.Allowed||gate.Next?.Id!=nextId||!autoPlay.PreparePlayback(epoch,nextId,SystemClock.ElapsedRealtime()))
                    {PauseAutoPlayback("剧情路线已变化，自动播放已暂停。");return;}
                    if(!engine.TryAdvanceAutomatically(nextId)){PauseAutoPlayback("无法安全推进到下一句，请重新定位。");return;}
                    Diagnostics.Advance();Diagnostics.Log("自动播放下一句",nextId);Notify();
                }),450);
            });
        }
        catch(Exception ex){Post(()=>{if(autoPlay.Epoch==epoch&&autoPlay.Running)PauseAutoPlayback("点击未完成："+ex.Message);});}
    }
    void SelectAdvanceRegion()
    {
        Invalidate();audio.Stop();Engine?.PauseForBrowse();
        var state=GameAdvanceAccessibilityService.GetAvailability(AutoGamePackage);
        if(uiVisible||!state.CanTap){PauseAutoPlayback(uiVisible?"请在游戏前台设置下一句区域。":state.Reason);return;}
        var displayKey=state.DisplayKey;
        if(displayKey==null){PauseAutoPlayback("暂时无法确认屏幕，请回到游戏后重试。");return;}
        overlay.Hide();
        advanceSelector=new(context,state.DisplayWidth,state.DisplayHeight,region=>Post(()=>
        {
            advanceSelector=null;overlay.Show();
            var current=GameAdvanceAccessibilityService.GetAvailability(AutoGamePackage);
            if(region==null){Status="已取消设置触碰区域。";Notify();return;}
            if(!current.CanTap||current.DisplayKey!=displayKey||
                current.DisplayWidth!=state.DisplayWidth||current.DisplayHeight!=state.DisplayHeight||current.Rotation!=state.Rotation)
            {PauseAutoPlayback("设置期间游戏或屏幕已变化，请重新框选触碰范围。");return;}
            var r=region;
            Settings.AdvanceTargets[displayKey]=new(AutoGamePackage,state.DisplayWidth,state.DisplayHeight,state.Rotation,r.Left,r.Top,r.Width,r.Height);
            SaveSettings();Status="区域已保存。人工对齐当前句后开“点按跟随”；共同线连播可选“自动播放”。";Notify();overlay.ShowNotice(Status);
        }));
        try{advanceSelector.Show();}
        catch{advanceSelector.Dispose();advanceSelector=null;overlay.Show();throw;}
    }
}
