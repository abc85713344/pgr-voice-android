using Android.Content;
using Android.Graphics;
using IOnCompleteListener = global::Android.Gms.Tasks.IOnCompleteListener;
using Android.Runtime;
using System.Diagnostics;
using Xamarin.Google.MLKit.Vision.Common;
using Xamarin.Google.MLKit.Vision.Text;
using Xamarin.Google.MLKit.Vision.Text.Chinese;
using NetTask = System.Threading.Tasks.Task;
using MlText = Xamarin.Google.MLKit.Vision.Text.Text;

namespace PgrVoice.AndroidApp.Ocr;

/// <summary>随 APK 提供中文模型的 ML Kit 对照引擎，不使用需要下载模型的 Play Services 版本。</summary>
public sealed class MlKitEngine : IOcrEngine
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITextRecognizer? _recognizer;
    private Java.Util.Concurrent.IExecutorService? _completionExecutor;
    private bool _disposed;
    public string Name => "ML Kit 中文（离线对照）";

    public MlKitEngine(Context context) { ArgumentNullException.ThrowIfNull(context); }

    public async NetTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { InitializeCore(); }
        finally { _gate.Release(); }
    }

    private void InitializeCore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_recognizer is not null) return;
        using var builder = new ChineseTextRecognizerOptions.Builder();
        using var options = builder.Build();
        _recognizer = TextRecognition.GetClient(options);
        _completionExecutor = Java.Util.Concurrent.Executors.NewSingleThreadExecutor();
    }

    public async Task<OcrResult> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InitializeCore();
            cancellationToken.ThrowIfCancellationRequested();
            if (bitmap.IsRecycled) throw new ArgumentException("OCR 截图已失效。", nameof(bitmap));
            var stopwatch = Stopwatch.StartNew();
            using var input = InputImage.FromBitmap(bitmap, 0);
            using var listener = new CompletionListener();
            using var task = _recognizer!.Process(input);
            // 回调不依赖主线程，避免 Activity 释放引擎时同步等待与主线程回调相互阻塞。
            task.AddOnCompleteListener(_completionExecutor!, listener);
            // ML Kit 原生 task 不可取消。等其完成后再丢弃结果，保证仍在处理的 Bitmap 不被提前释放。
            using var nativeResult = await listener.Completion.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            using var recognized = nativeResult.JavaCast<MlText>();
            var blocks = new List<OcrTextBlock>();
            foreach (var block in recognized.TextBlocks)
            foreach (var line in block.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.Text)) continue;
                var bounds = line.BoundingBox;
                if (bounds is null) continue;
                var points = line.GetCornerPoints();
                var polygon = points is { Length: >= 4 }
                    ? points.Select(p => new OcrPoint(p.X, p.Y)).ToArray()
                    : new[] { new OcrPoint(bounds.Left, bounds.Top), new OcrPoint(bounds.Right, bounds.Top), new OcrPoint(bounds.Right, bounds.Bottom), new OcrPoint(bounds.Left, bounds.Bottom) };
                // ML Kit 在不提供置信度时给 0。保留 0，不能伪造为满分用于自动推进。
                var confidence = float.IsFinite(line.Confidence) ? Math.Clamp(line.Confidence, 0, 1) : 0;
                blocks.Add(new OcrTextBlock(line.Text.Trim(), confidence, polygon));
            }
            return new OcrResult(blocks, stopwatch.Elapsed);
        }
        finally { _gate.Release(); }
    }

    private sealed class CompletionListener : Java.Lang.Object, IOnCompleteListener
    {
        private readonly TaskCompletionSource<Java.Lang.Object> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Java.Lang.Object> Completion => _completion.Task;
        public void OnComplete(global::Android.Gms.Tasks.Task task)
        {
            if (task.IsCanceled) _completion.TrySetCanceled();
            else if (!task.IsSuccessful) _completion.TrySetException(new InvalidOperationException(task.Exception?.Message ?? "ML Kit 中文识别失败。"));
            else if (task.Result is { } result) _completion.TrySetResult(result);
            else _completion.TrySetException(new InvalidDataException("ML Kit 未返回识别结果。"));
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _recognizer?.Close();
            _recognizer?.Dispose();
            _recognizer = null;
            _completionExecutor?.Shutdown();
            _completionExecutor?.Dispose();
            _completionExecutor = null;
        }
        finally { _gate.Release(); }
    }
}
