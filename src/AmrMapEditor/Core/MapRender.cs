using System;

namespace AmrMapEditor.Core;

public enum MapDisplayMode { Standard, Probability, Category, Raw }

public enum MapOverlayMode { None, Diff, Reference }

/// <summary>값 → 화면 색상 LUT (Pbgra32, 0xAARRGGBB)</summary>
public static class MapPalettes
{
    public static uint[] Create(MapDisplayMode mode)
    {
        var lut = new uint[256];
        for (int v = 0; v < 256; v++) lut[v] = ColorOf(mode, (byte)v);
        return lut;
    }

    private static uint ColorOf(MapDisplayMode mode, byte v)
    {
        if (mode == MapDisplayMode.Raw) return Rgb(v, v, v);

        switch (v)
        {
            case MapValues.Unknown: return Rgb(0xB4, 0xBE, 0xCB);   // 청회색
            case MapValues.Free: return Rgb(0xFF, 0xFF, 0xFF);
            case MapValues.Obstacle: return Rgb(0x00, 0x00, 0x00);
            case MapValues.Invalid: return Rgb(0xFF, 0x00, 0xFF);   // 범위 외 경고색
        }

        // 확률값 2~253
        double t = (v - 2) / 251.0;
        return mode switch
        {
            MapDisplayMode.Category => Rgb(0xFF, 0x98, 0x00),
            MapDisplayMode.Probability => Hsv(240.0 * (1 - t), 0.9, 0.95),
            _ => Gray((int)Math.Round(245 - t * 225)),
        };
    }

    private static uint Gray(int g) => Rgb(g, g, g);

    private static uint Rgb(int r, int g, int b) =>
        0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;

    private static uint Hsv(double h, double s, double v)
    {
        double c = v * s;
        double hp = h / 60.0;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        (double r, double g, double b) = hp switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        double m = v - c;
        return Rgb((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }
}

public static class MapRender
{
    /// <summary>chunk 영역을 LUT로 변환해 dest(행 우선, 폭 = chunk.Width)에 기록</summary>
    public static void FillBase(MapImage map, uint[] lut, IntRect chunk, uint[] dest)
    {
        byte[] data = map.Data;
        int w = map.Width, k = 0;
        for (int y = chunk.Y; y < chunk.Bottom; y++)
        {
            int row = y * w;
            for (int x = chunk.X; x < chunk.Right; x++) dest[k++] = lut[data[row + x]];
        }
    }
}

public sealed class DiffStats
{
    public long Added { get; set; }
    public long Removed { get; set; }
    public long Other { get; set; }
    public long Total => Added + Removed + Other;
}

/// <summary>기준 맵(업데이트 전)과 현재 맵 비교</summary>
public static class MapDiff
{
    // Pbgra32 프리멀티플라이드 색상
    public static readonly uint AddedColor = Premul(200, 255, 30, 30);      // 빨강: 새로 생긴 장애물
    public static readonly uint RemovedColor = Premul(200, 20, 110, 255);   // 파랑: 사라진 장애물
    public static readonly uint OtherColor = Premul(110, 255, 210, 0);      // 노랑: 기타 값 변화
    public static readonly uint ReferenceColor = Premul(150, 0, 200, 220);  // 청록: 기준 맵 장애물

    public static void Fill(MapImage cur, MapImage reference, byte threshold, MapOverlayMode mode, IntRect chunk, uint[] dest)
    {
        byte[] a = cur.Data, b = reference.Data;
        int w = cur.Width, k = 0;
        for (int y = chunk.Y; y < chunk.Bottom; y++)
        {
            int row = y * w;
            for (int x = chunk.X; x < chunk.Right; x++, k++)
            {
                int i = row + x;
                dest[k] = mode switch
                {
                    MapOverlayMode.Diff => DiffColor(a[i], b[i], threshold),
                    MapOverlayMode.Reference => b[i] >= threshold ? ReferenceColor : 0u,
                    _ => 0u,
                };
            }
        }
    }

    public static DiffStats Count(MapImage cur, MapImage reference, byte threshold)
    {
        var st = new DiffStats();
        byte[] a = cur.Data, b = reference.Data;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            bool co = a[i] >= threshold, ro = b[i] >= threshold;
            if (co && !ro) st.Added++;
            else if (!co && ro) st.Removed++;
            else st.Other++;
        }
        return st;
    }

    /// <summary>값이 다른 픽셀을 254로 표시한 마스크 (변경 영역 묶음 검출용)</summary>
    public static MapImage CreateMask(MapImage cur, MapImage reference)
    {
        var mask = new MapImage(cur.Width, cur.Height);
        byte[] a = cur.Data, b = reference.Data, m = mask.Data;
        for (int i = 0; i < a.Length; i++) m[i] = a[i] != b[i] ? MapValues.Obstacle : (byte)0;
        return mask;
    }

    private static uint DiffColor(byte cur, byte refv, byte threshold)
    {
        if (cur == refv) return 0u;
        bool co = cur >= threshold, ro = refv >= threshold;
        if (co && !ro) return AddedColor;
        if (!co && ro) return RemovedColor;
        return OtherColor;
    }

    private static uint Premul(byte a, byte r, byte g, byte b) =>
        ((uint)a << 24) | ((uint)(r * a / 255) << 16) | ((uint)(g * a / 255) << 8) | (uint)(b * a / 255);
}

public sealed class MapStats
{
    private MapStats(long[] histogram) => Histogram = histogram;

    public long[] Histogram { get; }
    public long Unknown => Histogram[MapValues.Unknown];
    public long Free => Histogram[MapValues.Free];
    public long Obstacle => Histogram[MapValues.Obstacle];
    public long Invalid => Histogram[MapValues.Invalid];

    public long Probability
    {
        get
        {
            long n = 0;
            for (int v = 2; v <= 253; v++) n += Histogram[v];
            return n;
        }
    }

    public long CountAbove(int value)
    {
        long n = 0;
        for (int v = value + 1; v <= 255; v++) n += Histogram[v];
        return n;
    }

    public static MapStats Compute(MapImage map)
    {
        var h = new long[256];
        foreach (byte b in map.Data) h[b]++;
        return new MapStats(h);
    }

    /// <summary>두 맵의 다른 픽셀 수와 범위</summary>
    public static (long Changed, IntRect Bounds) Compare(MapImage a, MapImage b)
    {
        int w = a.Width;
        long n = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int i = 0; i < a.Data.Length; i++)
        {
            if (a.Data[i] == b.Data[i]) continue;
            n++;
            int x = i % w, y = i / w;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        return n == 0 ? (0, IntRect.Empty) : (n, new IntRect(minX, minY, maxX - minX + 1, maxY - minY + 1));
    }
}
