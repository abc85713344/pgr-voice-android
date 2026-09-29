namespace PgrVoice.AndroidApp.Contracts;

public enum AudioStrategy { Simultaneous, VoicePriority, Listening }

/// <summary>只播放当前音频，不隐式推进剧情；由会话层处理完成、错误与系统中断。</summary>
public interface IAudioOutput : IDisposable
{
    AudioStrategy Strategy { get; set; }
    bool Playing { get; }
    event Action? Completed;
    /// <summary>仅在对应的播放请求自然结束后通知；暂停、停止、错误和替换请求均不会通知。</summary>
    event Action<long>? PlaybackCompleted;
    event Action<string>? Error;
    event Action<string>? Interrupted;
    void Play(string file);
    /// <summary>返回本次播放请求的唯一标识；调用方仍须绑定章节、节点和自动播放代次。</summary>
    long PlayTagged(string file);
    void Stop();
    void Pause();
    void SetVolume(float value);
}
