using PgrVoice.AndroidApp.Contracts;

namespace PgrVoice.AndroidApp;

/// <summary>独立单句试听：不会触碰导航引擎；旧回调、失败、静音、暂停均不能记成完成。</summary>
public sealed class OnboardingPreview : IDisposable
{
    readonly IAudioOutput output;
    readonly Func<float> volume;
    readonly Func<bool> mediaAudible;
    readonly Func<string, bool> fileExists;
    long ticket;
    bool active, disposed;
    string? file;
    public event Action? Changed;
    public bool IsPlaying => active;
    public bool Succeeded { get; private set; }
    public string Message { get; private set; } = "点“试听这一句”，听完后再确认。";
    public bool CanAcknowledge => !disposed && Succeeded && file != null && fileExists(file) && volume() > 0 && mediaAudible();
    public OnboardingPreview(IAudioOutput output, Func<float> volume, Func<bool> mediaAudible, Func<string, bool>? fileExists = null)
    {
        this.output = output; this.volume = volume; this.mediaAudible = mediaAudible; this.fileExists = fileExists ?? File.Exists;
        output.PlaybackCompleted += Completed; output.Error += Failed; output.Interrupted += Failed;
    }
    public void Start(string? path)
    {
        if (disposed) return;
        Stop(); file = path;
        if (path == null || !fileExists(path)) { Failed("这一句音频文件不存在，请换一句或重新导入本章。"); return; }
        if (volume() <= 0) { Failed("配音总音量或这位角色的音量为 0，请调高后重试。"); return; }
        if (!mediaAudible()) { Failed("手机媒体音量为 0，请调高后重试。"); return; }
        active = true; Message = "正在试听这一句，播完后请确认是否听到。";
        output.Strategy = AudioStrategy.Listening; output.SetVolume(volume());
        try { long next = output.PlayTagged(path); if (active) ticket = next; }
        catch (Exception ex) { Failed("试听失败：" + ex.Message); }
        Changed?.Invoke();
    }
    void Completed(long completed)
    {
        if (disposed || !active || ticket == 0 || ticket != completed) return;
        active = false; ticket = 0;
        Succeeded = file != null && fileExists(file) && volume() > 0 && mediaAudible();
        Message = Succeeded ? "这一句已播放完。你听到声音了吗？" : "音量或文件状态有变化，请检查后再试听。";
        Changed?.Invoke();
    }
    void Failed(string reason)
    {
        if (disposed) return;
        active = false; ticket = 0; Succeeded = false; output.Stop(); Message = reason; Changed?.Invoke();
    }
    public void Stop()
    {
        active = false; ticket = 0; Succeeded = false; output.Stop();
        Message = "点“试听这一句”，听完后再确认。";
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Stop(); output.PlaybackCompleted -= Completed; output.Error -= Failed; output.Interrupted -= Failed; output.Dispose();
    }
}
