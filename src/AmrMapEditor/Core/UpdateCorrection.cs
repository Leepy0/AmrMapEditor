using System;
using System.Collections.Generic;
using System.Linq;

namespace AmrMapEditor.Core;

public static class MapAlign
{
    /// <summary>aligned[x, y] = source[x + dx, y + dy]. 원본 범위 밖은 Unknown</summary>
    public static MapImage Shift(MapImage source, int width, int height, int dx, int dy)
    {
        var dst = new MapImage(width, height) { MaxVal = source.MaxVal, Format = source.Format };
        for (int y = 0; y < height; y++)
        {
            int sy = y + dy;
            if ((uint)sy >= (uint)source.Height) continue;
            for (int x = 0; x < width; x++)
            {
                int sx = x + dx;
                if ((uint)sx < (uint)source.Width) dst.Data[y * width + x] = source.Data[sy * source.Width + sx];
            }
        }
        return dst;
    }

    /// <summary>
    /// 두 yaml origin으로 기준 맵 오프셋 계산 (ROS 규칙, 해상도가 같아야 함).
    /// 결과는 Shift(dx, dy)에 그대로 사용
    /// </summary>
    public static (int Dx, int Dy)? OffsetFromMeta(MapMeta current, int currentHeight, MapMeta reference, int referenceHeight)
    {
        double res = current.Resolution;
        if (res <= 0 || Math.Abs(res - reference.Resolution) > res * 1e-6) return null;
        int dx = (int)Math.Round((current.OriginX - reference.OriginX) / res);
        int dy = (int)Math.Round(referenceHeight - currentHeight + (reference.OriginY - current.OriginY) / res);
        return (dx, dy);
    }
}

public sealed class OffsetResult
{
    public int Dx { get; init; }
    public int Dy { get; init; }
    public double AngleDeg { get; init; }
    public int PointCount { get; init; }

    /// <summary>보정 후 기준 장애물과 1px 이내로 겹치는 비율</summary>
    public double InlierRatio { get; init; }

    public double MeanBefore { get; init; }
    public double MeanAfter { get; init; }
}

/// <summary>기존 맵(기준)에 업데이트가 덧그려진 맵 보정</summary>
public static class UpdateCorrection
{
    /// <summary>
    /// 두 맵(같은 크기) 벽 불일치 비율: 상대 맵이 알고 있는 곳(Unknown 아님)에 놓인 장애물 중
    /// 상대 맵 장애물과 1px 이내에 없는 비율. 같은 좌표계에서 업데이트한 맵이면 작고, 따로 그린 맵이면 큼.
    /// 비교할 장애물이 없으면 1
    /// </summary>
    public static double ObstacleMismatch(MapImage a, MapImage b, byte thr)
    {
        long total = 0, miss = 0;
        Count(a, b);
        Count(b, a);
        return total == 0 ? 1 : (double)miss / total;

        void Count(MapImage p, MapImage q)
        {
            int w = p.Width, h = p.Height;
            byte[] pd = p.Data, qd = q.Data;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (pd[i] < thr || qd[i] == MapValues.Unknown) continue;
                total++;
                bool near = false;
                for (int dy = -1; dy <= 1 && !near; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx < (uint)w && (uint)ny < (uint)h && qd[ny * w + nx] >= thr)
                    {
                        near = true;
                        break;
                    }
                }
                if (!near) miss++;
            }
        }
    }

    /// <summary>업데이트 영역 밖에서 기준 맵과 다른 픽셀 수</summary>
    public static long CountOutside(MapImage cur, MapImage reference, bool[] areaMask)
    {
        long n = 0;
        for (int i = 0; i < cur.Data.Length; i++)
            if (!areaMask[i] && cur.Data[i] != reference.Data[i]) n++;
        return n;
    }

    /// <summary>업데이트 영역 밖의 변경을 모두 기준 맵 값으로 되돌림</summary>
    public static int RevertOutside(MapImage reference, bool[] areaMask, EditTracker t)
    {
        byte[] cur = t.Map.Data, rf = reference.Data;
        int n = 0;
        for (int i = 0; i < cur.Length; i++)
            if (!areaMask[i] && cur[i] != rf[i] && t.SetIndex(i, rf[i])) n++;
        return n;
    }

    /// <summary>
    /// 추가된 장애물을 기준 맵 장애물에 맞추는 이동량(dx, dy, θ) 추정.
    /// 결과 이동량을 업데이트 내용에 적용하면 기준 맵과 겹침. 추가 장애물이 20px 미만이면 null
    /// </summary>
    public static OffsetResult? EstimateOffset(MapImage cur, MapImage reference, byte thr, bool[]? areaMask,
                                               int radius, double maxAngleDeg)
    {
        int w = cur.Width;
        byte[] a = cur.Data, b = reference.Data;

        var all = new List<int>();
        for (int i = 0; i < a.Length; i++)
            if (a[i] >= thr && b[i] < thr && (areaMask == null || areaMask[i])) all.Add(i);
        if (all.Count < 20) return null;

        int stride = Math.Max(1, all.Count / 20000);
        int m = (all.Count + stride - 1) / stride;
        var px = new double[m];
        var py = new double[m];
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        double cx = 0, cy = 0;
        for (int k = 0, j = 0; k < all.Count; k += stride, j++)
        {
            int x = all[k] % w, y = all[k] / w;
            px[j] = x + 0.5;
            py[j] = y + 0.5;
            cx += px[j];
            cy += py[j];
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        cx /= m;
        cy /= m;

        const double cap = 5.0;
        int margin = radius + (int)cap + 4;
        IntRect rect = new IntRect(minX - margin, minY - margin, maxX - minX + 1 + 2 * margin, maxY - minY + 1 + 2 * margin)
            .Intersect(cur.Bounds);
        DistanceField df = DistanceField.Compute(rect, (x, y) => b[y * w + x] >= thr);

        double Score(int dx, int dy, double angDeg)
        {
            double rad = angDeg * Math.PI / 180, cs = Math.Cos(rad), sn = Math.Sin(rad), sum = 0;
            for (int j = 0; j < m; j++)
            {
                double ux = px[j] - cx, uy = py[j] - cy;
                int tx = (int)Math.Floor(cx + ux * cs - uy * sn + dx);
                int ty = (int)Math.Floor(cy + ux * sn + uy * cs + dy);
                sum += Math.Min(df.DistanceAt(tx, ty), cap);
            }
            return sum / m;
        }

        // 1단계: 회전 없이 정수 이동 전체 탐색
        double best = double.MaxValue;
        int bdx = 0, bdy = 0;
        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            double s = Score(dx, dy, 0);
            if (s < best - 1e-9 || (Math.Abs(s - best) < 1e-9 && Math.Abs(dx) + Math.Abs(dy) < Math.Abs(bdx) + Math.Abs(bdy)))
            {
                best = s;
                bdx = dx;
                bdy = dy;
            }
        }

        // 2단계: 주변 이동 + 회전 세분 탐색
        double bang = 0;
        if (maxAngleDeg > 0)
        {
            int steps = (int)Math.Round(maxAngleDeg / 0.1);
            int sdx = bdx, sdy = bdy;
            for (int k = -steps; k <= steps; k++)
            {
                double ang = k * 0.1;
                if (k == 0) continue;
                for (int dy = sdy - 2; dy <= sdy + 2; dy++)
                for (int dx = sdx - 2; dx <= sdx + 2; dx++)
                {
                    double s = Score(dx, dy, ang);
                    if (s < best - 1e-6)
                    {
                        best = s;
                        bdx = dx;
                        bdy = dy;
                        bang = ang;
                    }
                }
            }
        }

        // 최종 겹침 비율
        double r = bang * Math.PI / 180, c2 = Math.Cos(r), s2 = Math.Sin(r);
        int inl = 0;
        for (int j = 0; j < m; j++)
        {
            double ux = px[j] - cx, uy = py[j] - cy;
            int tx = (int)Math.Floor(cx + ux * c2 - uy * s2 + bdx);
            int ty = (int)Math.Floor(cy + ux * s2 + uy * c2 + bdy);
            if (df.DistanceAt(tx, ty) <= 1.0) inl++;
        }

        return new OffsetResult
        {
            Dx = bdx,
            Dy = bdy,
            AngleDeg = bang,
            PointCount = all.Count,
            InlierRatio = (double)inl / m,
            MeanBefore = Score(0, 0, 0),
            MeanAfter = best,
        };
    }

    /// <summary>
    /// 업데이트 내용(기준과 다른 픽셀)을 (dx, dy)만큼 옮겨 기준 맵에 다시 합성.
    /// keepReferenceObstacles면 옮긴 값이 기준 맵 장애물을 지우지 않음. 이동한 픽셀 수 반환
    /// </summary>
    public static int Realign(MapImage reference, bool[]? areaMask, int dx, int dy, byte thr,
                              bool keepReferenceObstacles, EditTracker t)
    {
        MapImage cur = t.Map;
        int w = cur.Width, h = cur.Height;
        byte[] a = cur.Data, b = reference.Data;

        var idx = new List<int>();
        var vals = new List<byte>();
        for (int i = 0; i < a.Length; i++)
        {
            if (areaMask != null && !areaMask[i]) continue;
            if (a[i] == b[i]) continue;
            idx.Add(i);
            vals.Add(a[i]);
        }

        // 원위치는 기준 맵 값으로
        foreach (int i in idx) t.SetIndex(i, b[i]);

        // 이동 위치에 업데이트 값 기록
        int moved = 0;
        for (int k = 0; k < idx.Count; k++)
        {
            int x = idx[k] % w + dx, y = idx[k] / w + dy;
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
            int ti = y * w + x;
            if (keepReferenceObstacles && b[ti] >= thr && vals[k] < thr) continue;
            t.Set(x, y, vals[k]);
            moved++;
        }
        return moved;
    }

    /// <summary>추가된 장애물 중 기준 맵 장애물에서 maxDist px 이내인 덩어리 (이중 벽 후보)</summary>
    public static List<Blob> FindDuplicateWalls(MapImage cur, MapImage reference, byte thr, bool[]? areaMask,
                                                double maxDist, int minArea)
    {
        int w = cur.Width;
        byte[] a = cur.Data, b = reference.Data;

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] < thr || b[i] >= thr || (areaMask != null && !areaMask[i])) continue;
            int x = i % w, y = i / w;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        if (maxX < 0) return new List<Blob>();

        int margin = (int)Math.Ceiling(maxDist) + 2;
        IntRect rect = new IntRect(minX - margin, minY - margin, maxX - minX + 1 + 2 * margin, maxY - minY + 1 + 2 * margin)
            .Intersect(cur.Bounds);
        DistanceField df = DistanceField.Compute(rect, (x, y) => b[y * w + x] >= thr);

        var mask = new MapImage(cur.Width, cur.Height);
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            int i = y * w + x;
            if (a[i] < thr || b[i] >= thr || (areaMask != null && !areaMask[i])) continue;
            if (df.DistanceAt(x, y) <= maxDist) mask.Data[i] = MapValues.Obstacle;
        }

        var region = PixelRegion.FromRect(new IntRect(minX, minY, maxX - minX + 1, maxY - minY + 1));
        return BlobDetector.Detect(mask, MapValues.Obstacle, int.MaxValue, int.MaxValue, region)
            .Where(x => x.Area >= minArea)
            .ToList();
    }

    /// <summary>덩어리를 기준 맵 값으로 복원. 주변 expand px의 확률값 변경분도 함께 복원</summary>
    public static int RestoreFromReference(Blob blob, MapImage reference, EditTracker t, byte thr, int expand)
    {
        if (blob.Pixels == null) return 0;
        MapImage cur = t.Map;
        int w = cur.Width;
        byte[] a = cur.Data, b = reference.Data;
        int n = 0;

        foreach (int i in blob.Pixels)
            if (t.SetIndex(i, b[i])) n++;

        if (expand <= 0) return n;
        foreach (int i in blob.Pixels)
        {
            int cx = i % w, cy = i / w;
            for (int dy = -expand; dy <= expand; dy++)
            for (int dx = -expand; dx <= expand; dx++)
            {
                int nx = cx + dx, ny = cy + dy;
                if (!cur.InBounds(nx, ny)) continue;
                int ni = ny * w + nx;
                if (a[ni] != b[ni] && MapValues.IsProbability(a[ni]) && a[ni] < thr && t.Set(nx, ny, b[ni])) n++;
            }
        }
        return n;
    }
}
