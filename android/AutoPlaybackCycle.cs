namespace PgrVoice.AndroidApp;

/// <summary>首次确认位置后按共同剧情顺序播放；每次自然完成最多授权一个手势。</summary>
public enum AutoPlaybackPhase { Off, CheckingCurrent, Playing, Tapping, StartingNext }

public sealed class AutoPlaybackCycle
{
    public AutoPlaybackPhase Phase { get; private set; }
    public bool Running => Phase != AutoPlaybackPhase.Off;
    public long Epoch { get; private set; }
    public string NodeId { get; private set; } = "";
    public string? ExpectedNext { get; private set; }
    public long AudioTicket { get; private set; }
    public long TapTicket { get; private set; }
    public long Deadline { get; private set; }
    long nextTapTicket;
    long lastAudioTicket;
    public void Start(string nodeId, long now)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(nodeId)) return;
        NodeId=nodeId; Phase=AutoPlaybackPhase.CheckingCurrent; Deadline=now+15_000;
    }
    public void Stop()
    {
        Epoch++; Phase=AutoPlaybackPhase.Off; NodeId=""; ExpectedNext=null;
        AudioTicket=0; TapTicket=0; Deadline=0; lastAudioTicket=0;
    }
    public bool PreparePlayback(long epoch,string nodeId,long now)
    {
        if (epoch != Epoch || string.IsNullOrWhiteSpace(nodeId) || TimedOut(now)) return false;
        if (Phase==AutoPlaybackPhase.CheckingCurrent && nodeId==NodeId || Phase==AutoPlaybackPhase.StartingNext && nodeId==ExpectedNext)
        {
            // Preparing the node does not prove that the foreground service has returned a
            // usable audio request. Keep a bounded wait until BindAudio succeeds.
            NodeId=nodeId; ExpectedNext=null; AudioTicket=0; TapTicket=0; Deadline=now+10_000;
            Phase=AutoPlaybackPhase.Playing; return true;
        }
        return false;
    }
    // AndroidAudioPlayer.PlayTagged 的票据单调递增；同一次会话不能重新绑定已经用过的音频票据。
    public bool BindAudio(long epoch,string nodeId,long ticket,long now)
    {
        if(epoch!=Epoch||Phase!=AutoPlaybackPhase.Playing||NodeId!=nodeId||ticket<=lastAudioTicket||AudioTicket!=0||TimedOut(now))return false;
        AudioTicket=ticket;lastAudioTicket=ticket;Deadline=0;return true;
    }
    public bool CompleteAudio(long epoch,long ticket,long now)
    {
        if(epoch!=Epoch||Phase!=AutoPlaybackPhase.Playing||ticket<=0||ticket!=AudioTicket)return false;
        AudioTicket=0;TapTicket=0;ExpectedNext=null;Phase=AutoPlaybackPhase.Tapping;Deadline=now+4_000;return true;
    }
    public bool BeginTap(long epoch,string expectedNext,long now)
    {
        if(epoch!=Epoch||Phase!=AutoPlaybackPhase.Tapping||TapTicket!=0||TimedOut(now)||
            string.IsNullOrWhiteSpace(expectedNext)||expectedNext==NodeId)return false;
        ExpectedNext=expectedNext;TapTicket=++nextTapTicket;Deadline=now+4_000;return true;
    }
    public bool GestureCompleted(long epoch,long tapTicket,string expectedNext,long now)
    {
        if(epoch!=Epoch||Phase!=AutoPlaybackPhase.Tapping||tapTicket<=0||tapTicket!=TapTicket||
            ExpectedNext!=expectedNext||TimedOut(now))return false;
        Phase=AutoPlaybackPhase.StartingNext;Deadline=now+10_000;return true;
    }
    public bool TimedOut(long now)=>Running&&Deadline>0&&now>=Deadline;
}
