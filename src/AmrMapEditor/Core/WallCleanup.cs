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

/// <summary>고립 구역 찾기 결과</summary>
public sealed class IsolatedResult
{
    /// <summary>고립 구역 묶음 (넓은 순)</summary>
    public List<Blob> Groups { get; } = new();

    /// <summary>묶음별 Free · 낮은 확률값 픽셀 수 (Groups와 같은 순서)</summary>
    public List<int> OpenCounts { get; } = new();

    /// <summary>주행 공간 픽셀 수 (가장 크게 이어진 Free · 낮은 확률값 영역)</summary>
    public long MainArea { get; set; }
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
    /// 영역 안 장애물 픽셀을 벽 단위(덩어리)로 나눠 각각 직선으로 다시 그림.
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
            StraightenResult? r = StraightenCluster(clusterPts, thr, thickness, axisDeg, snapTolDeg, region, t);
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

    /// <summary>직선 성분 하나 (꺾인 벽이면 구간마다 하나)</summary>
    private sealed class WallSegment
    {
        public double Dx, Dy, Nx, Ny;            // 방향, 법선 (단위 벡터)
        public double Cn;                        // 새 중심선 위치 (법선 좌표)
        public double TMin, TMax;                // 새 직선의 방향 좌표 범위
        public int Thickness;
        public int Count;                        // 띠 찾기 단계의 점 수
        public readonly List<int> Candidates = new();   // 이 구간에 배정된 점 (덩어리 내 번호)
        public readonly List<int> Members = new();      // 그중 원래 벽 몸체로 판정된 점 (지울 대상)
        public double LocalTol;                  // 원래 벽 몸체 판정 폭 (국소 중심선 기준 ±)
        public Func<double, double> LocalCenter = _ => 0;   // 방향 좌표 → 원래 벽의 국소 중심 (법선 좌표)

        public double S(double px, double py) => px * Nx + py * Ny;
        public double T(double px, double py) => px * Dx + py * Dy;
    }

    /// <summary>
    /// 벽 덩어리 하나를 직선으로 다시 그림.
    /// 1) 방향 · 구간 찾기: 직교하는 두 방향의 띠로 나누고(ㄱ·ㄴ·T자), 같은 방향의 가까운 띠(드리프트 · 두꺼운 벽)는 한 벽으로 합침
    /// 2) 구간마다 국소 중심선을 따라가며 원래 벽 몸체를 판정 → 길고 미세하게 기울거나 어긋난 벽도 끝까지 지움
    /// 3) 지운 자리는 벽 바깥쪽 원래 배경(Free / Unknown)으로, 새 직선은 열(행)마다 같은 두께로 그림
    /// 4) 몸체가 아닌 조각: 붙어 있던 칸막이 등 큰 구조는 새 벽까지 다시 잇고, 작은 부스러기는 지움
    /// </summary>
    private static StraightenResult? StraightenCluster(List<(int X, int Y)> pts, byte thr, int thickness, double? axisDeg,
                                                        double snapTolDeg, PixelRegion region, EditTracker t)
    {
        MapImage map = t.Map;
        int w = map.Width, n = pts.Count;
        if (n < 5) return null;
        byte[] data = map.Data;

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

        // 덩어리 주변 격자: 점 번호 조회용 (0 = 없음, k+1)
        const int Pad = 24;
        IntRect box = new IntRect(minX - Pad, minY - Pad, maxX - minX + 1 + 2 * Pad, maxY - minY + 1 + 2 * Pad).Intersect(map.Bounds);
        var at = new int[box.Width * box.Height];
        for (int k = 0; k < n; k++) at[(pts[k].Y - box.Y) * box.Width + pts[k].X - box.X] = k + 1;
        int PointAt(int x, int y) => box.Contains(x, y) ? at[(y - box.Y) * box.Width + x - box.X] - 1 : -1;

        // 1) 구간
        List<WallSegment> segs = FindSegments(xs, ys, thickness, axisDeg, snapTolDeg, w + map.Height, out bool snapped);
        foreach (WallSegment seg in segs) FitSegment(seg, xs, ys, thickness);
        segs.RemoveAll(s => s.Members.Count == 0);
        if (segs.Count == 0) return null;
        if (segs.Count > 1) ExtendCorners(segs);

        var isMember = new bool[n];
        var owner = new WallSegment?[n];
        foreach (WallSegment seg in segs)
            foreach (int k in seg.Members)
            {
                isMember[k] = true;
                owner[k] = seg;
            }

        // 가장 가까운 구간 (법선 거리 기준, 방향 범위 안)
        WallSegment Nearest(double px, double py)
        {
            WallSegment best = segs[0];
            double bestD = double.MaxValue;
            foreach (WallSegment s in segs)
            {
                double tt = s.T(px, py);
                double d = Math.Abs(s.S(px, py) - s.Cn) + Math.Max(0, Math.Max(s.TMin - tt, tt - s.TMax));
                if (d < bestD) { bestD = d; best = s; }
            }
            return best;
        }

        // 지운 자리에 넣을 값: 새 중심선 기준으로 같은 쪽, 원래 벽 몸체 바깥의 원래 값 (Unknown이면 Unknown, 아니면 Free)
        byte Background(int x, int y, WallSegment s)
        {
            double px = x + 0.5, py = y + 0.5, tt = s.T(px, py);
            double side = s.S(px, py) - s.Cn >= 0 ? 1 : -1;
            double c = s.LocalCenter(tt), sp = s.S(px, py);
            for (double d = s.LocalTol + 1; d <= s.LocalTol + 8; d += 1)
            {
                double move = c + side * d - sp;
                int qx = (int)Math.Floor(px + s.Nx * move), qy = (int)Math.Floor(py + s.Ny * move);
                if ((uint)qx >= (uint)w || (uint)qy >= (uint)map.Height) return MapValues.Unknown;
                byte v = data[qy * w + qx];
                if (v == MapValues.Unknown) return MapValues.Unknown;
                if (v < thr) return MapValues.Free;
            }
            return MapValues.Free;
        }

        // 2) 몸체가 아닌 장애물 조각 분류 (원래 맵 기준으로 미리 결정)
        int minKeep = Math.Max(6, 2 * segs[0].Thickness);
        var debris = new List<int>();                        // 지울 부스러기 (점 번호)
        var bridgeFrom = new List<(int X, int Y)>();         // 새 벽까지 다시 이을 칸막이 경계 픽셀
        var seen = new bool[n];
        var comp = new List<int>();
        var stack = new Stack<int>();
        for (int k0 = 0; k0 < n; k0++)
        {
            if (isMember[k0] || seen[k0]) continue;
            comp.Clear();
            seen[k0] = true;
            stack.Push(k0);
            bool attached = false, external = false;
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                comp.Add(k);
                (int x, int y) = pts[k];
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)map.Height) continue;
                    int j = PointAt(nx, ny);
                    if (j < 0)
                    {
                        if (data[ny * w + nx] >= thr) external = true;   // 선택 영역 밖으로 이어지는 구조
                        continue;
                    }
                    if (isMember[j]) { attached = true; continue; }
                    if (seen[j]) continue;
                    seen[j] = true;
                    stack.Push(j);
                }
            }
            if (!attached) continue;   // 벽과 떨어진 구조물은 그대로
            if (!external && comp.Count < minKeep)
            {
                debris.AddRange(comp);   // 원래 벽의 톱니 · 잔여물
                continue;
            }
            foreach (int k in comp)
            {
                (int x, int y) = pts[k];
                bool edge = false;
                for (int dy = -1; dy <= 1 && !edge; dy++)
                for (int dx = -1; dx <= 1 && !edge; dx++)
                {
                    int j = PointAt(x + dx, y + dy);
                    if (j >= 0 && isMember[j]) edge = true;
                }
                if (edge) bridgeFrom.Add((x, y));
            }
        }

        // 3) 지우기: 몸체 · 부스러기 · 주변 확률값(장애물 기준값 미만) → 바깥쪽 배경값
        var erase = new Dictionary<int, byte>();
        void Mark(int x, int y)
        {
            int i = y * w + x;
            if (erase.ContainsKey(i)) return;
            double px = x + 0.5, py = y + 0.5;
            erase[i] = Background(x, y, Nearest(px, py));
        }
        foreach (WallSegment seg in segs)
            foreach (int k in seg.Members) Mark(pts[k].X, pts[k].Y);
        foreach (int k in debris) Mark(pts[k].X, pts[k].Y);
        for (int y = box.Y; y < box.Bottom; y++)
        for (int x = box.X; x < box.Right; x++)
        {
            byte v = data[y * w + x];
            if (v < 2 || v >= thr || !region.Contains(x, y)) continue;
            double px = x + 0.5, py = y + 0.5;
            bool near = false;
            for (int dy = -1; dy <= 1 && !near; dy++)
            for (int dx = -1; dx <= 1 && !near; dx++)
            {
                int j = PointAt(x + dx, y + dy);
                if (j >= 0 && isMember[j]) near = true;
            }
            if (!near)
            {
                WallSegment s = Nearest(px, py);
                double tt = s.T(px, py);
                near = Math.Abs(s.S(px, py) - s.Cn) <= s.Thickness / 2.0 + 1 && tt >= s.TMin - 1 && tt <= s.TMax + 1;
            }
            if (near) Mark(x, y);
        }
        foreach ((int i, byte v) in erase) t.SetIndex(i, v);

        // 4) 새 직선. 벽 끝에 붙어 있던 꺾인 팔 · 칸막이는 그 아래까지 벽을 늘려 모서리를 유지
        foreach ((int x, int y) in bridgeFrom)
        {
            WallSegment s = Nearest(x + 0.5, y + 0.5);
            double tt = s.T(x + 0.5, y + 0.5), reach = s.Thickness + 6;
            if (tt < s.TMin && tt >= s.TMin - reach) s.TMin = tt - 0.5;
            if (tt > s.TMax && tt <= s.TMax + reach) s.TMax = tt + 0.5;
        }
        double total = 0;
        foreach (WallSegment seg in segs)
        {
            DrawBand(t, seg);
            total += seg.TMax - seg.TMin + 1;
        }

        // 5) 칸막이 다시 잇기: 경계 픽셀에서 가장 가까운 새 벽까지 법선 방향으로 채움
        foreach ((int x, int y) in bridgeFrom)
        {
            double px = x + 0.5, py = y + 0.5;
            WallSegment s = Nearest(px, py);
            double off = s.S(px, py) - s.Cn;
            double dir = off > 0 ? -1 : 1;
            var path = new List<int>();
            bool hit = false;
            for (double step = 0.5; step <= Math.Abs(off) + 1; step += 0.5)
            {
                int qx = (int)Math.Floor(px + s.Nx * dir * step), qy = (int)Math.Floor(py + s.Ny * dir * step);
                if ((uint)qx >= (uint)w || (uint)qy >= (uint)map.Height) break;
                if (qx == x && qy == y) continue;
                int qi = qy * w + qx;
                if (data[qi] >= thr) { hit = true; break; }
                if (!path.Contains(qi)) path.Add(qi);
            }
            if (hit)
                foreach (int qi in path) t.SetIndex(qi, MapValues.Obstacle);
        }

        WallSegment main = segs[0];
        foreach (WallSegment s in segs) if (s.TMax - s.TMin > main.TMax - main.TMin) main = s;
        double angle = Math.Atan2(main.Dy, main.Dx) * 180 / Math.PI;
        return new StraightenResult
        {
            AngleDeg = angle > 90 ? angle - 180 : angle <= -90 ? angle + 180 : angle,
            LengthPx = total, Thickness = main.Thickness, PointCount = n, Snapped = snapped, SegmentCount = segs.Count,
        };
    }

    /// <summary>
    /// 벽 방향을 정하고 직교하는 두 방향의 띠로 구간을 나눔. 같은 방향의 가까운 띠는 합침.
    /// 구간이 하나뿐이면 덩어리 전체를 후보로 하는 단일 구간 (몸체 판정은 FitSegment에서)
    /// </summary>
    private static List<WallSegment> FindSegments(double[] xs, double[] ys, int thickness, double? axisDeg,
                                                  double snapTolDeg, int extent, out bool snapped)
    {
        int n = xs.Length;
        snapped = false;

        // 방향: 점이 충분하면 직교 방향 쌍(칸막이 · 톱니에 강함), 적으면 PCA.
        // 구간 나누기는 실제 벽 방향으로 하고, 주축 스냅은 다 나눈 뒤 구간마다 적용
        // (스냅한 방향으로 띠를 찾으면 길고 미세하게 기운 벽이 평행 조각 여러 개로 쪼개짐)
        double theta = n >= 20 ? DominantAngle(xs, ys, n, extent) : PcaAngle(xs, ys);
        double snapDelta = 0;
        if (axisDeg is double ax && Math.Abs(NormalizeAxis(theta - ax)) <= snapTolDeg)
        {
            snapDelta = NormalizeAxis(ax - theta);   // 주축(또는 주축+90°)에 정확히 맞추는 회전량
            snapped = true;
        }

        var segs = new List<WallSegment>();
        if (n >= 20)
        {
            int window = thickness > 0 ? thickness + 2 : 5;
            double mergeTol = (thickness > 0 ? thickness : 6) + 4;
            var used = new bool[n];
            for (int iter = 0; iter < 8; iter++)
            {
                WallSegment? best = null;
                foreach (double deg in new[] { theta, theta + 90 })
                {
                    WallSegment? seg = FindBand(xs, ys, used, deg, window);
                    if (seg != null && (best == null || seg.Count > best.Count)) best = seg;
                }
                // 톱니 · 잡음 같은 짧은 조각은 구간으로 보지 않음
                if (best == null || best.Count < Math.Max(8, n * 0.12) || best.TMax - best.TMin + 1 < 3 * window) break;

                // 같은 방향의 띠는 같은 벽: 가까이 붙은 줄(두꺼운 벽의 나머지 · 이중 벽) 또는
                // 길이 방향으로 이어지는 조각(드리프트 · 휨). 길이 방향으로 겹치며 떨어진 평행 벽(ㄷ자 양 팔)은 별개
                WallSegment? same = null;
                foreach (WallSegment s in segs)
                {
                    if (Math.Abs(s.Dx * best.Dx + s.Dy * best.Dy) < 0.9) continue;
                    double gap = Math.Abs(s.Cn - best.Cn);
                    double lenA = s.TMax - s.TMin + 1, lenB = best.TMax - best.TMin + 1;
                    double overlap = Math.Min(s.TMax, best.TMax) - Math.Max(s.TMin, best.TMin);
                    bool continuation = overlap < 0.3 * Math.Min(lenA, lenB) && gap <= Math.Max(mergeTol, 0.1 * (lenA + lenB));
                    if (gap <= mergeTol || continuation)
                    {
                        same = s;
                        break;
                    }
                }
                WallSegment target = same ?? best;
                for (int k = 0; k < n; k++)
                {
                    if (used[k]) continue;
                    if (Math.Abs(xs[k] * best.Nx + ys[k] * best.Ny - best.Cn) <= window / 2.0 + 0.5)
                    {
                        used[k] = true;
                        target.Candidates.Add(k);
                    }
                }
                if (same == null) segs.Add(best);
                else
                {
                    same.TMin = Math.Min(same.TMin, best.TMin);
                    same.TMax = Math.Max(same.TMax, best.TMax);
                }
            }
        }

        if (segs.Count < 2)
        {
            // 단일 직선: 방향은 띠가 찾은 방향(없으면 PCA 기준으로 theta / theta+90 중 가까운 쪽), 후보는 덩어리 전체
            double dirDeg;
            if (segs.Count == 1) dirDeg = Math.Atan2(segs[0].Dy, segs[0].Dx) * 180 / Math.PI;
            else
            {
                double pca = PcaAngle(xs, ys);
                dirDeg = n >= 20 ? (Math.Abs(LineAngleDiff(pca, theta)) <= 45 ? theta : theta + 90) : pca;
            }
            var single = new WallSegment();
            SetDirection(single, dirDeg);
            for (int k = 0; k < n; k++) single.Candidates.Add(k);
            segs = new List<WallSegment> { single };
        }

        if (snapped)
            foreach (WallSegment s in segs)
                SetDirection(s, Math.Atan2(s.Dy, s.Dx) * 180 / Math.PI + snapDelta);
        return segs;
    }

    private static void SetDirection(WallSegment s, double deg)
    {
        double r = deg * Math.PI / 180;
        s.Dx = Math.Cos(r);
        s.Dy = Math.Sin(r);
        // 정확히 축 방향이면 반올림 오차 제거 (열마다 같은 두께로 그리도록)
        if (Math.Abs(s.Dx) < 1e-12) s.Dx = 0;
        if (Math.Abs(s.Dy) < 1e-12) s.Dy = 0;
        s.Nx = -s.Dy;
        s.Ny = s.Dx;
    }

    private static double PcaAngle(double[] xs, double[] ys)
    {
        int n = xs.Length;
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
        return 0.5 * Math.Atan2(2 * sxy, sxx - syy) * 180 / Math.PI;
    }

    /// <summary>
    /// 구간의 두께 · 국소 중심선 · 몸체 점 · 새 중심선을 정함.
    /// 두께 = 길이 방향 1px 칸마다 점 개수의 중앙값 (칸막이 · 톱니 · 끊김에 강함).
    /// 국소 중심선 = 길이 방향 구간마다 점이 가장 몰린 띠의 중심 → 미세하게 기울거나 어긋난 긴 벽도 따라감
    /// </summary>
    private static void FitSegment(WallSegment seg, double[] xs, double[] ys, int thicknessParam)
    {
        int m = seg.Candidates.Count;
        if (m == 0) return;
        var tt = new double[m];
        var ss = new double[m];
        double tMin = double.MaxValue, tMax = double.MinValue;
        for (int i = 0; i < m; i++)
        {
            int k = seg.Candidates[i];
            tt[i] = seg.T(xs[k], ys[k]);
            ss[i] = seg.S(xs[k], ys[k]);
            if (tt[i] < tMin) tMin = tt[i];
            if (tt[i] > tMax) tMax = tt[i];
        }

        // 두께
        int th = thicknessParam;
        if (th <= 0)
        {
            var cnt = new int[(int)(tMax - tMin) + 2];
            for (int i = 0; i < m; i++) cnt[(int)(tt[i] - tMin)]++;
            var nonzero = new List<int>();
            foreach (int c in cnt) if (c > 0) nonzero.Add(c);
            nonzero.Sort();
            th = Math.Clamp(nonzero[nonzero.Count / 2], 1, 15);
        }

        // 국소 중심선
        double binW = Math.Max(10, 4 * th);
        int nb = (int)((tMax - tMin) / binW) + 1;
        var bins = new List<double>[nb];
        for (int b = 0; b < nb; b++) bins[b] = new List<double>();
        for (int i = 0; i < m; i++) bins[Math.Min(nb - 1, (int)((tt[i] - tMin) / binW))].Add(ss[i]);
        var center = new double[nb];
        var valid = new bool[nb];
        for (int b = 0; b < nb; b++)
            if (bins[b].Count >= Math.Max(2, th))
            {
                center[b] = DenseCenter(bins[b], th);
                valid[b] = true;
            }
        bool any = false;
        foreach (bool v in valid) any |= v;
        if (!any)
        {
            center[0] = DenseCenter(new List<double>(ss), th);
            valid[0] = true;
        }
        // 빈 구간은 이웃 값으로
        for (int b = 0; b < nb; b++)
        {
            if (valid[b]) continue;
            int l = b - 1, r = b + 1;
            while (l >= 0 && !valid[l]) l--;
            while (r < nb && !valid[r]) r++;
            center[b] = l >= 0 && r < nb ? center[l] + (center[r] - center[l]) * (b - l) / (r - l) : l >= 0 ? center[l] : center[r];
        }
        double t0 = tMin;
        double[] cs = center;
        seg.LocalCenter = tv =>
        {
            double f = (tv - t0) / binW - 0.5;
            if (f <= 0) return cs[0];
            if (f >= cs.Length - 1) return cs[^1];
            int i0 = (int)f;
            return cs[i0] + (cs[i0 + 1] - cs[i0]) * (f - i0);
        };

        // 몸체 점: 국소 중심선에서 두께/2 + 1.5 이내 (톱니 ±1px 허용)
        seg.LocalTol = th / 2.0 + 1.5;
        var memberS = new List<double>();
        double mMin = double.MaxValue, mMax = double.MinValue;
        for (int i = 0; i < m; i++)
        {
            if (Math.Abs(ss[i] - seg.LocalCenter(tt[i])) > seg.LocalTol) continue;
            seg.Members.Add(seg.Candidates[i]);
            memberS.Add(ss[i]);
            if (tt[i] < mMin) mMin = tt[i];
            if (tt[i] > mMax) mMax = tt[i];
        }
        if (seg.Members.Count == 0) return;

        // 새 중심선: 몸체 점 법선 좌표의 중앙값. 축 정렬 시 정확히 두께만큼 칠해지도록 반 픽셀 보정
        memberS.Sort();
        double cn = memberS[memberS.Count / 2];
        seg.Cn = th % 2 == 1 ? Math.Floor(cn) + 0.5 : Math.Round(cn);
        seg.Thickness = th;
        seg.TMin = mMin;
        seg.TMax = mMax;
    }

    /// <summary>값들 중 폭 th 안에 가장 많이 몰린 구간의 평균 (칸막이 · 잡음에 영향받지 않는 중심)</summary>
    private static double DenseCenter(List<double> values, int th)
    {
        values.Sort();
        int bestI = 0, bestJ = 0;
        for (int i = 0, j = 0; i < values.Count; i++)
        {
            while (j < values.Count && values[j] - values[i] <= th) j++;
            if (j - i > bestJ - bestI) { bestI = i; bestJ = j; }
        }
        double sum = 0;
        for (int k = bestI; k < bestJ; k++) sum += values[k];
        return sum / (bestJ - bestI);
    }

    /// <summary>직교하는 두 구간이 만나는 끝을 상대 벽 바깥면까지 연장 (ㄱ자 모서리가 비지 않도록)</summary>
    private static void ExtendCorners(List<WallSegment> segs)
    {
        foreach (WallSegment a in segs)
        foreach (WallSegment b in segs)
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
    }

    /// <summary>
    /// 중심선을 따라 열(가로에 가까우면) 또는 행마다 같은 개수의 픽셀을 칠함.
    /// 기울어진 벽도 두께가 들쭉날쭉하지 않고 1px씩 고르게 꺾인 계단이 됨 (축 정렬이면 정확히 두께만큼)
    /// </summary>
    private static void DrawBand(EditTracker t, WallSegment s)
    {
        double x0 = s.TMin * s.Dx + s.Cn * s.Nx, y0 = s.TMin * s.Dy + s.Cn * s.Ny;
        double x1 = s.TMax * s.Dx + s.Cn * s.Nx, y1 = s.TMax * s.Dy + s.Cn * s.Ny;
        const double eps = 1e-6;
        if (Math.Abs(s.Dx) >= Math.Abs(s.Dy))
        {
            int cnt = Math.Max(1, (int)Math.Round(s.Thickness / Math.Abs(s.Dx)));
            double slope = s.Dy / s.Dx;
            // 끝점이 속한 픽셀까지 (중심선 보정으로 끝점이 픽셀 중심에서 조금 밀려도 끝 열이 빠지지 않도록)
            int c0 = (int)Math.Floor(Math.Min(x0, x1) + eps), c1 = (int)Math.Floor(Math.Max(x0, x1) - eps);
            for (int c = c0; c <= c1; c++)
            {
                double yc = y0 + (c + 0.5 - x0) * slope;
                int r0 = (int)Math.Ceiling(yc - cnt / 2.0 - 0.5 - eps);
                for (int r = r0; r < r0 + cnt; r++) t.Set(c, r, MapValues.Obstacle);
            }
        }
        else
        {
            int cnt = Math.Max(1, (int)Math.Round(s.Thickness / Math.Abs(s.Dy)));
            double slope = s.Dx / s.Dy;
            int r0 = (int)Math.Floor(Math.Min(y0, y1) + eps), r1 = (int)Math.Floor(Math.Max(y0, y1) - eps);
            for (int r = r0; r <= r1; r++)
            {
                double xc = x0 + (r + 0.5 - y0) * slope;
                int c0 = (int)Math.Ceiling(xc - cnt / 2.0 - 0.5 - eps);
                for (int c = c0; c < c0 + cnt; c++) t.Set(c, r, MapValues.Obstacle);
            }
        }
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

    // ───────────── 고립 구역 ─────────────

    /// <summary>
    /// 주행 공간과 이어지지 않은 고립 구역을 찾음.
    /// 주행 공간 = Free · 장애물 기준값 미만 확률값이 4방향으로 이어진 가장 큰 영역 (Unknown은 통과 못 함).
    /// 고립 = 주행 공간이 아닌 Free · 확률값 + 주행 공간에 8방향으로 닿지 않는 장애물 덩어리
    /// (닫힌 방 안, 창문 너머 산란점, 벽 사이에 갇힌 값, 그 안의 물건 등).
    /// 산란점처럼 작은 조각은 서로 가까운(약 groupGap px) 것끼리 한 묶음. scope가 있으면 그 안에 완전히 들어오는 묶음만
    /// </summary>
    public static IsolatedResult FindIsolated(MapImage map, byte thr, PixelRegion? scope = null, int groupGap = 5)
    {
        const int SmallPiece = 64;   // 이보다 작은 조각만 가까운 것끼리 한 묶음
        int w = map.Width, h = map.Height, len = w * h;
        byte[] data = map.Data;
        var result = new IsolatedResult();
        bool Open(byte v) => v != MapValues.Unknown && v < thr;

        // 1) 주행 가능 영역 4방향 연결 → 가장 큰 것이 주행 공간
        var lab = new int[len];   // 0 = 없음, 1.. = 영역 번호
        var sizes = new List<int> { 0 };
        var stack = new Stack<int>();
        for (int s = 0; s < len; s++)
        {
            if (lab[s] != 0 || !Open(data[s])) continue;
            int id = sizes.Count, size = 0;
            lab[s] = id;
            stack.Push(s);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                size++;
                int x = i % w;
                if (x > 0 && lab[i - 1] == 0 && Open(data[i - 1])) { lab[i - 1] = id; stack.Push(i - 1); }
                if (x < w - 1 && lab[i + 1] == 0 && Open(data[i + 1])) { lab[i + 1] = id; stack.Push(i + 1); }
                if (i >= w && lab[i - w] == 0 && Open(data[i - w])) { lab[i - w] = id; stack.Push(i - w); }
                if (i < len - w && lab[i + w] == 0 && Open(data[i + w])) { lab[i + w] = id; stack.Push(i + w); }
            }
            sizes.Add(size);
        }
        if (sizes.Count == 1) return result;
        int main = 1;
        for (int id = 2; id < sizes.Count; id++) if (sizes[id] > sizes[main]) main = id;
        result.MainArea = sizes[main];

        // 2) 장애물 덩어리(8방향) 중 주행 공간에 닿는 것은 유지
        var keep = new bool[len];
        var seen = new bool[len];
        var comp = new List<int>();
        for (int s = 0; s < len; s++)
        {
            if (lab[s] == main) { keep[s] = true; continue; }
            if (seen[s] || data[s] < thr) continue;
            comp.Clear();
            bool touches = false;
            seen[s] = true;
            stack.Push(s);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                comp.Add(i);
                int x = i % w, y = i / w;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                    int j = ny * w + nx;
                    if (lab[j] == main) touches = true;
                    else if (!seen[j] && data[j] >= thr)
                    {
                        seen[j] = true;
                        stack.Push(j);
                    }
                }
            }
            if (touches) foreach (int i in comp) keep[i] = true;
        }

        // 3) 고립 픽셀을 8방향 덩어리로 → 가까운 덩어리끼리 묶음 (groupGap 칸 격자 이웃)
        int[] gid = lab;   // 영역 번호는 더 쓰지 않으므로 재사용 (큰 맵 메모리 절약). 0 = 고립 아님, 1.. = 덩어리 번호
        Array.Clear(gid);
        var parent = new List<int> { 0 };
        int Find(int a)
        {
            while (parent[a] != a) a = parent[a] = parent[parent[a]];
            return a;
        }
        void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
        }
        var compSize = new List<int> { 0 };
        bool Iso(int i) => data[i] != MapValues.Unknown && !keep[i];
        for (int s = 0; s < len; s++)
        {
            if (gid[s] != 0 || !Iso(s)) continue;
            int id = parent.Count;
            parent.Add(id);
            compSize.Add(0);
            gid[s] = id;
            stack.Push(s);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                compSize[id]++;
                int x = i % w, y = i / w;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                    int j = ny * w + nx;
                    if (gid[j] != 0 || !Iso(j)) continue;
                    gid[j] = id;
                    stack.Push(j);
                }
            }
        }
        if (parent.Count == 1) return result;

        // 산란점 같은 작은 조각끼리만 묶음 (닫힌 방처럼 큰 덩어리는 벽 너머 조각과 섞이지 않게 따로)
        int g = Math.Max(1, groupGap), cw = (w + g - 1) / g, ch = (h + g - 1) / g;
        var cell = new int[cw * ch];
        for (int i = 0; i < len; i++)
        {
            if (gid[i] == 0 || compSize[gid[i]] > SmallPiece) continue;
            int c = i / w / g * cw + i % w / g;
            if (cell[c] == 0) cell[c] = gid[i];
            else Union(cell[c], gid[i]);
        }
        for (int cy = 0; cy < ch; cy++)
        for (int cx = 0; cx < cw; cx++)
        {
            int a = cell[cy * cw + cx];
            if (a == 0) continue;
            if (cx + 1 < cw && cell[cy * cw + cx + 1] != 0) Union(a, cell[cy * cw + cx + 1]);
            if (cy + 1 < ch)
                for (int dx = -1; dx <= 1; dx++)
                    if ((uint)(cx + dx) < (uint)cw && cell[(cy + 1) * cw + cx + dx] != 0) Union(a, cell[(cy + 1) * cw + cx + dx]);
        }

        // 4) 묶음 만들기
        var index = new Dictionary<int, int>();
        var pix = new List<List<int>>();
        var open = new List<int>();
        var bounds = new List<(int X0, int Y0, int X1, int Y1)>();
        for (int i = 0; i < len; i++)
        {
            if (gid[i] == 0) continue;
            int root = Find(gid[i]);
            if (!index.TryGetValue(root, out int k))
            {
                k = pix.Count;
                index[root] = k;
                pix.Add(new List<int>());
                open.Add(0);
                bounds.Add((int.MaxValue, int.MaxValue, int.MinValue, int.MinValue));
            }
            pix[k].Add(i);
            if (data[i] < thr) open[k]++;
            int x = i % w, y = i / w;
            (int x0, int y0, int x1, int y1) = bounds[k];
            bounds[k] = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
        }

        var order = new List<int>();
        for (int k = 0; k < pix.Count; k++)
        {
            (int x0, int y0, int x1, int y1) = bounds[k];
            var b = new IntRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            if (scope != null)
            {
                if (!scope.Bounds.Contains(b)) continue;
                if (!scope.IsRect && pix[k].Exists(i => !scope.Contains(i % w, i / w))) continue;
            }
            order.Add(k);
        }
        order.Sort((a, b) => pix[b].Count.CompareTo(pix[a].Count));
        foreach (int k in order)
        {
            (int x0, int y0, int x1, int y1) = bounds[k];
            result.Groups.Add(new Blob(pix[k].Count, new IntRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1), pix[k].ToArray()));
            result.OpenCounts.Add(open[k]);
        }
        return result;
    }

    /// <summary>
    /// 방금 이은 벽(seeds) 바로 옆의 고립 구역 (틈을 이어 막힌 방 등).
    /// 주행 공간의 maxShare 이상인 큰 구역은 실제 주행 구역일 수 있어 제외
    /// </summary>
    public static List<Blob> FindIsolatedNear(MapImage map, byte thr, IEnumerable<int> seeds, double maxShare = 0.3)
    {
        IsolatedResult r = FindIsolated(map, thr);
        var found = new List<Blob>();
        if (r.Groups.Count == 0) return found;
        int w = map.Width, h = map.Height;
        var owner = new Dictionary<int, int>();
        for (int k = 0; k < r.Groups.Count; k++)
            foreach (int i in r.Groups[k].Pixels!) owner[i] = k;
        var hit = new bool[r.Groups.Count];
        foreach (int s in seeds)
        {
            int sx = s % w, sy = s / w;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = sx + dx, ny = sy + dy;
                if ((uint)nx < (uint)w && (uint)ny < (uint)h && owner.TryGetValue(ny * w + nx, out int k)) hit[k] = true;
            }
        }
        for (int k = 0; k < hit.Length; k++)
            if (hit[k] && r.OpenCounts[k] <= r.MainArea * maxShare) found.Add(r.Groups[k]);
        return found;
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
