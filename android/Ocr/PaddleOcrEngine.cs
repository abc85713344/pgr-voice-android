using Android.Content;
using Android.Graphics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Diagnostics;
using System.Security.Cryptography;
using IOPath = System.IO.Path;

namespace PgrVoice.AndroidApp.Ocr;

/// <summary>完整本地 PP-OCRv5 mobile：DB 检测、旋转框、透视裁切、逐行识别、CTC 解码。</summary>
public sealed class PaddleOcrEngine : IOcrEngine
{
    private const string DetectorFile = "ch_PP-OCRv5_mobile_det.onnx";
    private const string RecognizerFile = "ch_PP-OCRv5_rec_mobile_infer.onnx";
    private const string DetectorHash = "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae";
    private const string RecognizerHash = "5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5";
    private readonly Context _context;
    private readonly PaddleOcrOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _detector;
    private InferenceSession? _recognizer;
    private string[] _characters = [];
    private bool _disposed;

    public string Name => "PP-OCRv5 mobile（离线）";

    public PaddleOcrEngine(Context context, PaddleOcrOptions? options = null)
    {
        _context = context.ApplicationContext ?? context;
        _options = options ?? new PaddleOcrOptions();
        if (_options.MaximumTextRegions is < 1 or > 256 || _options.RecognitionMaximumWidth is < 320 or > 3200 ||
            _options.DetectionThreshold is <= 0 or >= 1 || _options.BoxScoreThreshold is <= 0 or > 1 ||
            _options.RecognitionThreshold is < 0 or > 1 || _options.UnclipRatio is <= 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(options));
        _ = PaddleTensorCodec.DetectionSize(32, 32, _options.DetectionMaxSide);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await Task.Run(() => InitializeCore(cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<OcrResult> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                InitializeCore(cancellationToken);
                if (bitmap.IsRecycled || bitmap.Width <= 0 || bitmap.Height <= 0) throw new ArgumentException("OCR 截图已失效。", nameof(bitmap));
                return RecognizeCore(bitmap, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private void InitializeCore(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_detector is not null && _recognizer is not null) return;
        var detectorPath = InstallModel(DetectorFile, DetectorHash, cancellationToken);
        var recognizerPath = InstallModel(RecognizerFile, RecognizerHash, cancellationToken);
        using var options = new SessionOptions
        {
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableMemoryPattern = false,
            EnableCpuMemArena = false,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
        };
        // 两个 session 轮流执行，空闲线程不自旋抢占游戏 CPU。
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        InferenceSession? detector = null;
        InferenceSession? recognizer = null;
        try
        {
            detector = new InferenceSession(detectorPath, options);
            cancellationToken.ThrowIfCancellationRequested();
            recognizer = new InferenceSession(recognizerPath, options);
            if (!recognizer.ModelMetadata.CustomMetadataMap.TryGetValue("character", out var metadata))
                throw new InvalidDataException("OCR 识别模型缺少内置 character 字典。");
            var characters = PaddleTensorCodec.ReadCharacters(metadata);
            var dimensions = recognizer.OutputMetadata.First().Value.Dimensions;
            if (dimensions.Length != 3 || dimensions[^1] != characters.Length)
                throw new InvalidDataException($"OCR 模型与字典不匹配（字典 {characters.Length} 项）。");
            cancellationToken.ThrowIfCancellationRequested();
            _characters = characters;
            _detector = detector;
            _recognizer = recognizer;
            detector = recognizer = null;
        }
        finally { detector?.Dispose(); recognizer?.Dispose(); }
    }

    private string InstallModel(string fileName, string expectedHash, CancellationToken cancellationToken)
    {
        var directory = IOPath.Combine(_context.FilesDir!.AbsolutePath, "ocr-models");
        Directory.CreateDirectory(directory);
        var destination = IOPath.Combine(directory, fileName);
        if (File.Exists(destination) && HashMatches(destination, expectedHash)) return destination;
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var source = _context.Assets!.Open("ocr/" + fileName))
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536];
                int count;
                while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, count);
                }
                target.Flush(true);
            }
            if (!HashMatches(temporary, expectedHash)) throw new InvalidDataException($"离线 OCR 模型校验失败：{fileName}");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool HashMatches(string path, string expectedHash)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private OcrResult RecognizeCore(Bitmap bitmap, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var size = PaddleTensorCodec.DetectionSize(bitmap.Width, bitmap.Height, _options.DetectionMaxSide);
        // CreateScaledBitmap 可能返回传入对象；只释放实际新建的位图。
        var scaled = Bitmap.CreateScaledBitmap(bitmap, size.Width, size.Height, true)!;
        float[] detectorInput;
        try { detectorInput = CreateTensor(scaled, size.Width, detection: true); }
        finally { if (!ReferenceEquals(scaled, bitmap)) scaled.Dispose(); }
        var detection = Run(_detector!, detectorInput, [1, 3, size.Height, size.Width], cancellationToken);
        if (detection.Dimensions.Length != 4 || detection.Dimensions[0] != 1 || detection.Dimensions[1] != 1)
            throw new InvalidDataException("OCR 检测模型输出不是 [1,1,H,W]。");
        var (quads, truncated) = DbPostprocessor.Extract(detection.Values, detection.Dimensions[3], detection.Dimensions[2],
            bitmap.Width, bitmap.Height, _options, cancellationToken);
        var blocks = new List<OcrTextBlock>(quads.Count);
        foreach (var quad in quads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var crop = WarpRecognitionCrop(bitmap, quad, out var capped);
            truncated |= capped;
            if (crop is null) continue;
            var tensorWidth = Math.Max(320, crop.Width);
            var recognition = Run(_recognizer!, CreateTensor(crop, tensorWidth, detection: false), [1, 3, 48, tensorWidth], cancellationToken);
            if (recognition.Dimensions.Length != 3 || recognition.Dimensions[0] != 1)
                throw new InvalidDataException("OCR 识别模型输出不是 [1,T,C]。");
            var decoded = PaddleTensorCodec.Decode(recognition.Values, recognition.Dimensions[1], recognition.Dimensions[2], _characters);
            if (!string.IsNullOrWhiteSpace(decoded.Text) && decoded.Confidence >= _options.RecognitionThreshold)
                blocks.Add(new OcrTextBlock(decoded.Text, decoded.Confidence, quad.Points));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new OcrResult(blocks, stopwatch.Elapsed, truncated);
    }

    private static float[] CreateTensor(Bitmap bitmap, int paddedWidth, bool detection)
    {
        var pixels = new int[checked(bitmap.Width * bitmap.Height)];
        bitmap.GetPixels(pixels, 0, bitmap.Width, 0, 0, bitmap.Width, bitmap.Height);
        return PaddleTensorCodec.ToBgrTensor(pixels, bitmap.Width, bitmap.Height, paddedWidth, detection);
    }

    private Bitmap? WarpRecognitionCrop(Bitmap bitmap, OcrQuad quad, out bool capped)
    {
        var width = Math.Max(OcrQuad.Distance(quad.P0, quad.P1), OcrQuad.Distance(quad.P3, quad.P2));
        var height = Math.Max(OcrQuad.Distance(quad.P0, quad.P3), OcrQuad.Distance(quad.P1, quad.P2));
        var points = quad.Points;
        if (height >= width * 1.5f)
        {
            // 与 Paddle np.rot90 方向一致，竖向检测框逆时针转为水平识别。
            (width, height) = (height, width);
            points = [quad.P1, quad.P2, quad.P3, quad.P0];
        }
        var naturalWidth = Math.Max(8, (int)Math.Ceiling(48 * width / Math.Max(1, height)));
        var targetWidth = Math.Min(_options.RecognitionMaximumWidth, naturalWidth);
        capped = naturalWidth > targetWidth;
        using var matrix = new Matrix();
        float[] from = [points[0].X, points[0].Y, points[1].X, points[1].Y, points[2].X, points[2].Y, points[3].X, points[3].Y];
        float[] to = [0, 0, targetWidth, 0, targetWidth, 48, 0, 48];
        if (!matrix.SetPolyToPoly(from, 0, to, 0, 4)) return null;
        var output = Bitmap.CreateBitmap(targetWidth, 48, Bitmap.Config.Argb8888!)!;
        try
        {
            using var canvas = new Canvas(output);
            using var paint = new Paint(PaintFlags.FilterBitmap | PaintFlags.AntiAlias | PaintFlags.Dither);
            canvas.DrawColor(Color.White);
            canvas.DrawBitmap(bitmap, matrix, paint);
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    private static (float[] Values, int[] Dimensions) Run(InferenceSession session, float[] values, int[] dimensions, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = NamedOnnxValue.CreateFromTensor(session.InputNames[0], new DenseTensor<float>(values, dimensions));
        using var runOptions = new RunOptions();
        using var registration = cancellationToken.Register(() => runOptions.Terminate = true);
        try
        {
            using var outputs = session.Run([input], session.OutputNames, runOptions);
            cancellationToken.ThrowIfCancellationRequested();
            var tensor = outputs.First().AsTensor<float>();
            return (tensor.ToArray(), tensor.Dimensions.ToArray());
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _detector?.Dispose(); _detector = null;
            _recognizer?.Dispose(); _recognizer = null;
            _characters = [];
        }
        finally { _gate.Release(); }
    }
}
