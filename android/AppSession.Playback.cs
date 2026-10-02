using Android.OS;
using PgrVoice.AndroidApp.Platform;

namespace PgrVoice.AndroidApp;

public sealed partial class AppSession
{
    void PlayNode(PlaybackEngine owner,Node? node)
    {
        if(!ReferenceEquals(owner,Engine)||node==null)return;
        long request=++playGeneration;
        if(autoPlay.Running&&IsSilentPunctuation(node)){StartAutoSilentPause(owner,node,request);return;}
        string? path=owner.Pack.ResolveAudio(node);
        if(path==null||!File.Exists(path)){audio.Stop();if(autoPlay.Running)PauseAutoPlayback("这一句没有可用配音，自动播放已暂停。");else{Status=owner.Pack.AudioNotice(node);Notify();}return;}
        VoiceForegroundService.EnsureStarted(context,()=>
        {
            if(request!=playGeneration||!ReferenceEquals(Engine,owner))return;
            if(autoPlay.Running&&!CheckAutoPlaybackEnvironment())return;
            if(autoPlay.Running && (autoPlay.Phase!=AutoPlaybackPhase.Playing||autoPlay.NodeId!=node.Id||!owner.EvaluateCommonAutoPlayNext().CurrentIsCommon))
            {PauseAutoPlayback("当前播放不属于已确认的共同线，自动播放已暂停。");return;}
            long ticket=audio.PlayTagged(path);
            if(autoPlay.Running&&!autoPlay.BindAudio(autoPlay.Epoch,node.Id,ticket,SystemClock.ElapsedRealtime()))PauseAutoPlayback("播放请求已变化或等待超时，请重新确认自动播放位置。");
        });
        if(request!=playGeneration)return;
        Status=string.IsNullOrWhiteSpace(node.Speaker)?"播放配音":node.Speaker+" · 播放配音";
    }
}
