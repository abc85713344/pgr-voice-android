using Android.Content;
using Android.Graphics;

namespace PgrVoice.AndroidApp.Ocr;

/// <summary>串行初始化、切换与识别；切换时先释放旧引擎，任何时候只加载一个 OCR 引擎。</summary>
public sealed class OcrEngineManager : IDisposable
{
    private readonly Context _context;
    private readonly OcrWorkLifetime _lifetime = new();
    private IOcrEngine? _engine;
    private OcrEngineKind _kind = OcrEngineKind.Paddle;
    private bool _disposed;
    public OcrEngineKind Kind => _kind;
    public string Name => _engine?.Name ?? (_kind == OcrEngineKind.Paddle ? "PP-OCRv5 mobile（离线）" : "ML Kit 中文（离线对照）");

    public OcrEngineManager(Context context) => _context = context.ApplicationContext ?? context;

    public long CreateRequest()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _lifetime.CreateRequest();
    }

    public Task<OcrResult> RecognizeAsync(OcrEngineKind kind, Bitmap bitmap, long request, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        return _lifetime.RunAsync(request, async () =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_kind != kind) { _engine?.Dispose(); _engine = null; _kind = kind; }
            await GetOrInitializeEngineAsync(cancellationToken).ConfigureAwait(false);
        }, () => _engine!.RecognizeAsync(bitmap, cancellationToken), cancellationToken);
    }

    /// <summary>后台长期暂停时可主动卸载模型；之后按当前选择重新初始化。</summary>
    public Task UnloadAsync(CancellationToken cancellationToken = default) =>
        _lifetime.StopAsync(() => { _engine?.Dispose(); _engine = null; }, cancellationToken);

    private async Task GetOrInitializeEngineAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_engine is not null) return;
        var engine = _kind == OcrEngineKind.Paddle ? (IOcrEngine)new PaddleOcrEngine(_context) : new MlKitEngine(_context);
        try
        {
            await engine.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _engine = engine;
        }
        catch { engine.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnloadAsync().GetAwaiter().GetResult();
    }
}
