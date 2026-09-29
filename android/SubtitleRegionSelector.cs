namespace PgrVoice.AndroidApp;

/// <summary>画面比例坐标；与用户保存的字幕框无关，不能写回设置。</summary>
public readonly record struct NarrationRegion(double X, double Y, double Width, double Height);

/// <summary>
/// 仅判断明显的黑底中央白字画面。它不是 OCR，也不能确认文字属于剧情或当前路线。
/// 调用方在返回 false 时沿用用户字幕框；返回 true 时仍须经过原有 OCR、稳定检测与匹配门控。
/// </summary>
public static class SubtitleRegionSelector
{
    // 双线性缩放保留小字笔画。直接隔点采样可能跳过只有几像素高的旁白。
    public const int SampleWidth = 320;
    public const int SampleHeight = 192;
    public static NarrationRegion CentralNarration { get; } = new(.04, .15, .92, .50);

    /// <summary>
    /// 输入按行排列的 ARGB 低分辨率画面，不保留像素。仅支持有限大小样本，避免误扫全屏原图。
    /// 选区排除顶部菜单及底部普通对白。孤立箭头不足以触发，但不会保证裁掉正文旁的进度箭头。
    /// </summary>
    public static bool TrySelectNarration(ReadOnlySpan<int> argb, int width, int height, out NarrationRegion region)
    {
        region = default;
        if (width is < 96 or > 640 || height is < 64 or > 384 || argb.Length != width * height)
            return false;

        int left = (int)(width * .04), right = (int)(width * .96);
        int top = (int)(height * .12), bottom = (int)(height * .93);
        int textLeft = (int)(width * .08), textRight = (int)(width * .92);
        int textTop = (int)(height * .16), textBottom = (int)(height * .64);
        Span<int> rowCount = stackalloc int[height];
        Span<int> rowLeft = stackalloc int[height];
        Span<int> rowRight = stackalloc int[height];
        rowCount.Clear(); rowLeft.Fill(width); rowRight.Fill(-1);
        int total = 0, black = 0, colored = 0, bright = 0, lowerBright = 0;

        for (int y = top; y < bottom; y++)
        for (int x = left; x < right; x++)
        {
            uint pixel = (uint)argb[y * width + x];
            // 未就绪或透明缓冲区不能成为旁白证据。
            if ((pixel >> 24) < 240) return false;
            int r = (int)(pixel >> 16 & 255), g = (int)(pixel >> 8 & 255), b = (int)(pixel & 255);
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            total++;
            if (max <= 24) black++;
            if (max >= 48 && max - min > 32) colored++;
            bool white = min >= 70 && max - min <= 32;
            if (!white) continue;
            if (y >= (int)(height * .69)) lowerBright++;
            if (y < textTop || y >= textBottom || x < textLeft || x >= textRight) continue;
            bright++; rowCount[y]++; rowLeft[y] = Math.Min(rowLeft[y], x); rowRight[y] = Math.Max(rowRight[y], x);
        }

        // 允许文字自身占用少量非黑像素，但大幅暗场、彩色场景和普通底部对白不触发。
        if (black < total * .955 || colored > total * .0025 || bright < width * .09 || lowerBright >= width * .08)
            return false;

        int acceptedBright = 0, acceptedLines = 0;
        for (int y = textTop; y < textBottom; y++)
        {
            if (rowCount[y] < 2) continue;
            int first = y, last = y, count = 0, minX = width, maxX = -1;
            // 字体抗锯齿可令某一行稀疏；只桥接一行空隙，不能把段落并成大矩形。
            for (; y < textBottom; y++)
            {
                if (rowCount[y] < 2)
                {
                    if (y + 1 >= textBottom || rowCount[y + 1] < 2) break;
                    continue;
                }
                last = y; count += rowCount[y]; minX = Math.Min(minX, rowLeft[y]); maxX = Math.Max(maxX, rowRight[y]);
            }
            int bandWidth = maxX - minX + 1, bandHeight = last - first + 1;
            double fill = (double)count / (bandWidth * bandHeight);
            if (bandHeight < 2 || bandHeight > Math.Max(3, height * .06) || bandWidth < width * .075 ||
                bandWidth > width * .84 || bandWidth < bandHeight * 3 || fill is < .12 or > .82)
                continue;
            // 多个字的笔画通常令列投影反复变化；空心按钮框只有两侧变化，不能当旁白。
            int variations = 0, previousColumn = -1;
            for (int x = minX; x <= maxX; x++)
            {
                int column = 0;
                for (int yy = first; yy <= last; yy++)
                {
                    uint pixel = (uint)argb[yy * width + x];
                    int r = (int)(pixel >> 16 & 255), g = (int)(pixel >> 8 & 255), b = (int)(pixel & 255);
                    int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    if (min >= 70 && max - min <= 32) column++;
                }
                if (previousColumn >= 0 && column != previousColumn) variations++;
                previousColumn = column;
            }
            if (variations < 4) continue;
            acceptedLines++; acceptedBright += count;
        }

        // 图标和杂散亮点不应只凭其中一小块触发；复杂图案宁可留给用户框选。
        if (acceptedLines is < 1 or > 5 || acceptedBright < bright * .80) return false;
        region = CentralNarration;
        return true;
    }
}
