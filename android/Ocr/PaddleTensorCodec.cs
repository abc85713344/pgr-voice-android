using System.Text;
using System.Text.RegularExpressions;

namespace PgrVoice.AndroidApp.Ocr;

/// <summary>无平台依赖的 PP-OCRv5 张量预处理及 CTC 解码，可由桌面回归测试直接运行。</summary>
internal static class PaddleTensorCodec
{
    public static string[] ReadCharacters(string metadata)
    {
        // 等价于 Python str.splitlines()；不 Trim 字符，不过滤空项，不破坏代理对/emoji。
        var characters = Regex.Split(metadata, "\\r\\n|[\\n\\r\\v\\f\\u001c-\\u001e\\u0085\\u2028\\u2029]").ToList();
        if (characters.Count > 0 && characters[^1].Length == 0) characters.RemoveAt(characters.Count - 1);
        if (characters.Count == 0) throw new InvalidDataException("OCR 模型内置字典为空。");
        characters.Insert(0, ""); // CTC blank
        characters.Add(" ");
        return characters.ToArray();
    }

    public static (int Width, int Height) DetectionSize(int width, int height, int maxSide)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (maxSide < 32 || maxSide > 2048 || maxSide % 32 != 0) throw new ArgumentOutOfRangeException(nameof(maxSide));
        var ratio = Math.Min(1d, (double)maxSide / Math.Max(width, height));
        static int Align(double value, int limit) => Math.Clamp((int)Math.Round(value / 32, MidpointRounding.ToEven) * 32, 32, limit);
        return (Align(width * ratio, maxSide), Align(height * ratio, maxSide));
    }

    public static float[] ToBgrTensor(int[] argb, int width, int height, int paddedWidth, bool detection)
    {
        if (argb.Length != checked(width * height) || paddedWidth < width) throw new ArgumentException("OCR 图像尺寸不匹配。");
        var plane = checked(paddedWidth * height);
        var output = new float[checked(3 * plane)];
        // PP-OCRv5 官方 det 配置为 BGR + ImageNet mean/std；rec 为 BGR + 0.5/0.5。
        // RapidOCR 的旧通用默认 det=0.5/0.5 与本模型官方训练配置不同，这里按模型配置处理。
        ReadOnlySpan<float> mean = detection ? [.485f, .456f, .406f] : [.5f, .5f, .5f];
        ReadOnlySpan<float> std = detection ? [.229f, .224f, .225f] : [.5f, .5f, .5f];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var color = argb[y * width + x];
            var index = y * paddedWidth + x;
            output[index] = ((color & 255) / 255f - mean[0]) / std[0];
            output[plane + index] = (((color >> 8) & 255) / 255f - mean[1]) / std[1];
            output[2 * plane + index] = (((color >> 16) & 255) / 255f - mean[2]) / std[2];
        }
        // rec 右侧 padding 保持归一化后的 0，等效于灰色 127.5。
        return output;
    }

    public static (string Text, float Confidence) Decode(ReadOnlySpan<float> output, int steps, int classes, IReadOnlyList<string> characters)
    {
        if (classes != characters.Count)
            throw new InvalidDataException($"OCR 字典与模型类别不一致：{characters.Count} / {classes}。");
        if (steps < 0 || classes <= 0 || output.Length != checked(steps * classes))
            throw new InvalidDataException("OCR 识别张量尺寸异常。");
        var text = new StringBuilder();
        double confidence = 0;
        var count = 0;
        var previous = -1;
        for (var t = 0; t < steps; t++)
        {
            var row = output.Slice(t * classes, classes);
            var bestIndex = 0;
            var best = float.NegativeInfinity;
            for (var c = 0; c < classes; c++)
                if (row[c] > best) { best = row[c]; bestIndex = c; }
            if (bestIndex != 0 && bestIndex != previous && float.IsFinite(best))
            {
                text.Append(characters[bestIndex]);
                confidence += Math.Clamp(best, 0, 1);
                count++;
            }
            // blank 也更新 previous，允许“哈哈”中间有 blank 时输出重复字。
            previous = bestIndex;
        }
        return (text.ToString().Trim(), count == 0 ? 0 : (float)(confidence / count));
    }
}
