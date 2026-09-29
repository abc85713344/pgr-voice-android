using Android.Graphics;

namespace PgrVoice.AndroidApp.Contracts;

/// <summary>A sample of the latest authorized screen pixels. The single subscriber owns and disposes it.</summary>
public sealed class CapturedFrame : IDisposable
{
    public Bitmap Bitmap { get; }
    /// <summary>Time the source pixels were acquired. Repeated samples keep this timestamp.</summary>
    public long TimestampMs { get; }
    /// <summary>Time this sample was delivered; it does not imply a new native screen image.</summary>
    public long SampledAtMs { get; }
    public long SourceFrameId { get; }
    public bool IsRepeatedSample { get; }
    public long SessionId { get; }
    public ScreenDisplayState DisplayState { get; }
    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;
    private int disposed;

    public CapturedFrame(Bitmap bitmap, long timestampMs, long sessionId, ScreenDisplayState displayState,
        long sourceFrameId = 0, bool isRepeatedSample = false, long? sampledAtMs = null)
    {
        Bitmap = bitmap;
        TimestampMs = timestampMs;
        SampledAtMs = sampledAtMs ?? timestampMs;
        SourceFrameId = sourceFrameId;
        IsRepeatedSample = isRepeatedSample;
        SessionId = sessionId;
        DisplayState = displayState;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) Bitmap.Dispose();
    }
}
