using System;

namespace AmrMapEditor.Core;

/// <summary>
/// Chamfer(3-4) 거리 변환. 결과 값 / 3 ≈ 픽셀 거리.
/// rect 영역만 계산하고 rect 밖은 특징점이 없는 것으로 간주
/// </summary>
public sealed class DistanceField
{
    private const int Inf = int.MaxValue / 4;
    private readonly int[] _d;

    private DistanceField(IntRect rect, int[] d)
    {
        Rect = rect;
        _d = d;
    }

    public IntRect Rect { get; }

    /// <summary>픽셀 거리 (rect 밖이면 double.MaxValue)</summary>
    public double DistanceAt(int x, int y)
    {
        if (!Rect.Contains(x, y)) return double.MaxValue;
        int v = _d[(y - Rect.Y) * Rect.Width + (x - Rect.X)];
        return v >= Inf ? double.MaxValue : v / 3.0;
    }

    public static DistanceField Compute(IntRect rect, Func<int, int, bool> isFeature)
    {
        int w = rect.Width, h = rect.Height;
        var d = new int[Math.Max(0, w * h)];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            d[y * w + x] = isFeature(rect.X + x, rect.Y + y) ? 0 : Inf;

        // 정방향
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x, v = d[i];
            if (v == 0) continue;
            if (x > 0) v = Math.Min(v, d[i - 1] + 3);
            if (y > 0)
            {
                v = Math.Min(v, d[i - w] + 3);
                if (x > 0) v = Math.Min(v, d[i - w - 1] + 4);
                if (x < w - 1) v = Math.Min(v, d[i - w + 1] + 4);
            }
            d[i] = v;
        }
        // 역방향
        for (int y = h - 1; y >= 0; y--)
        for (int x = w - 1; x >= 0; x--)
        {
            int i = y * w + x, v = d[i];
            if (v == 0) continue;
            if (x < w - 1) v = Math.Min(v, d[i + 1] + 3);
            if (y < h - 1)
            {
                v = Math.Min(v, d[i + w] + 3);
                if (x < w - 1) v = Math.Min(v, d[i + w + 1] + 4);
                if (x > 0) v = Math.Min(v, d[i + w - 1] + 4);
            }
            d[i] = v;
        }
        return new DistanceField(rect, d);
    }
}
