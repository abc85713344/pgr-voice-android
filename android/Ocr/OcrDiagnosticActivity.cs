#if DEBUG
using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Widget;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Text.Json;
using IOPath = System.IO.Path;

namespace PgrVoice.AndroidApp.Ocr;

/// <summary>仅 Debug 存在的可重复 OCR 验收入口；输入/输出都限制在本应用私有 files 目录。</summary>
[Activity(Name = "org.pgrvoice.player.OcrDiagnosticActivity", Exported = true, Label = "OCR 离线验收")]
public sealed class OcrDiagnosticActivity : Activity
{
    private readonly CancellationTokenSource _lifetime = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(new TextView(this) { Text = "正在执行离线 OCR 验收，结果写入应用私有目录。", TextSize = 20 });
        _ = RunDiagnosticAsync();
    }

    private async Task RunDiagnosticAsync()
    {
        var root = IOPath.GetFullPath(FilesDir!.AbsolutePath) + IOPath.DirectorySeparatorChar;
        var reportName = Intent?.GetStringExtra("report") ?? "ocr-test-result.json";
        if (reportName.Length > 100 || reportName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_' && c != '.')) reportName = "ocr-test-result.json";
        var output = IOPath.Combine(root, reportName);
        object report;
        var kind = (Intent?.GetStringExtra("engine") ?? "paddle").Equals("mlkit", StringComparison.OrdinalIgnoreCase) ? OcrEngineKind.MlKitChinese : OcrEngineKind.Paddle;
        try
        {
            if (Intent?.GetBooleanExtra("maskOnly", false) == true)
            {
                var records = new List<object>();
                var tight = Intent?.GetBooleanExtra("maskTight", false) == true;
                foreach (var file in Directory.EnumerateFiles(root, "ch29-sequence-??.png").OrderBy(p => p, StringComparer.Ordinal))
                {
                    using var source = await BitmapFactory.DecodeFileAsync(file) ?? throw new InvalidDataException("无法读取稳定检测样本。");
                    using var crop = tight
                        ? Bitmap.CreateBitmap(source, (int)(source.Width * .17), (int)(source.Height * .77), (int)(source.Width * .75), (int)(source.Height * .15))!
                        : Bitmap.CreateBitmap(source, (int)(source.Width * .025), (int)(source.Height * .64), (int)(source.Width * .95), (int)(source.Height * .34))!;
                    using var small = Bitmap.CreateScaledBitmap(crop, 384, 96, true)!;
                    var pixels = new int[384 * 96];
                    small.GetPixels(pixels, 0, 384, 0, 0, 384, 96);
                    var mask = new byte[pixels.Length];
                    for (var i = 0; i < pixels.Length; i++)
                    {
                        var r = (pixels[i] >> 16) & 255; var g = (pixels[i] >> 8) & 255; var b = pixels[i] & 255;
                        mask[i] = (byte)(Math.Min(r, Math.Min(g, b)) > 155 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 75 ? 1 : 0);
                    }
                    records.Add(new { file = IOPath.GetFileName(file), whitePixels = mask.Count(v => v != 0), maskBase64 = Convert.ToBase64String(mask) });
                }
                report = new { success = true, maskWidth = 384, maskHeight = 96, tightRegion = tight, platform = "Android Bitmap.CreateScaledBitmap", records };
                await WriteReportAsync(output, report);
                RunOnUiThread(Finish);
                return;
            }
            var input = IOPath.GetFullPath(IOPath.Combine(root, Intent?.GetStringExtra("input") ?? "ocr-test.png"));
            if (!input.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("测试图片必须放在应用私有 files 目录内。");
            var count = Math.Clamp(Intent?.GetIntExtra("iterations", 2) ?? 2, 1, 5);
            using var bitmap = await BitmapFactory.DecodeFileAsync(input) ?? throw new InvalidDataException("无法读取 OCR 测试图片。");
            using IOcrEngine engine = kind == OcrEngineKind.Paddle ? new PaddleOcrEngine(this) : new MlKitEngine(this);
            var init = Stopwatch.StartNew();
            await engine.InitializeAsync(_lifetime.Token);
            init.Stop();
            var runs = new List<object>();
            for (var i = 0; i < count; i++)
            {
                var recognized = await engine.RecognizeAsync(bitmap, _lifetime.Token);
                runs.Add(new
                {
                    elapsedMs = recognized.Elapsed.TotalMilliseconds,
                    text = recognized.Text,
                    truncated = recognized.IsTruncated,
                    blocks = recognized.Blocks.Select(b => new { text = b.Text, confidence = b.Confidence, polygon = b.Polygon.Select(p => new { x = p.X, y = p.Y }) }).ToArray()
                });
            }
            report = new { success = true, engine = kind.ToString(), width = bitmap.Width, height = bitmap.Height, initializationMs = init.Elapsed.TotalMilliseconds,
                managedBytes = GC.GetTotalMemory(false), nativeBytes = global::Android.OS.Debug.NativeHeapAllocatedSize, runs };
        }
        catch (Exception ex)
        {
            report = new { success = false, engine = kind.ToString(), error = ex.ToString() };
        }
        try
        {
            await WriteReportAsync(output, report);
            global::Android.Util.Log.Info("PgrVoiceOcrTest", $"Complete: {output}");
        }
        catch (Exception ex) { global::Android.Util.Log.Error("PgrVoiceOcrTest", ex.ToString()); }
        RunOnUiThread(Finish);
    }

    private static Task WriteReportAsync(string output, object report) =>
        File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

    protected override void OnDestroy()
    {
        _lifetime.Cancel();
        base.OnDestroy();
    }
}
#endif
