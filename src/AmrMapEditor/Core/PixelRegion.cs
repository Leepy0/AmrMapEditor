using System;
using System.Collections.Generic;
using System.Linq;

namespace AmrMapEditor.Core;

/// <summary>이미지 좌표의 실수 점 (픽셀 중심 = x + 0.5)</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>
/// 픽셀 영역 (사각형 또는 폴리곤). 폴리곤은 꼭짓점을 픽셀 중심으로 찍고,
/// 내부(픽셀 중심 기준) + 경계선 픽셀을 포함
/// </summary>
public sealed class PixelRegion
{
    private readonly bool[]? _mask;   // Bounds 크기, null이면 사각형 전체

    private PixelRegion(IntRect bounds, bool[]? mask, IReadOnlyList<PointD>? polygon, long count)
    {
        Bounds = bounds;
        _mask = mask;
        Polygon = polygon;
        PixelCount = count;
    }

    public IntRect Bounds { get; }

    /// <summary>폴리곤 꼭짓점 (사각형이면 null)</summary>
    public IReadOnlyList<PointD>? Polygon { get; }

    public bool IsRect => _mask == null;
    public long PixelCount { get; }

    public bool Contains(int x, int y)
    {
        if (!Bounds.Contains(x, y)) return false;
        return _mask == null || _mask[(y - Bounds.Y) * Bounds.Width + (x - Bounds.X)];
    }

    /// <summary>화면 표시용 외곽선 (이미지 좌표). 사각형은 픽셀 모서리 기준</summary>
    public IReadOnlyList<PointD> Outline =>
        Polygon ?? new[]
        {
            new PointD(Bounds.X, Bounds.Y), new PointD(Bounds.Right, Bounds.Y),
            new PointD(Bounds.Right, Bounds.Bottom), new PointD(Bounds.X, Bounds.Bottom),
        };

    public void ForEach(Action<int, int> action)
    {
        for (int y = Bounds.Y; y < Bounds.Bottom; y++)
        for (int x = Bounds.X; x < Bounds.Right; x++)
            if (_mask == null || _mask[(y - Bounds.Y) * Bounds.Width + (x - Bounds.X)])
                action(x, y);
    }

    public static PixelRegion FromRect(IntRect r) => new(r, null, null, r.Area);

    /// <summary>폴리곤 영역 생성. 꼭짓점 3개 미만이거나 이미지와 겹치지 않으면 null</summary>
    public static PixelRegion? FromPolygon(IReadOnlyList<PointD> points, IntRect clipTo)
    {
        if (points.Count < 3) return null;
        int minX = (int)Math.Floor(points.Min(p => p.X)), maxX = (int)Math.Floor(points.Max(p => p.X));
        int minY = (int)Math.Floor(points.Min(p => p.Y)), maxY = (int)Math.Floor(points.Max(p => p.Y));
        IntRect bounds = new IntRect(minX, minY, maxX - minX + 1, maxY - minY + 1).Intersect(clipTo);
        if (bounds.IsEmpty) return null;

        var mask = new bool[bounds.Width * bounds.Height];
        int n = points.Count;
        var xs = new List<double>(8);

        // 내부 채우기 (even-odd, 픽셀 중심 기준 scanline)
        for (int y = bounds.Y; y < bounds.Bottom; y++)
        {
            double yc = y + 0.5;
            xs.Clear();
            for (int k = 0; k < n; k++)
            {
                PointD a = points[k], b = points[(k + 1) % n];
                if ((a.Y <= yc && yc < b.Y) || (b.Y <= yc && yc < a.Y))
                    xs.Add(a.X + (yc - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int x0 = Math.Max(bounds.X, (int)Math.Ceiling(xs[k] - 0.5));
                int x1 = Math.Min(bounds.Right - 1, (int)Math.Ceiling(xs[k + 1] - 0.5) - 1);
                for (int x = x0; x <= x1; x++) mask[(y - bounds.Y) * bounds.Width + (x - bounds.X)] = true;
            }
        }

        // 경계선 픽셀 포함 (클릭한 꼭짓점 픽셀이 빠지지 않도록)
        for (int k = 0; k < n; k++)
        {
            PointD a = points[k], b = points[(k + 1) % n];
            Raster.Line((int)Math.Floor(a.X), (int)Math.Floor(a.Y), (int)Math.Floor(b.X), (int)Math.Floor(b.Y), (x, y) =>
            {
                if (bounds.Contains(x, y)) mask[(y - bounds.Y) * bounds.Width + (x - bounds.X)] = true;
            });
        }

        long count = mask.LongCount(v => v);
        return count == 0 ? null : new PixelRegion(bounds, mask, points.ToArray(), count);
    }

    /// <summary>여러 영역의 합집합을 전체 이미지 크기 마스크로</summary>
    public static bool[] BuildMask(IEnumerable<PixelRegion> regions, int width, int height)
    {
        var mask = new bool[width * height];
        var full = new IntRect(0, 0, width, height);
        foreach (PixelRegion r in regions)
        {
            IntRect b = r.Bounds.Intersect(full);
            for (int y = b.Y; y < b.Bottom; y++)
            for (int x = b.X; x < b.Right; x++)
                if (r.Contains(x, y)) mask[y * width + x] = true;
        }
        return mask;
    }

    public override string ToString() =>
        IsRect ? $"사각형 {Bounds}" : $"폴리곤 {Polygon!.Count}점, 범위 {Bounds}, {PixelCount:N0} px";
}
