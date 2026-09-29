using System.Text.Json;

namespace PgrVoice.AndroidApp;

public sealed class SessionDiagnostics(string directory)
{
    readonly object gate = new();
    long frames, repeatedSamples, ocrRuns, advances, errors, discarded;
    long logWriteFailures;
    string? lastLogWriteError;
    double totalMs, maxMs;
    public void Frame(bool repeated=false) { Interlocked.Increment(ref frames);if(repeated)Interlocked.Increment(ref repeatedSamples); }
    public void Discard() => Interlocked.Increment(ref discarded);
    public void Ocr(double ms) { lock (gate) { ocrRuns++; totalMs += ms; maxMs = Math.Max(maxMs, ms); } }
    public void Advance() => Interlocked.Increment(ref advances);
    public void Error(string message) { Interlocked.Increment(ref errors); Log("错误", message); }
    public void Log(string kind, string message)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "events.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512_000) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O}\t{kind}\t{message}\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Error reporting must still work when storage is full or unavailable.
                // Keep this in memory; calling Error/Log here would recurse.
                logWriteFailures++;
                lastLogWriteError = ex.Message;
            }
        }
    }
    public string Summary { get { lock (gate) return $"画面采样 {frames} · OCR {ocrRuns} · 自动推进 {advances}\nOCR 平均 {(ocrRuns == 0 ? 0 : totalMs / ocrRuns):F0} ms / 最长 {maxMs:F0} ms\n丢弃过期画面 {discarded} · 错误 {errors}\n进程托管内存 {GC.GetTotalMemory(false) / 1048576:F1} MB（不含原生模型）" +
        (logWriteFailures == 0 ? "" : $"\n诊断日志保存失败 {logWriteFailures} 次，可导出当前内存中的统计。"); } }
    public string Export()
    {
        lock (gate) return JsonSerializer.Serialize(new { generatedAt = DateTimeOffset.Now, frames, repeatedSamples, ocrRuns, advances, errors, discarded, logWriteFailures, lastLogWriteError,
            ocrMeanMs = ocrRuns == 0 ? 0 : totalMs / ocrRuns, ocrMaxMs = maxMs, managedBytes = GC.GetTotalMemory(false),
            device = global::Android.OS.Build.Model, android = global::Android.OS.Build.VERSION.Release,
            warning = "这是当前设备数据；模拟器不能代表手机游戏性能。人工误播/漏播及游戏混音需另记。" }, Json.Options);
    }
}
