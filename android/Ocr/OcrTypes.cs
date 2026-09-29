namespace PgrVoice.AndroidApp.Ocr;

public enum OcrEngineKind { Paddle, MlKitChinese }

public readonly record struct OcrPoint(float X, float Y);

/// <summary>坐标相对于传入的 Bitmap；调用方负责把区域坐标映射回捕获画面。</summary>
public sealed record OcrTextBlock(string Text, float Confidence, OcrPoint[] Polygon)
{
    public float Left => Polygon.Length == 0 ? 0 : Polygon.Min(p => p.X);
    public float Top => Polygon.Length == 0 ? 0 : Polygon.Min(p => p.Y);
    public float Right => Polygon.Length == 0 ? 0 : Polygon.Max(p => p.X);
    public float Bottom => Polygon.Length == 0 ? 0 : Polygon.Max(p => p.Y);
}

public sealed record OcrResult(IReadOnlyList<OcrTextBlock> Blocks, TimeSpan Elapsed, bool IsTruncated = false)
{
    public string Text => string.Join("\n", Blocks.Select(b => b.Text));
}

public sealed record PaddleOcrOptions
{
    // 限定字幕区域的推理大小，避免把窄字幕裁剪图按短边放大成数百万像素。
    public int DetectionMaxSide { get; init; } = 960;
    public float DetectionThreshold { get; init; } = .3f;
    public float BoxScoreThreshold { get; init; } = .5f;
    public float UnclipRatio { get; init; } = 1.6f;
    public float RecognitionThreshold { get; init; } = .35f;
    public int MaximumTextRegions { get; init; } = 64;
    public int RecognitionMaximumWidth { get; init; } = 3200;
}
