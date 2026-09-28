using System;
using System.Collections.Generic;

namespace AmrMapEditor.Core;

public sealed class DeskewResult
{
    public DeskewResult(MapImage image, double originX, double originY)
    {
        Image = image;
        OriginX = originX;
        OriginY = originY;
    }

    public MapImage Image { get; }
    public double OriginX { get; }
    public double OriginY { get; }
}

public sealed class StraightenResult
{
    public double AngleDeg { get; init; }
    public double LengthPx { get; init; }
    public int Thickness { get; init; }
    public int PointCount { get; init; }
    public bool Snapped { get; init; }

    /// <summary>다시 그린 직선 구간 수 (꺾인 벽이면 2 이상)</summary>
    public int SegmentCount { get; init; } = 1;
}

public sealed class RectifyResult
{
    public double AngleDeg { get; init; }
    public double WidthPx { get; init; }
    public double HeightPx { get; init; }
}

/// <summary>신규 맵 품질 정리</summary>
public static class WallCleanup
{
    // ───────────── 주축 각도 ─────────────

    /// <summary>
    /// 벽 방향(주축) 각도 추정 [-45°, 45°). 이미지 좌표(y 아래) 기준, x축에서 벽 방향까지 각도.
    /// 장애물 픽셀이 부족하면 null
    /// </summary>
    public static double? EstimateAxis(MapImage map, byte thr, PixelRegion? region)
    {
        int w = map.Width;
        var pts = new List<int>();
        IntRect scan = region?.Bounds.Intersect(map.Bounds) ?? map.Bounds;
        for (int y = scan.Y; y < scan.Bottom; y++)
        for (int x = scan.X; x < scan.Right; x++)
            if (map.Data[y * w + x] >= thr && (region == null || region.Contains(x, y))) pts.Add(y * w + x);
        if (pts.Count < 50) return null;

        int stride = Math.Max(1, pts.Count / 60000);
        int m = (pts.Count + stride - 1) / stride;
        var xs = new double[m];
        var ys = new double[m];
        for (int k = 0, j = 0; k < pts.Count; k += stride, j++)
        {
            xs[j] = pts[k] % w + 0.5;
            ys[j] = pts[k] / w + 0.5;
        }
        return DominantAngle(xs, ys, m, map.Width + map.Height);
    }

    /// <summary>
    /// 점들이 가장 날카롭게 모이는 직교 방향 쌍의 각도 [-45°, 45°).
    /// extent는 좌표 최댓값 합(히스토그램 크기)
    /// </summary>
    private static double DominantAngle(IReadOnlyList<double> xs, IReadOnlyList<double> ys, int m, int extent)
    {
        int off = extent + 2;
        var hu = new int[2 * off + 2];
        var hv = new int[2 * off + 2];

        double Sharpness(double deg)
        {
            double r = deg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
            Array.Clear(hu);
            Array.Clear(hv);
            for (int j = 0; j < m; j++)
            {
                hu[(int)Math.Floor(xs[j] * c + ys[j] * s) + off]++;
                hv[(int)Math.Floor(-xs[j] * s + ys[j] * c) + off]++;
            }
            double sum = 0;
            for (int k = 0; k < hu.Length; k++) sum += (double)hu[k] * hu[k] + (double)hv[k] * hv[k];
            return sum;
        }

        double best = -45, bestScore = double.MinValue;
        for (double d = -45; d < 45; d += 0.5)
        {
            double sc = Sharpness(d);
            if (sc > bestScore) { bestScore = sc; best = d; }
        }
        double center = best;
        for (double d = center - 0.5; d <= center + 0.5; d += 0.02)
        {
            double sc = Sharpness(d);
            if (sc > bestScore) { bestScore = sc; best = d; }
        }
        return NormalizeAxis(best);
    }

    /// <summary>각도를 [-45, 45) 범위로</summary>
    public static double NormalizeAxis(double deg)
    {
        double d = ((deg + 45) % 90 + 90) % 90 - 45;
        return Math.Abs(d) < 1e-9 ? 0 : d;
    }

    /// <summary>
    /// 벽이 수평/수직이 되도록 회전 (Nearest neighbor, 캔버스 확장, 빈 곳은 Unknown).
    /// 이미지 중심의 월드 좌표가 유지되도록 origin 재계산
    /// </summary>
    public static DeskewResult Deskew(MapImage src, double axisDeg, MapMeta meta)
    {
        double a = axisDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        int w = src.Width, h = src.Height;
        int nw = (int)Math.Ceiling(Math.Abs(w * c) + Math.Abs(h * s) - 1e-9);
        int nh = (int)Math.Ceiling(Math.Abs(w * s) + Math.Abs(h * c) - 1e-9);

        var dst = new MapImage(nw, nh) { MaxVal = src.MaxVal, Format = src.Format };
        dst.Comments.AddRange(src.Comments);
        double scx = w / 2.0, scy = h / 2.0, dcx = nw / 2.0, dcy = nh / 2.0;

        for (int y = 0; y < nh; y++)
        {
            double qy = y + 0.5 - dcy;
            for (int x = 0; x < nw; x++)
            {
                double qx = x + 0.5 - dcx;
                int sx = (int)Math.Floor(scx + qx * c - qy * s);
                int sy = (int)Math.Floor(scy + qx * s + qy * c);
                if ((uint)sx < (uint)w && (uint)sy < (uint)h) dst.Data[y * nw + x] = src.Data[sy * w + sx];
            }
        }

        double wcx = meta.OriginX + scx * meta.Resolution, wcy = meta.OriginY + scy * meta.Resolution;
        return new DeskewResult(dst, wcx - dcx * meta.Resolution, wcy - dcy * meta.Resolution);
    }

    // ───────────── 벽 직선화 ─────────────

    /// <summary>같은 벽의 끊긴 구간으로 볼 최대 간격(px). 이보다 멀리 떨어진 장애물 덩어리는 별개 벽으로 취급</summary>
    private const int StraightenClusterGap = 4;

    /// <summary>
    /// 영역 안 장애물 픽셀을 벽 단위(덩어리)로 나눠 각각 직선에 피팅해 지정 두께의 직선으로 다시 그림.
    /// 벽이 여러 개 포함된 영역이어도 각 벽을 독립적으로 인식해 처리한다.
    /// (같은 벽 안의 끊긴 구간은 <see cref="StraightenClusterGap"/> px 이내면 이어서 하나로 봄)
    /// thickness 0 = 벽마다 자동 추정. axisDeg가 있으면 주축(±90°) 근처 각도는 벽마다 스냅
    /// </summary>
    public static List<StraightenResult> Straighten(PixelRegion region, byte thr, int thickness, double? axisDeg,
                                                     double snapTolDeg, EditTracker t)
    {
        MapImage map = t.Map;
        var results = new List<StraightenResult>();
        IntRect scan = region.Bounds.Intersect(map.Bounds);
        if (scan.IsEmpty) return results;

        var pts = new List<(int X, int Y)>();
        for (int y = scan.Y; y < scan.Bottom; y++)
        for (int x = scan.X; x < scan.Right; x++)
            if (map.Get(x, y) >= thr && region.Contains(x, y))
                pts.Add((x, y));
        if (pts.Count < 5) return results;

        foreach (List<int> cluster in ClusterPoints(pts, scan, StraightenClusterGap))
        {
            if (cluster.Count < 5) continue;
            var clusterPts = new List<(int X, int Y)>(cluster.Count);
            foreach (int i in cluster) clusterPts.Add(pts[i]);
            StraightenResult? r = StraightenCluster(clusterPts, thickness, axisDeg, snapTolDeg, region, t);
            if (r != null) results.Add(r);
        }
        return results;
    }

    /// <summary>
    /// 점들을 gapTol px 이내로 가까운 것끼리 묶어 그룹으로 나눔 (같은 벽의 끊긴 구간은 이어붙이고,
    /// 멀리 떨어진 별개의 벽은 분리). scan 크기의 격자로 각 점의 위치를 찾아 주변 gapTol 범위를 훑는 방식
    /// </summary>
    private static List<List<int>> ClusterPoints(List<(int X, int Y)> pts, IntRect scan, int gapTol)
    {
        int w = scan.Width, h = scan.Height;
        var idxAt = new int[w * h];
        Array.Fill(idxAt, -1);
        for (int i = 0; i < pts.Count; i++)
        {
            (int x, int y) = pts[i];
            idxAt[(y - scan.Y) * w + (x - scan.X)] = i;
        }

        var visited = new bool[pts.Count];
        var groups = new List<List<int>>();
        var stack = new List<int>();

        for (int i = 0; i < pts.Count; i++)
        {
            if (visited[i]) continue;
            visited[i] = true;
            var group = new List<int> { i };
            stack.Clear();
            stack.Add(i);
            while (stack.Count > 0)
            {
                int cur = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                (int cx, int cy) = pts[cur];
                int lx = cx - scan.X, ly = cy - scan.Y;
                int y0 = Math.Max(0, ly - gapTol), y1 = Math.Min(h - 1, ly + gapTol);
                int x0 = Math.Max(0, lx - gapTol), x1 = Math.Min(w - 1, lx + gapTol);
                for (int ny = y0; ny <= y1; ny++)
                for (int nx = x0; nx <= x1; nx++)
                {
                    int ni = idxAt[ny * w + nx];
                    if (ni < 0 || visited[ni]) continue;
                    visited[ni] = true;
                    group.Add(ni);
                    stack.Add(ni);
                }
            }
            groups.Add(group);
        }
        return groups;
    }

    /// <summary>벽 하나(점 덩어리)에 직선을 피팅해 지정 두께로 다시 그림</summary>
    private static StraightenResult? StraightenCluster(List<(int X, int Y)> pts, int thickness, double? axisDeg,
                                                        double snapTolDeg, PixelRegion region, EditTracker t)
    {
        MapImage map = t.Map;
        int n = pts.Count;
        if (n < 5) return null;
        var xs = new double[n];
        var ys = new double[n];
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int k = 0; k < n; k++)
        {
            (int x, int y) = pts[k];
            xs[k] = x + 0.5;
            ys[k] = y + 0.5;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        // 꺾인 벽(ㄱ·ㄴ·T자): 직선 하나로 피팅하면 대각선이 되어 모서리가 사라지므로 직교 구간별로 다시 그림.
        // 지우는 범위는 이 벽 덩어리 주변으로 한정 (같은 선택 영역의 다른 벽 보호)
        int segPad = (thickness > 0 ? thickness : 15) / 2 + 6;
        IntRect clusterScan = new IntRect(minX - segPad, minY - segPad, maxX - minX + 1 + 2 * segPad, maxY - minY + 1 + 2 * segPad)
            .Intersect(map.Bounds);
        StraightenResult? multi = StraightenSegments(region, clusterScan, xs, ys, thickness, axisDeg, snapTolDeg, t);
        if (multi != null) return multi;

        // PCA
        double mx = 0, my = 0;
        for (int k = 0; k < n; k++) { mx += xs[k]; my += ys[k]; }
        mx /= n;
        my /= n;
        double sxx = 0, syy = 0, sxy = 0;
        for (int k = 0; k < n; k++)
        {
            double dx = xs[k] - mx, dy = ys[k] - my;
            sxx += dx * dx;
            syy += dy * dy;
            sxy += dx * dy;
        }
        double phi = 0.5 * Math.Atan2(2 * sxy, sxx - syy) * 180 / Math.PI;

        bool snapped = false;
        if (axisDeg is double ax)
        {
            double d = LineAngleDiff(phi, ax);
            if (Math.Abs(d) <= snapTolDeg) { phi -= d; snapped = true; }
            else
            {
                d = LineAngleDiff(phi, ax + 90);
                if (Math.Abs(d) <= snapTolDeg) { phi -= d; snapped = true; }
            }
        }

        double r = phi * Math.PI / 180, dxv = Math.Cos(r), dyv = Math.Sin(r);   // 방향
        double nxv = -dyv, nyv = dxv;                                             // 법선

        var sv = new double[n];
        double tmin = double.MaxValue, tmax = double.MinValue;
        for (int k = 0; k < n; k++)
        {
            sv[k] = xs[k] * nxv + ys[k] * nyv;
            double tt = xs[k] * dxv + ys[k] * dyv;
            if (tt < tmin) tmin = tt;
            if (tt > tmax) tmax = tt;
        }
        var sorted = (double[])sv.Clone();
        Array.Sort(sorted);
        double cn = sorted[n / 2];
        double length = tmax - tmin + 1;

        if (thickness <= 0)
        {
            int inBand = 0;
            for (int k = 0; k < n; k++) if (Math.Abs(sv[k] - cn) <= 4) inBand++;
            thickness = Math.Clamp((int)Math.Round(inBand / length), 1, 15);
        }
        // 축 정렬 시 정확히 thickness 픽셀이 되도록 중심선 위치 보정
        cn = thickness % 2 == 1 ? Math.Floor(cn) + 0.5 : Math.Round(cn);

        // 기존 벽(띠 영역의 장애물·확률값) 지우기. 이 벽 덩어리 주변으로만 범위를 한정해
        // 같은 선택 영역 안의 다른 벽(다른 덩어리)을 건드리지 않는다.
        double band = thickness / 2.0 + 2;
        int pad = (int)Math.Ceiling(band) + 2;
        IntRect scan = new IntRect(minX - pad, minY - pad, maxX - minX + 1 + 2 * pad, maxY - minY + 1 + 2 * pad)
            .Intersect(map.Bounds);
        for (int y = scan.Y; y < scan.Bottom; y++)
        for (int x = scan.X; x < scan.Right; x++)
        {
            if (!region.Contains(x, y)) continue;
            byte v = map.Get(x, y);
            if (v < 2) continue;
            double px = x + 0.5, py = y + 0.5;
            double ss = px * nxv + py * nyv - cn, tt = px * dxv + py * dyv;
            if (Math.Abs(ss) <= band && tt >= tmin - 2 && tt <= tmax + 2) t.Set(x, y, MapValues.Free);
        }

        // 새 직선 그리기
        // half는 축 정렬(수평/수직) 기준으로는 (thickness-1)/2로도 정확히 thickness줄이 선택되지만,
        // 스냅되지 않은 임의 각도에서는 픽셀 중심이 격자에 맞지 않아 폭이 1px 부족해진다.
        // thickness=1인 대각선 벽은 이 부족분 때문에 해당 폭 안에 픽셀 중심이 하나도 안 걸려
        // 아무것도 그려지지 않고(기존 벽은 지워졌으므로) 벽 전체가 사라지는 문제가 있었다.
        // thickness/2를 쓰면 축 정렬 시엔 폭이 그대로 유지되면서 임의 각도에서도 항상 최소 1줄은 그려진다.
        double half = thickness / 2.0 + 1e-9;
        IntRect box = LineBox(tmin, tmax, cn, half, dxv, dyv, nxv, nyv).Intersect(map.Bounds);
        for (int y = box.Y; y < box.Bottom; y++)
        for (int x = box.X; x < box.Right; x++)
        {
            double px = x + 0.5, py = y + 0.5;
            double ss = px * nxv + py * nyv - cn, tt = px * dxv + py * dyv;
            if (Math.Abs(ss) <= half && tt >= tmin - 1e-6 && tt <= tmax + 1e-6) t.Set(x, y, MapValues.Obstacle);
        }

        return new StraightenResult { AngleDeg = phi, LengthPx = length, Thickness = thickness, PointCount = n, Snapped = snapped };
    }

    private sealed class WallSegment
    {
        public double Dx, Dy, Nx, Ny;   // 방향, 법선
        public double Cn;               // 중심선 위치 (법선 좌표)
        public double TMin, TMax;       // 방향 좌표 범위
        public int Thickness;
        public int Count;
    }

    /// <summary>
    /// 직교하는 두 방향으로 벽 구간을 차례로 찾아 2개 이상이면 구간별로 다시 그리고,
    /// 서로 만나는 끝은 상대 벽 바깥면까지 늘려 각진 모서리를 유지. 구간이 1개면 null
    /// </summary>
    private static StraightenResult? StraightenSegments(PixelRegion region, IntRect scan, double[] xs, double[] ys,
                                                       int thickness, double? axisDeg, double snapTolDeg, EditTracker t)
    {
        int n = xs.Length;
        if (n < 20) return null;

        double theta = DominantAngle(xs, ys, n, t.Map.Width + t.Map.Height);
        bool snapped = false;
        if (axisDeg is double ax && Math.Abs(NormalizeAxis(theta - ax)) <= snapTolDeg)
        {
            theta = NormalizeAxis(ax);
            snapped = true;
        }

        int window = thickness > 0 ? thickness + 2 : 5;
        var used = new bool[n];
        var segments = new List<WallSegment>();
        for (int iter = 0; iter < 4; iter++)
        {
            WallSegment? best = null;
            foreach (double deg in new[] { theta, theta + 90 })
            {
                WallSegment? seg = FindBand(xs, ys, used, deg, window);
                if (seg != null && (best == null || seg.Count > best.Count)) best = seg;
            }
            // 톱니·잡음 같은 짧은 조각은 구간으로 보지 않음
            if (best == null || best.Count < Math.Max(8, n * 0.12) || best.TMax - best.TMin + 1 < 3 * window) break;

            for (int k = 0; k < n; k++)
            {
                double ss = xs[k] * best.Nx + ys[k] * best.Ny;
                if (Math.Abs(ss - best.Cn) <= window / 2.0 + 0.5) used[k] = true;
            }
            segments.Add(best);
        }
        if (segments.Count < 2) return null;

        // 구간별 두께와 중심선 (축 정렬 시 정확히 두께만큼 칠해지도록 보정)
        foreach (WallSegment seg in segments)
        {
            double length = seg.TMax - seg.TMin + 1;
            int th = thickness;
            if (th <= 0)
            {
                int inBand = 0;
                for (int k = 0; k < n; k++)
                {
                    double ss = xs[k] * seg.Nx + ys[k] * seg.Ny, tt = xs[k] * seg.Dx + ys[k] * seg.Dy;
                    if (Math.Abs(ss - seg.Cn) <= 4 && tt >= seg.TMin && tt <= seg.TMax) inBand++;
                }
                th = Math.Clamp((int)Math.Round(inBand / length), 1, 15);
            }
            seg.Thickness = th;
            seg.Cn = th % 2 == 1 ? Math.Floor(seg.Cn) + 0.5 : Math.Round(seg.Cn);
        }

        // 만나는 끝을 상대 벽 바깥면까지 연장 (ㄱ자 모서리가 비지 않도록)
        foreach (WallSegment a in segments)
        foreach (WallSegment b in segments)
        {
            if (ReferenceEquals(a, b) || Math.Abs(a.Dx * b.Dx + a.Dy * b.Dy) > 0.5) continue;
            // 두 중심선 교점: p = cnA·nA + cnB·nB (서로 직교)
            double px = a.Cn * a.Nx + b.Cn * b.Nx, py = a.Cn * a.Ny + b.Cn * b.Ny;
            double ta = px * a.Dx + py * a.Dy, tb = px * b.Dx + py * b.Dy;
            double tol = Math.Max(a.Thickness, b.Thickness) + 4;
            if (tb < b.TMin - tol || tb > b.TMax + tol) continue;   // 교점이 상대 벽 범위 밖
            if (ta < a.TMin - tol || ta > a.TMax + tol) continue;
            double halfB = b.Thickness / 2.0;
            if (ta >= a.TMax - tol) a.TMax = Math.Max(a.TMax, ta + halfB);
            if (ta <= a.TMin + tol) a.TMin = Math.Min(a.TMin, ta - halfB);
        }

        MapImage map = t.Map;
        for (int y = scan.Y; y < scan.Bottom; y++)
        for (int x = scan.X; x < scan.Right; x++)
        {
            if (!region.Contains(x, y) || map.Get(x, y) < 2) continue;
            double px = x + 0.5, py = y + 0.5;
            foreach (WallSegment seg in segments)
            {
                double ss = px * seg.Nx + py * seg.Ny - seg.Cn, tt = px * seg.Dx + py * seg.Dy;
                if (Math.Abs(ss) <= seg.Thickness / 2.0 + 2 && tt >= seg.TMin - 2 && tt <= seg.TMax + 2)
                {
                    t.Set(x, y, MapValues.Free);
                    break;
                }
            }
        }

        double total = 0;
        foreach (WallSegment seg in segments)
        {
            double half = seg.Thickness / 2.0 + 1e-9;   // 단일 직선과 같은 폭 규칙 (임의 각도에서도 최소 1줄)
            total += seg.TMax - seg.TMin + 1;
            IntRect box = LineBox(seg.TMin, seg.TMax, seg.Cn, half, seg.Dx, seg.Dy, seg.Nx, seg.Ny).Intersect(map.Bounds);
            for (int y = box.Y; y < box.Bottom; y++)
            for (int x = box.X; x < box.Right; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                double ss = px * seg.Nx + py * seg.Ny - seg.Cn, tt = px * seg.Dx + py * seg.Dy;
                if (Math.Abs(ss) <= half && tt >= seg.TMin - 1e-6 && tt <= seg.TMax + 1e-6) t.Set(x, y, MapValues.Obstacle);
            }
        }

        return new StraightenResult
        {
            AngleDeg = theta, LengthPx = total, Thickness = segments[0].Thickness, PointCount = n,
            Snapped = snapped, SegmentCount = segments.Count,
        };
    }

    /// <summary>deg 방향으로 남은 점이 가장 많이 모인 띠(폭 window) 찾기</summary>
    private static WallSegment? FindBand(double[] xs, double[] ys, bool[] used, double deg, int window)
    {
        double r = deg * Math.PI / 180, dx = Math.Cos(r), dy = Math.Sin(r), nx = -dy, ny = dx;
        var hist = new Dictionary<int, int>();
        for (int k = 0; k < xs.Length; k++)
        {
            if (used[k]) continue;
            int b = (int)Math.Floor(xs[k] * nx + ys[k] * ny);
            hist[b] = hist.TryGetValue(b, out int c) ? c + 1 : 1;
        }
        if (hist.Count == 0) return null;

        int bestStart = 0, bestCount = -1;
        foreach (int start in hist.Keys)
        {
            // start 칸이 띠의 가운데가 되도록 모든 위치 시험
            int from = start - window / 2, sum = 0;
            for (int b = from; b < from + window; b++) sum += hist.TryGetValue(b, out int c) ? c : 0;
            if (sum > bestCount) { bestCount = sum; bestStart = from; }
        }

        double sSum = 0, tMin = double.MaxValue, tMax = double.MinValue;
        int count = 0;
        for (int k = 0; k < xs.Length; k++)
        {
            if (used[k]) continue;
            double ss = xs[k] * nx + ys[k] * ny;
            int b = (int)Math.Floor(ss);
            if (b < bestStart || b >= bestStart + window) continue;
            double tt = xs[k] * dx + ys[k] * dy;
            sSum += ss;
            count++;
            if (tt < tMin) tMin = tt;
            if (tt > tMax) tMax = tt;
        }
        if (count == 0) return null;
        return new WallSegment { Dx = dx, Dy = dy, Nx = nx, Ny = ny, Cn = sSum / count, TMin = tMin, TMax = tMax, Count = count };
    }

    /// <summary>직선(방향 무관, 180° 주기) 각도 차이 (-90, 90]</summary>
    public static double LineAngleDiff(double a, double b)
    {
        double d = ((a - b) % 180 + 180) % 180;
        return d > 90 ? d - 180 : d;
    }

    private static IntRect LineBox(double tmin, double tmax, double cn, double half,
                                   double dx, double dy, double nx, double ny)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (double tt in new[] { tmin, tmax })
        foreach (double ss in new[] { cn - half - 1, cn + half + 1 })
        {
            double x = tt * dx + ss * nx, y = tt * dy + ss * ny;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        int x0 = (int)Math.Floor(minX) - 1, y0 = (int)Math.Floor(minY) - 1;
        int x1 = (int)Math.Ceiling(maxX) + 1, y1 = (int)Math.Ceiling(maxY) + 1;
        return new IntRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    // ───────────── 기둥 사각형화 ─────────────

    /// <summary>
    /// 덩어리를 최소 면적 사각형으로 치환 (주축 근처면 스냅).
    /// fill=false면 테두리만 그리고 내부는 유지. 주변 1px 확률값은 Free로 정리
    /// </summary>
    public static RectifyResult? Rectify(Blob blob, double? axisDeg, double snapTolDeg, bool fill, EditTracker t)
    {
        if (blob.Pixels == null || blob.Pixels.Length == 0) return null;
        MapImage map = t.Map;
        int w = map.Width, n = blob.Pixels.Length;
        var xs = new double[n];
        var ys = new double[n];
        for (int k = 0; k < n; k++)
        {
            xs[k] = blob.Pixels[k] % w + 0.5;
            ys[k] = blob.Pixels[k] / w + 0.5;
        }

        (double Area, double U0, double U1, double V0, double V1) Extent(double deg)
        {
            double r = deg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
            double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
            for (int k = 0; k < n; k++)
            {
                double u = xs[k] * c + ys[k] * s, v = -xs[k] * s + ys[k] * c;
                if (u < u0) u0 = u;
                if (u > u1) u1 = u;
                if (v < v0) v0 = v;
                if (v > v1) v1 = v;
            }
            return ((u1 - u0 + 1) * (v1 - v0 + 1), u0, u1, v0, v1);
        }

        double best = 0, bestArea = double.MaxValue;
        for (double d = 0; d < 90; d += 1)
        {
            double a = Extent(d).Area;
            if (a < bestArea - 1e-9) { bestArea = a; best = d; }
        }
        double center = best;
        for (double d = center - 1; d <= center + 1; d += 0.05)
        {
            double a = Extent(d).Area;
            if (a < bestArea - 1e-9) { bestArea = a; best = d; }
        }
        if (axisDeg is double ax)
        {
            // 건물 기둥은 주축 정렬이 기본: 한두 면만 스캔된 L자는 대각 방향 면적이 비슷하게 나오므로
            // 주축 사각형 면적이 최소의 1.25배 이내면 주축 채택
            double diff = NormalizeAxis(best - ax);
            if (Math.Abs(diff) <= snapTolDeg || Extent(ax).Area <= bestArea * 1.25) best = ax;
        }

        var e = Extent(best);
        double rr = best * Math.PI / 180, cs = Math.Cos(rr), sn = Math.Sin(rr);
        const double eps = 1e-6;
        IntRect scan = blob.Bounds.Inflate((int)Math.Ceiling(Math.Max(blob.Bounds.Width, blob.Bounds.Height) * 0.5) + 2)
            .Intersect(map.Bounds);

        for (int y = scan.Y; y < scan.Bottom; y++)
        for (int x = scan.X; x < scan.Right; x++)
        {
            double px = x + 0.5, py = y + 0.5;
            double u = px * cs + py * sn, v = -px * sn + py * cs;
            bool inside = u >= e.U0 - eps && u <= e.U1 + eps && v >= e.V0 - eps && v <= e.V1 + eps;
            if (inside)
            {
                bool edge = u < e.U0 + 1 - eps || u > e.U1 - 1 + eps || v < e.V0 + 1 - eps || v > e.V1 - 1 + eps;
                if (fill || edge) t.Set(x, y, MapValues.Obstacle);
            }
            else
            {
                bool near = u >= e.U0 - 1.5 && u <= e.U1 + 1.5 && v >= e.V0 - 1.5 && v <= e.V1 + 1.5;
                if (near && MapValues.IsProbability(map.Get(x, y))) t.Set(x, y, MapValues.Free);
            }
        }

        return new RectifyResult { AngleDeg = best, WidthPx = e.U1 - e.U0 + 1, HeightPx = e.V1 - e.V0 + 1 };
    }

    // ───────────── 벽 끊김 ─────────────

    /// <summary>
    /// 같은 행(열)에서 길이 minRun 이상인 장애물 구간 사이의 maxGap 이하 틈을 후보로 반환.
    /// 수평/수직 벽만 대상 (기울기 보정 후 사용 권장)
    /// </summary>
    public static List<Blob> FindGaps(MapImage map, byte thr, int maxGap, int minRun, PixelRegion? region)
    {
        int w = map.Width, h = map.Height;
        byte[] data = map.Data;
        var mask = new MapImage(w, h);

        void Scan(int count, int length, Func<int, int, int> index)
        {
            // 틈의 양옆 줄(수직 방향)이 절반 이상 장애물이면 관통된 끊김이 아니라 벽 가장자리 톱니
            bool IsNotch(int line, int g0, int g1)
            {
                foreach (int side in new[] { line - 1, line + 1 })
                {
                    if (side < 0 || side >= count) continue;
                    int occ = 0;
                    for (int g = g0; g <= g1; g++)
                        if (data[index(side, g)] >= thr) occ++;
                    if (occ * 2 >= g1 - g0 + 1) return true;
                }
                return false;
            }

            var runs = new List<(int S, int E)>();
            for (int line = 0; line < count; line++)
            {
                // 장애물 구간 수집
                runs.Clear();
                int pos = 0;
                while (pos < length)
                {
                    if (data[index(line, pos)] < thr) { pos++; continue; }
                    int start = pos;
                    while (pos < length && data[index(line, pos)] >= thr) pos++;
                    runs.Add((start, pos - 1));
                }
                int n = runs.Count;
                if (n < 2) continue;

                // 벽 길이는 1px 구멍(회전·톱니 흔적)을 이어서 계산
                var groupStart = new int[n];
                var groupEnd = new int[n];
                for (int i = 0; i < n; i++)
                    groupStart[i] = i > 0 && runs[i].S - runs[i - 1].E - 1 <= 1 ? groupStart[i - 1] : runs[i].S;
                for (int i = n - 1; i >= 0; i--)
                    groupEnd[i] = i < n - 1 && runs[i + 1].S - runs[i].E - 1 <= 1 ? groupEnd[i + 1] : runs[i].E;

                for (int i = 1; i < n; i++)
                {
                    int g0 = runs[i - 1].E + 1, g1 = runs[i].S - 1, gap = g1 - g0 + 1;
                    if (gap < 1 || gap > maxGap) continue;
                    if (runs[i - 1].E - groupStart[i - 1] + 1 < minRun || groupEnd[i] - runs[i].S + 1 < minRun) continue;
                    if (IsNotch(line, g0, g1)) continue;
                    for (int g = g0; g <= g1; g++)
                    {
                        int gi = index(line, g);
                        if (region == null || region.Contains(gi % w, gi / w)) mask.Data[gi] = MapValues.Obstacle;
                    }
                }
            }
        }

        Scan(h, w, (row, x) => row * w + x);   // 수평 벽
        Scan(w, h, (col, y) => y * w + col);   // 수직 벽
        return BlobDetector.Detect(mask, MapValues.Obstacle, int.MaxValue, int.MaxValue, null);
    }

    // ───────────── 확률값 / 외곽 ─────────────

    /// <summary>
    /// 확률값(2~253) 정리: occThr 이상 → 장애물, lowMax 이하 → lowTarget(Free 또는 Unknown), 그 사이는 유지
    /// </summary>
    public static (int ToObstacle, int ToLow) CleanProbability(PixelRegion? region, byte occThr, byte lowMax, byte lowTarget,
                                                               EditTracker t)
    {
        MapImage map = t.Map;
        int toObs = 0, toLow = 0;
        void Apply(int x, int y)
        {
            byte v = map.Get(x, y);
            if (!MapValues.IsProbability(v)) return;
            if (v >= occThr) { if (t.Set(x, y, MapValues.Obstacle)) toObs++; }
            else if (v <= lowMax) { if (t.Set(x, y, lowTarget)) toLow++; }
        }

        if (region != null) region.ForEach(Apply);
        else PixelRegion.FromRect(map.Bounds).ForEach(Apply);
        return (toObs, toLow);
    }

    // ───────────── 닫힌 내부 ─────────────

    /// <summary>
    /// seeds(방금 이은 벽 픽셀) 옆에서 벽으로 완전히 둘러싸인 내부 영역을 찾음.
    /// 내부 = 맵 가장자리에 닿지 않는 비장애물 영역(4방향 연결) + 그 안에 갇힌 장애물·확률값.
    /// 맵 전체 Free의 maxFreeShare 이상을 차지하는 영역은 AMR 주행 공간(건물 내부)으로 보고 제외
    /// </summary>
    public static List<int[]> FindEnclosedInteriors(MapImage map, byte thr, IEnumerable<int> seeds, double maxFreeShare = 0.3)
    {
        int w = map.Width, h = map.Height;
        byte[] data = map.Data;
        var visited = new bool[data.Length];
        var result = new List<int[]>();
        long totalFree = 0;
        foreach (byte v in data) if (v == MapValues.Free) totalFree++;

        var stack = new Stack<int>();
        var region = new List<int>();
        foreach (int seed in seeds)
        {
            int sx = seed % w, sy = seed / w;
            foreach ((int nx, int ny) in new[] { (sx - 1, sy), (sx + 1, sy), (sx, sy - 1), (sx, sy + 1) })
            {
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                int start = ny * w + nx;
                if (visited[start] || data[start] >= thr) continue;

                // 비장애물 영역 채우기 (4방향: 8방향으로 이어진 대각선 벽도 막힘)
                region.Clear();
                bool border = false;
                long free = 0;
                int minX = nx, maxX = nx, minY = ny, maxY = ny;
                visited[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    region.Add(i);
                    int x = i % w, y = i / w;
                    if (data[i] == MapValues.Free) free++;
                    if (x == 0 || y == 0 || x == w - 1 || y == h - 1) border = true;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    if (x > 0) Visit(i - 1);
                    if (x < w - 1) Visit(i + 1);
                    if (y > 0) Visit(i - w);
                    if (y < h - 1) Visit(i + w);
                }
                if (border || (totalFree > 0 && free > totalFree * maxFreeShare)) continue;

                result.Add(WithHoles(region, minX, minY, maxX, maxY));
            }
        }
        return result;

        void Visit(int j)
        {
            if (visited[j] || data[j] >= thr) return;
            visited[j] = true;
            stack.Push(j);
        }

        // 영역 안에 섬처럼 갇힌 장애물도 내부에 포함: 영역 경계 상자 바깥에서 8방향으로 닿지 않는 곳
        int[] WithHoles(List<int> inside, int x0, int y0, int x1, int y1)
        {
            x0--; y0--; x1++; y1++;   // 가장자리에 닿지 않으므로 맵 안
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            var inR = new bool[bw * bh];
            foreach (int i in inside) inR[(i / w - y0) * bw + i % w - x0] = true;
            var outside = new bool[bw * bh];
            var q = new Stack<int>();
            for (int x = 0; x < bw; x++) { Seed(x); Seed((bh - 1) * bw + x); }
            for (int y = 0; y < bh; y++) { Seed(y * bw); Seed(y * bw + bw - 1); }
            while (q.Count > 0)
            {
                int k = q.Pop(), kx = k % bw, ky = k / bw;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int ax = kx + dx, ay = ky + dy;
                    if ((uint)ax < (uint)bw && (uint)ay < (uint)bh) Seed(ay * bw + ax);
                }
            }
            var all = new List<int>(inside);
            for (int k = 0; k < inR.Length; k++)
                if (!inR[k] && !outside[k]) all.Add((y0 + k / bw) * w + x0 + k % bw);
            return all.ToArray();

            void Seed(int k)
            {
                if (inR[k] || outside[k]) return;
                outside[k] = true;
                q.Push(k);
            }
        }
    }

    /// <summary>inside 영역 바깥을 모두 Unknown으로 (외벽 너머 산란점 정리)</summary>
    public static int ClearOutside(PixelRegion inside, EditTracker t)
    {
        MapImage map = t.Map;
        int n = 0;
        for (int y = 0; y < map.Height; y++)
        for (int x = 0; x < map.Width; x++)
            if (!inside.Contains(x, y) && map.Get(x, y) != MapValues.Unknown && t.Set(x, y, MapValues.Unknown)) n++;
        return n;
    }
}
