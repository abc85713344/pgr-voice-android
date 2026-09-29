// DB geometry adapted from ciddwd/overlay-translator, DBPostprocessor.kt (Apache-2.0).
// Copyright retained in THIRD-PARTY-OCR.md and Assets/ocr/LICENSE-overlay-translator.txt.
// Modified 2026: C# port, contiguous buffers, bounded candidates and cancellation;
// no manga column splitting; coordinates are clamped to the source bitmap.
namespace PgrVoice.AndroidApp.Ocr;

internal readonly record struct OcrQuad(OcrPoint P0, OcrPoint P1, OcrPoint P2, OcrPoint P3)
{
    public float Width => Distance(P0, P1);
    public float Height => Distance(P0, P3);
    public float CenterY => (P0.Y + P1.Y + P2.Y + P3.Y) / 4;
    public float CenterX => (P0.X + P1.X + P2.X + P3.X) / 4;
    public OcrPoint[] Points => [P0, P1, P2, P3];
    public static float Distance(OcrPoint a, OcrPoint b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

internal static class DbPostprocessor
{
    private readonly record struct Pixel(int X, int Y);

    public static (IReadOnlyList<OcrQuad> Quads, bool Truncated) Extract(
        float[] probabilities, int width, int height, int sourceWidth, int sourceHeight,
        PaddleOcrOptions options, CancellationToken cancellationToken)
    {
        if (width <= 0 || height <= 0 || probabilities.Length != checked(width * height))
            throw new InvalidDataException("OCR 检测概率图尺寸异常。");
        var visited = new bool[probabilities.Length];
        var stack = new Stack<int>();
        var boundary = new List<Pixel>();
        var result = new List<OcrQuad>();
        var truncated = false;
        var candidateCount = 0;
        var scaleX = (float)sourceWidth / width;
        var scaleY = (float)sourceHeight / height;
        var maxUnclip = Math.Max(4, Math.Min(width, height) * .05f);

        for (var index = 0; index < probabilities.Length; index++)
        {
            if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (visited[index] || !float.IsFinite(probabilities[index]) || probabilities[index] < options.DetectionThreshold) continue;
            if (++candidateCount > 1000) { truncated = true; break; }
            stack.Clear();
            boundary.Clear();
            stack.Push(index);
            visited[index] = true;
            var area = 0;
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                var x = current % width;
                var y = current / width;
                var isBoundary = false;
                if ((++area & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || nx >= width || ny < 0 || ny >= height) { isBoundary = true; continue; }
                    var next = ny * width + nx;
                    if (!float.IsFinite(probabilities[next]) || probabilities[next] < options.DetectionThreshold) { isBoundary = true; continue; }
                    if (!visited[next]) { visited[next] = true; stack.Push(next); }
                }
                if (isBoundary) boundary.Add(new Pixel(x, y));
            }
            if (area < 16 || boundary.Count < 3) continue;
            var hull = ConvexHull(boundary);
            var minimum = MinimumAreaRectangle(hull);
            if (minimum is not { } quad || Math.Min(quad.Width, quad.Height) < 3) continue;
            if (BoxScore(probabilities, width, height, quad) < options.BoxScoreThreshold) continue;
            if (result.Count == options.MaximumTextRegions) { truncated = true; break; }
            var expanded = Expand(quad, options.UnclipRatio, maxUnclip);
            OcrPoint Map(OcrPoint p) => new(Math.Clamp(p.X * scaleX, 0, sourceWidth - 1), Math.Clamp(p.Y * scaleY, 0, sourceHeight - 1));
            var mapped = new OcrQuad(Map(expanded.P0), Map(expanded.P1), Map(expanded.P2), Map(expanded.P3));
            if (mapped.Width > 3 && mapped.Height > 3) result.Add(mapped);
        }

        // 与 Paddle 的行排序相同：先纵向，再对相邻行顶端相差小于 10 像素的框按横向调整。
        result.Sort((a, b) => a.P0.Y == b.P0.Y ? a.P0.X.CompareTo(b.P0.X) : a.P0.Y.CompareTo(b.P0.Y));
        for (var i = 0; i < result.Count - 1; i++)
        for (var j = i; j >= 0; j--)
        {
            if (Math.Abs(result[j + 1].P0.Y - result[j].P0.Y) >= 10 || result[j + 1].P0.X >= result[j].P0.X) break;
            (result[j], result[j + 1]) = (result[j + 1], result[j]);
        }
        return (result, truncated);
    }

    private static List<Pixel> ConvexHull(List<Pixel> pixels)
    {
        var points = pixels.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        if (points.Length <= 2) return points.ToList();
        var hull = new List<Pixel>();
        foreach (var p in points)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        var lower = hull.Count + 1;
        for (var i = points.Length - 2; i >= 0; i--)
        {
            var p = points[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    private static long Cross(Pixel o, Pixel a, Pixel b) => (long)(a.X - o.X) * (b.Y - o.Y) - (long)(a.Y - o.Y) * (b.X - o.X);

    private static OcrQuad? MinimumAreaRectangle(List<Pixel> hull)
    {
        if (hull.Count < 3) return null;
        var bestArea = double.MaxValue;
        OcrQuad? best = null;
        for (var edge = 0; edge < hull.Count; edge++)
        {
            var a = hull[edge];
            var b = hull[(edge + 1) % hull.Count];
            var dx = (double)b.X - a.X;
            var dy = (double)b.Y - a.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < .000001) continue;
            var ux = dx / length; var uy = dy / length;
            var vx = -uy; var vy = ux;
            var minU = double.MaxValue; var maxU = double.MinValue;
            var minV = double.MaxValue; var maxV = double.MinValue;
            foreach (var point in hull)
            {
                var u = point.X * ux + point.Y * uy;
                var v = point.X * vx + point.Y * vy;
                minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
            }
            var w = maxU - minU; var h = maxV - minV;
            var area = w * h;
            if (area >= bestArea || w <= 1 || h <= 1) continue;
            bestArea = area;
            OcrPoint Point(double u, double v) => new((float)(u * ux + v * vx), (float)(u * uy + v * vy));
            var corners = new[] { Point(minU, minV), Point(maxU, minV), Point(maxU, maxV), Point(minU, maxV) }.OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
            var left = corners.Take(2).OrderBy(p => p.Y).ToArray();
            var right = corners.Skip(2).OrderBy(p => p.Y).ToArray();
            best = new OcrQuad(left[0], right[0], right[1], left[1]);
        }
        return best;
    }

    private static float BoxScore(float[] map, int width, int height, OcrQuad quad)
    {
        var points = quad.Points;
        var x0 = Math.Clamp((int)MathF.Floor(points.Min(p => p.X)), 0, width - 1);
        var x1 = Math.Clamp((int)MathF.Ceiling(points.Max(p => p.X)), 0, width - 1);
        var y0 = Math.Clamp((int)MathF.Floor(points.Min(p => p.Y)), 0, height - 1);
        var y1 = Math.Clamp((int)MathF.Ceiling(points.Max(p => p.Y)), 0, height - 1);
        double sum = 0;
        var count = 0;
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            if (!Contains(points, x + .5f, y + .5f)) continue;
            var probability = map[y * width + x];
            if (!float.IsFinite(probability)) continue;
            sum += probability;
            count++;
        }
        return count == 0 ? 0 : (float)(sum / count);
    }

    private static bool Contains(OcrPoint[] points, float x, float y)
    {
        var inside = false;
        var previous = points.Length - 1;
        for (var i = 0; i < points.Length; i++)
        {
            var p = points[i]; var q = points[previous];
            if ((p.Y > y) != (q.Y > y) && x < (q.X - p.X) * (y - p.Y) / (q.Y - p.Y + 1e-9f) + p.X) inside = !inside;
            previous = i;
        }
        return inside;
    }

    private static OcrQuad Expand(OcrQuad quad, float ratio, float maxDistance)
    {
        var width = quad.Width; var height = quad.Height;
        if (width <= 1 || height <= 1) return quad;
        var distance = Math.Min(width * height * ratio / (2 * (width + height)), maxDistance);
        var ux = (quad.P1.X - quad.P0.X) / width; var uy = (quad.P1.Y - quad.P0.Y) / width;
        var vx = (quad.P3.X - quad.P0.X) / height; var vy = (quad.P3.Y - quad.P0.Y) / height;
        OcrPoint Shift(OcrPoint p, float u, float v) => new(p.X + u * ux + v * vx, p.Y + u * uy + v * vy);
        return new OcrQuad(Shift(quad.P0, -distance, -distance), Shift(quad.P1, distance, -distance), Shift(quad.P2, distance, distance), Shift(quad.P3, -distance, distance));
    }
}
