using Android.App;
using Android.Content;

namespace PgrVoice.AndroidApp.Contracts;

/// <summary>屏幕授权与画面输入边界。UI 负责发起系统授权，输入实现不决定剧情如何推进。</summary>
public interface IScreenInput : IDisposable
{
    bool CaptureActive { get; }
    long SessionId { get; }
    ScreenDisplayState DisplayState { get; }
    /// <summary>只订阅一次；每个采样的唯一消费者负责 Dispose。静态画面可重复采样同一SourceFrameId，不能据此认定屏幕更新。</summary>
    event Action<CapturedFrame>? FrameAvailable;
    event Action<string>? CaptureStopped;
    event Action<ScreenDisplayState>? DisplayChanged;
    void StartCapture(Result resultCode, Intent resultData);
    void Stop();
}
