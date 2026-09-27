using System;
using System.Collections.Generic;

namespace AmrMapEditor.Core;

/// <summary>안티앨리어싱 없는 픽셀 단위 그리기</summary>
public static class Raster
{
    private static readonly Dictionary<(int Size, bool Round), (int Dx, int Dy)[]> BrushCache = new();

    /// <summary>
    /// 브러시 픽셀 오프셋. 범위는 -(size-1)/2 ~ size/2
    /// (화면 미리보기와 동일한 기준)
    /// </summary>
    public static (int Dx, int Dy)[] BrushOffsets(int size, bool round)
    {
        size = Math.Clamp(size, 1, 256);
        if (BrushCache.TryGetValue((size, round), out var cached)) return cached;

        var list = new List<(int, int)>(size * size);
        double c = (size - 1) / 2.0;
        double r2 = (size / 2.0) * (size / 2.0) - 0.3;
        int o = (size - 1) / 2;
        for (int j = 0; j < size; j++)
        for (int i = 0; i < size; i++)
        {
            if (round)
            {
                double dx = i - c, dy = j - c;
                if (dx * dx + dy * dy > r2) continue;
            }
            list.Add((i - o, j - o));
        }

        var arr = list.ToArray();
        BrushCache[(size, round)] = arr;
        return arr;
    }

    public static void Stamp(int cx, int cy, (int Dx, int Dy)[] offsets, Action<int, int> plot)
    {
        foreach (var (dx, dy) in offsets) plot(cx + dx, cy + dy);
    }

    /// <summary>Bresenham 직선</summary>
    public static void Line(int x0, int y0, int x1, int y1, Action<int, int> plot)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            plot(x0, y0);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>브러시 두께로 직선 그리기</summary>
    public static void StrokeLine(int x0, int y0, int x1, int y1, (int Dx, int Dy)[] offsets, Action<int, int> plot)
    {
        if (offsets.Length == 1 && offsets[0] == (0, 0))
        {
            Line(x0, y0, x1, y1, plot);
            return;
        }
        Line(x0, y0, x1, y1, (x, y) => Stamp(x, y, offsets, plot));
    }

    public static void RectFill(IntRect r, Action<int, int> plot)
    {
        for (int y = r.Y; y < r.Bottom; y++)
        for (int x = r.X; x < r.Right; x++)
            plot(x, y);
    }

    public static void RectOutline(IntRect r, (int Dx, int Dy)[] offsets, Action<int, int> plot)
    {
        if (r.IsEmpty) return;
        int x0 = r.X, y0 = r.Y, x1 = r.Right - 1, y1 = r.Bottom - 1;
        StrokeLine(x0, y0, x1, y0, offsets, plot);
        StrokeLine(x1, y0, x1, y1, offsets, plot);
        StrokeLine(x1, y1, x0, y1, offsets, plot);
        StrokeLine(x0, y1, x0, y0, offsets, plot);
    }

    /// <summary>
    /// 4방향 채우기: 시작 픽셀과 같은 값인 연결 영역을 fill 값으로 변경.
    /// tracker.Clip/보호 영역에서 막히면 그 방향으로는 확장하지 않음. 변경 픽셀 수 반환
    /// </summary>
    public static int FloodFill(MapImage map, int sx, int sy, byte fill, EditTracker tracker)
    {
        if (!map.InBounds(sx, sy)) return 0;
        byte target = map.Get(sx, sy);
        if (target == fill) return 0;

        IntRect clip = (tracker.Clip?.Bounds ?? map.Bounds).Intersect(map.Bounds);
        if (!clip.Contains(sx, sy)) return 0;

        int w = map.Width;
        byte[] data = map.Data;
        var stack = new Stack<int>();
        stack.Push(sy * w + sx);
        int count = 0;

        while (stack.Count > 0)
        {
            int i = stack.Pop();
            if (data[i] != target) continue;
            int x = i % w, y = i / w;
            if (!tracker.Set(x, y, fill)) continue;
            count++;
            if (x > clip.X) stack.Push(i - 1);
            if (x < clip.Right - 1) stack.Push(i + 1);
            if (y > clip.Y) stack.Push(i - w);
            if (y < clip.Bottom - 1) stack.Push(i + w);
        }
        return count;
    }

    /// <summary>Shift 스냅: 0 / 45 / 90도 방향으로 끝점 보정</summary>
    public static (int X, int Y) SnapAngle(int x0, int y0, int x1, int y1)
    {
        int dx = x1 - x0, dy = y1 - y0;
        int adx = Math.Abs(dx), ady = Math.Abs(dy);
        const double tan22 = 0.41421356;
        if (ady <= adx * tan22) return (x1, y0);
        if (adx <= ady * tan22) return (x0, y1);
        int d = Math.Max(adx, ady);
        return (x0 + Math.Sign(dx) * d, y0 + Math.Sign(dy) * d);
    }
}
