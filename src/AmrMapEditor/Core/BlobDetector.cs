using System;
using System.Collections.Generic;

namespace AmrMapEditor.Core;

/// <summary>8방향 연결된 장애물 덩어리</summary>
public sealed class Blob
{
    public Blob(int area, IntRect bounds, int[]? pixels)
    {
        Area = area;
        Bounds = bounds;
        Pixels = pixels;
    }

    public int Area { get; }
    public IntRect Bounds { get; }

    /// <summary>픽셀 인덱스 목록 (collectPixels=false로 검출하면 null)</summary>
    public int[]? Pixels { get; }

    public int MaxSide => Math.Max(Bounds.Width, Bounds.Height);
}

public static class BlobDetector
{
    private static readonly int[] DX = { -1, 0, 1, -1, 1, -1, 0, 1 };
    private static readonly int[] DY = { -1, -1, -1, 0, 0, 1, 1, 1 };

    /// <summary>
    /// 값 >= threshold 인 픽셀의 연결 덩어리 중 면적/크기 조건을 만족하는 것만 반환.
    /// region 지정 시 그 안에서 시작하고 경계가 region 안에 완전히 들어오는 덩어리만 대상
    /// (벽 일부가 선택 영역에 걸려 작은 덩어리로 오인되는 것 방지).
    /// </summary>
    public static List<Blob> Detect(MapImage map, byte threshold, int maxArea, int maxSide,
                                    PixelRegion? region, bool collectPixels = true)
    {
        int w = map.Width, h = map.Height;
        byte[] data = map.Data;
        IntRect scan = region != null ? region.Bounds.Intersect(map.Bounds) : map.Bounds;

        var visited = new bool[w * h];
        var result = new List<Blob>();
        var stack = new List<int>(1024);
        var pixels = new List<int>(256);

        for (int y = scan.Y; y < scan.Bottom; y++)
        for (int x = scan.X; x < scan.Right; x++)
        {
            int s = y * w + x;
            if (visited[s] || data[s] < threshold) continue;
            if (region != null && !region.Contains(x, y)) continue;

            visited[s] = true;
            stack.Clear();
            pixels.Clear();
            stack.Add(s);
            int area = 0, minX = x, maxX = x, minY = y, maxY = y;
            bool tooBig = false;

            while (stack.Count > 0)
            {
                int i = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                int cx = i % w, cy = i / w;
                area++;

                if (!tooBig)
                {
                    if (area > maxArea) { tooBig = true; pixels.Clear(); }
                    else if (collectPixels) pixels.Add(i);
                }

                if (cx < minX) minX = cx;
                if (cx > maxX) maxX = cx;
                if (cy < minY) minY = cy;
                if (cy > maxY) maxY = cy;

                for (int k = 0; k < 8; k++)
                {
                    int nx = cx + DX[k], ny = cy + DY[k];
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                    int ni = ny * w + nx;
                    if (visited[ni] || data[ni] < threshold) continue;
                    visited[ni] = true;
                    stack.Add(ni);
                }
            }

            if (tooBig) continue;
            var b = new IntRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
            if (Math.Max(b.Width, b.Height) > maxSide) continue;
            if (region != null && !InsideRegion(region, b, collectPixels ? pixels : null, w)) continue;
            result.Add(new Blob(area, b, collectPixels ? pixels.ToArray() : null));
        }

        return result;
    }

    private static bool InsideRegion(PixelRegion region, IntRect bounds, List<int>? pixels, int w)
    {
        if (!region.Bounds.Contains(bounds)) return false;
        if (region.IsRect || pixels == null) return true;
        foreach (int i in pixels)
            if (!region.Contains(i % w, i / w)) return false;
        return true;
    }

    /// <summary>클릭 위치의 덩어리 전체. 장애물 픽셀이 아니면 null</summary>
    public static Blob? ComponentAt(MapImage map, int x, int y, byte threshold)
    {
        if (!map.InBounds(x, y) || map.Get(x, y) < threshold) return null;

        int w = map.Width, h = map.Height;
        byte[] data = map.Data;
        var visited = new bool[w * h];
        var stack = new List<int> { y * w + x };
        var pixels = new List<int>();
        visited[y * w + x] = true;
        int minX = x, maxX = x, minY = y, maxY = y;

        while (stack.Count > 0)
        {
            int i = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            pixels.Add(i);
            int cx = i % w, cy = i / w;
            if (cx < minX) minX = cx;
            if (cx > maxX) maxX = cx;
            if (cy < minY) minY = cy;
            if (cy > maxY) maxY = cy;

            for (int k = 0; k < 8; k++)
            {
                int nx = cx + DX[k], ny = cy + DY[k];
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                int ni = ny * w + nx;
                if (visited[ni] || data[ni] < threshold) continue;
                visited[ni] = true;
                stack.Add(ni);
            }
        }

        return new Blob(pixels.Count, new IntRect(minX, minY, maxX - minX + 1, maxY - minY + 1), pixels.ToArray());
    }

    /// <summary>
    /// 덩어리를 지움. 검출 후 편집된 픽셀 보호를 위해 현재도 기준값 이상인 픽셀만 변경.
    /// 지운 자리는 주변 배경값: 둘레가 주로 Unknown(건물 밖 · 미탐색 구역)이면 Unknown, 아니면 Free
    /// (건물 밖 점을 Free로 지우면 그 자리에 고립된 Free가 남음).
    /// expand > 0 이면 주변 expand px 이내의 확률값(기준 미만) 픽셀도 같은 값으로 정리
    /// </summary>
    public static int Erase(Blob blob, EditTracker tracker, byte threshold, int expand)
    {
        if (blob.Pixels == null) return 0;
        MapImage map = tracker.Map;
        int w = map.Width;
        byte[] data = map.Data;
        byte fill = Background(blob, map, threshold);
        int n = 0;

        foreach (int i in blob.Pixels)
            if (data[i] >= threshold && tracker.SetIndex(i, fill)) n++;

        if (expand <= 0) return n;

        foreach (int i in blob.Pixels)
        {
            int cx = i % w, cy = i / w;
            for (int dy = -expand; dy <= expand; dy++)
            for (int dx = -expand; dx <= expand; dx++)
            {
                int nx = cx + dx, ny = cy + dy;
                if (!map.InBounds(nx, ny)) continue;
                byte v = data[ny * w + nx];
                if (MapValues.IsProbability(v) && v < threshold && tracker.Set(nx, ny, fill)) n++;
            }
        }
        return n;
    }

    /// <summary>덩어리 둘레(8방향 이웃 중 덩어리 밖 · 기준값 미만)에서 Unknown이 더 많으면 Unknown, 아니면 Free</summary>
    public static byte Background(Blob blob, MapImage map, byte threshold)
    {
        if (blob.Pixels == null || blob.Pixels.Length == 0) return MapValues.Free;
        int w = map.Width, h = map.Height;
        byte[] data = map.Data;
        var inBlob = new HashSet<int>(blob.Pixels);
        var counted = new HashSet<int>();
        int unknown = 0, other = 0;
        foreach (int i in blob.Pixels)
        {
            int cx = i % w, cy = i / w;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = cx + dx, ny = cy + dy;
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                int j = ny * w + nx;
                if (inBlob.Contains(j) || data[j] >= threshold || !counted.Add(j)) continue;
                if (data[j] == MapValues.Unknown) unknown++;
                else other++;
            }
        }
        return unknown > other ? MapValues.Unknown : MapValues.Free;
    }
}
