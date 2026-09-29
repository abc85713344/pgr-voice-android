using Android.App;
using Android.Content;
using PgrVoice.AndroidApp.Contracts;

namespace PgrVoice.AndroidApp.Platform;

public sealed class AndroidScreenInput : IScreenInput
{
    private readonly Context context;
    private volatile bool disposed;
    public bool CaptureActive => !disposed && VoiceForegroundService.CaptureActive;
    public long SessionId => disposed ? 0 : VoiceForegroundService.CaptureSessionId;
    public ScreenDisplayState DisplayState => disposed ? ScreenDisplayState.Unavailable : VoiceForegroundService.CurrentDisplayState;
    public event Action<CapturedFrame>? FrameAvailable;
    public event Action<string>? CaptureStopped;
    public event Action<ScreenDisplayState>? DisplayChanged;

    public AndroidScreenInput(Context context)
    {
        this.context = context.ApplicationContext!;
        VoiceForegroundService.FrameAvailable += ReceiveFrame;
        VoiceForegroundService.CaptureStopped += CaptureEnded;
        VoiceForegroundService.DisplayChanged += DisplayUpdated;
    }
    public void StartCapture(Result resultCode, Intent resultData)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        VoiceForegroundService.StartCapture(context, resultCode, resultData);
    }
    public void Stop()
    {
        if (!disposed) VoiceForegroundService.StopCapture(context);
    }
    private void ReceiveFrame(CapturedFrame frame)
    {
        var receiver = FrameAvailable;
        if (disposed || receiver == null) { frame.Dispose(); return; }
        receiver(frame);
    }
    private void CaptureEnded(string reason) { if (!disposed) CaptureStopped?.Invoke(reason); }
    private void DisplayUpdated(ScreenDisplayState state) { if (!disposed) DisplayChanged?.Invoke(state); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        VoiceForegroundService.FrameAvailable -= ReceiveFrame;
        VoiceForegroundService.CaptureStopped -= CaptureEnded;
        VoiceForegroundService.DisplayChanged -= DisplayUpdated;
        VoiceForegroundService.StopCapture(context);
        FrameAvailable = null;
        CaptureStopped = null;
        DisplayChanged = null;
    }
}
