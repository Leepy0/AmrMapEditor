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

        int off = map.Width + map.Height + 2;
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

    /// <summary>
    /// 영역 안 장애물 픽셀에 직선을 피팅해 지정 두께의 직선으로 다시 그림.
    /// thickness 0 = 자동. axisDeg가 있으면 주축(±90°) 근처 각도는 스냅
    /// </summary>
    public static StraightenResult? Straighten(PixelRegion region, byte thr, int thickness, double? axisDeg,
                                               double snapTolDeg, EditTracker t)
    {
        MapImage map = t.Map;
        IntRect scan = region.Bounds.Intersect(map.Bounds);
        var xs = new List<double>();
        var ys = new List<double>();
        for (int y = scan.Y; y < scan.Bottom; y++)
        for (int x = scan.X; x < scan.Right; x++)
            if (map.Get(x, y) >= thr && region.Contains(x, y))
            {
                xs.Add(x + 0.5);
                ys.Add(y + 0.5);
            }
        int n = xs.Count;
        if (n < 5) return null;

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

        // 기존 벽(띠 영역의 장애물·확률값) 지우기
        double band = thickness / 2.0 + 2;
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

    /// <summary>확률값(2~253) 정리: occThr 이상 → 장애물, freeMax 이하 → Free, 그 사이는 유지</summary>
    public static (int ToObstacle, int ToFree) CleanProbability(PixelRegion? region, byte occThr, byte freeMax, EditTracker t)
    {
        MapImage map = t.Map;
        int toObs = 0, toFree = 0;
        void Apply(int x, int y)
        {
            byte v = map.Get(x, y);
            if (!MapValues.IsProbability(v)) return;
            if (v >= occThr) { if (t.Set(x, y, MapValues.Obstacle)) toObs++; }
            else if (v <= freeMax) { if (t.Set(x, y, MapValues.Free)) toFree++; }
        }

        if (region != null) region.ForEach(Apply);
        else PixelRegion.FromRect(map.Bounds).ForEach(Apply);
        return (toObs, toFree);
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
