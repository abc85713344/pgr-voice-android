using Android.Graphics;

namespace PgrVoice.AndroidApp.Ocr;

public interface IOcrEngine : IDisposable
{
    string Name { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    /// <summary>调用方在任务完成前保留 Bitmap。引擎不保存截图，也不释放调用方 Bitmap。</summary>
    Task<OcrResult> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default);
}
