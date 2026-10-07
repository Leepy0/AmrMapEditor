using System;
using System.Collections.Generic;
using System.Threading;

namespace AmrMapEditor.Core;

/// <summary>
/// 맞출 맵(두 번째 맵) → 현재 맵 좌표 변환. q = R(θ)·p + T.
/// p, q는 픽셀 연속 좌표 (픽셀 (i, j)의 중심 = (i + 0.5, j + 0.5)), 각도는 이미지 좌표(y 아래) 기준
/// </summary>
public readonly record struct MapPose(double AngleDeg, double Tx, double Ty)
{
    public static MapPose Identity => new(0, 0, 0);

    private double Rad => AngleDeg * Math.PI / 180;

    public PointD ToBase(PointD p)
    {
        double c = Math.Cos(Rad), s = Math.Sin(Rad);
        return new PointD(c * p.X - s * p.Y + Tx, s * p.X + c * p.Y + Ty);
    }

    public PointD ToMoving(PointD q)
    {
        double c = Math.Cos(Rad), s = Math.Sin(Rad), x = q.X - Tx, y = q.Y - Ty;
        return new PointD(c * x + s * y, -s * x + c * y);
    }

    /// <summary>현재 맵 좌표의 pivot을 중심으로 delta만큼 회전</summary>
    public MapPose RotateAround(PointD pivot, double deltaDeg)
    {
        double r = deltaDeg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        double x = Tx - pivot.X, y = Ty - pivot.Y;
        return new MapPose(NormalizeDeg(AngleDeg + deltaDeg), c * x - s * y + pivot.X, s * x + c * y + pivot.Y);
    }

    public MapPose Translate(double dx, double dy) => this with { Tx = Tx + dx, Ty = Ty + dy };

    /// <summary>각도를 (-180, 180]으로</summary>
    public static double NormalizeDeg(double d)
    {
        d %= 360;
        if (d <= -180) d += 360;
        if (d > 180) d -= 360;
        return d;
    }
}

/// <summary>자동 정렬 결과</summary>
public sealed class AlignResult
{
    public MapPose Start { get; init; }
    public MapPose Pose { get; init; }

    /// <summary>맞출 맵 장애물 점(표본) 수</summary>
    public int Points { get; init; }

    /// <summary>현재 맵의 알려진 영역(Unknown 아님)에 떨어진 점 수 = 겹치는 부분</summary>
    public int OverlapPoints { get; init; }

    /// <summary>겹치는 점 중 현재 맵 장애물과 1.5px 이내인 비율 (정렬 전 / 후)</summary>
    public double MatchBefore { get; init; }
    public double MatchAfter { get; init; }

    public double OverlapRatio => Points == 0 ? 0 : (double)OverlapPoints / Points;

    /// <summary>점수가 비슷한 다른 후보 (반복 구조에서 한 칸 옆 등). 비어 있으면 뚜렷한 정답 하나</summary>
    public IReadOnlyList<MapPose> Alternatives { get; init; } = Array.Empty<MapPose>();
}

/// <summary>자동 정렬 진행 상황 (Stage 1 준비 · 2 거친 탐색 · 3 세밀 탐색, Percent 0~100)</summary>
public readonly record struct AlignProgress(int Stage, double Percent);

/// <summary>맞출 맵과 현재 맵 비교 결과 (맞출 맵 픽셀 기준)</summary>
public sealed class AlignDiff
{
    public const byte None = 0, Same = 1, Added = 2, Removed = 3, NewArea = 4;

    public AlignDiff(byte[] kind) => Kind = kind;

    /// <summary>맞출 맵 픽셀별 분류</summary>
    public byte[] Kind { get; }

    public int SameCount { get; set; }

    /// <summary>새 맵에만 있는 장애물 (현재 맵에서는 비어 있음)</summary>
    public int AddedCount { get; set; }

    /// <summary>현재 맵에만 있는 장애물 (새 맵에서는 비어 있음)</summary>
    public int RemovedCount { get; set; }

    /// <summary>현재 맵이 Unknown이거나 범위 밖인 곳을 새 맵이 채운 픽셀</summary>
    public int NewAreaCount { get; set; }
}

/// <summary>맵 합치기 결과</summary>
public sealed class MergeResult
{
    public MergeResult(MapImage image, int offsetX, int offsetY, int fromMoving)
    {
        Image = image;
        OffsetX = offsetX;
        OffsetY = offsetY;
        FromMoving = fromMoving;
    }

    public MapImage Image { get; }

    /// <summary>현재 맵 픽셀 (0, 0)이 결과 맵에서 놓이는 위치</summary>
    public int OffsetX { get; }
    public int OffsetY { get; }

    /// <summary>맞출 맵에서 가져온 픽셀 수</summary>
    public int FromMoving { get; }
}

public enum MergeRule
{
    /// <summary>현재 맵 우선: 현재 맵이 Unknown인 곳만 새 맵으로 채움 (이중 벽이 생기지 않음)</summary>
    BaseFirst,

    /// <summary>새 맵 우선: 새 맵이 알고 있는 곳은 새 맵 값</summary>
    MovingFirst,

    /// <summary>장애물 합침: 어느 한쪽이라도 장애물이면 장애물, 나머지는 현재 맵 우선</summary>
    ObstacleUnion,

    /// <summary>지정 영역(선택 영역) 안은 새 맵 우선, 밖은 현재 맵 우선</summary>
    RegionMovingFirst,
}

/// <summary>두 맵 정렬(위치 맞추기) · 비교 · 합치기</summary>
public static class MapRegistration
{
    private const int MaxPoints = 40000;
    private const double MatchDist = 1.5;

    /// <summary>맞출 맵 네 모서리를 현재 맵 좌표로</summary>
    public static PointD[] Footprint(MapImage moving, MapPose pose) => Footprint(new IntRect(0, 0, moving.Width, moving.Height), pose);

    /// <summary>맞출 맵 픽셀 범위 r의 네 모서리를 현재 맵 좌표로 (화면 외곽선 · 반영 범위)</summary>
    public static PointD[] Footprint(IntRect r, MapPose pose) => new[]
    {
        pose.ToBase(new PointD(r.X, r.Y)), pose.ToBase(new PointD(r.Right, r.Y)),
        pose.ToBase(new PointD(r.Right, r.Bottom)), pose.ToBase(new PointD(r.X, r.Bottom)),
    };

    /// <summary>맞출 맵이 차지하는 현재 맵 좌표 정수 범위 (현재 맵 밖으로 나갈 수 있음)</summary>
    public static IntRect FootprintBounds(MapImage moving, MapPose pose) => FootprintBounds(new IntRect(0, 0, moving.Width, moving.Height), pose);

    /// <summary>맞출 맵 픽셀 범위 r이 차지하는 현재 맵 좌표 정수 범위</summary>
    public static IntRect FootprintBounds(IntRect r, MapPose pose)
    {
        PointD[] c = Footprint(r, pose);
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (PointD p in c)
        {
            x0 = Math.Min(x0, p.X);
            y0 = Math.Min(y0, p.Y);
            x1 = Math.Max(x1, p.X);
            y1 = Math.Max(y1, p.Y);
        }
        int ix0 = (int)Math.Floor(x0), iy0 = (int)Math.Floor(y0);
        return new IntRect(ix0, iy0, (int)Math.Ceiling(x1) - ix0, (int)Math.Ceiling(y1) - iy0);
    }

    /// <summary>
    /// Unknown이 아닌 픽셀들의 볼록 껍질 (맞출 맵 연속 좌표, 픽셀 모서리 기준).
    /// ROS 맵 둘레의 넓은 Unknown 여백을 빼고 실제 내용만 감쌈. 모두 Unknown이면 이미지 네 모서리
    /// </summary>
    public static PointD[] KnownHull(MapImage m)
    {
        int w = m.Width, h = m.Height;
        byte[] d = m.Data;
        var pts = new List<PointD>();
        for (int y = 0; y < h; y++)
        {
            int row = y * w, first = -1, last = -1;
            for (int x = 0; x < w; x++)
                if (d[row + x] != MapValues.Unknown)
                {
                    first = x;
                    break;
                }
            if (first < 0) continue;
            for (int x = w - 1; x >= first; x--)
                if (d[row + x] != MapValues.Unknown)
                {
                    last = x;
                    break;
                }
            pts.Add(new PointD(first, y));
            pts.Add(new PointD(first, y + 1));
            pts.Add(new PointD(last + 1, y));
            pts.Add(new PointD(last + 1, y + 1));
        }
        return pts.Count == 0 ? Footprint(m, MapPose.Identity) : ConvexHull(pts);
    }

    /// <summary>볼록 껍질 (monotone chain, 반시계 아님 · 순서만 일관)</summary>
    private static PointD[] ConvexHull(List<PointD> p)
    {
        p.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        var hull = new PointD[2 * p.Count];
        int k = 0;
        for (int i = 0; i < p.Count; i++)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], p[i]) <= 0) k--;
            hull[k++] = p[i];
        }
        for (int i = p.Count - 2, t = k + 1; i >= 0; i--)
        {
            while (k >= t && Cross(hull[k - 2], hull[k - 1], p[i]) <= 0) k--;
            hull[k++] = p[i];
        }
        return hull[..Math.Max(1, k - 1)];
    }

    private static double Cross(PointD o, PointD a, PointD b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>맞출 맵 좌표 점들을 현재 맵 좌표로</summary>
    public static PointD[] Transform(IReadOnlyList<PointD> pts, MapPose pose)
    {
        var r = new PointD[pts.Count];
        for (int i = 0; i < r.Length; i++) r[i] = pose.ToBase(pts[i]);
        return r;
    }

    /// <summary>점들을 담는 정수 범위</summary>
    public static IntRect BoundsOf(IReadOnlyList<PointD> pts)
    {
        if (pts.Count == 0) return IntRect.Empty;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (PointD p in pts)
        {
            x0 = Math.Min(x0, p.X);
            y0 = Math.Min(y0, p.Y);
            x1 = Math.Max(x1, p.X);
            y1 = Math.Max(y1, p.Y);
        }
        // 부동소수 오차로 한 줄이 더 붙지 않도록
        int ix0 = (int)Math.Floor(x0 + 1e-6), iy0 = (int)Math.Floor(y0 + 1e-6);
        return new IntRect(ix0, iy0, (int)Math.Ceiling(x1 - 1e-6) - ix0, (int)Math.Ceiling(y1 - 1e-6) - iy0);
    }

    /// <summary>맵 이미지 전체 사각형 꼭짓점 (Unknown 여백 포함)</summary>
    public static PointD[] FullRect(MapImage m) =>
        new[] { new PointD(0, 0), new PointD(m.Width, 0), new PointD(m.Width, m.Height), new PointD(0, m.Height) };

    /// <summary>합친 맵 범위 (현재 맵 좌표): 현재 맵 전체 + 맞출 맵 (movingHull = FullRect 전체 · KnownHull 알려진 부분만)</summary>
    public static IntRect MergedBounds(MapImage baseMap, IReadOnlyList<PointD> movingHull, MapPose pose)
    {
        IntRect fp = BoundsOf(Transform(movingHull, pose));
        if (fp.IsEmpty) return baseMap.Bounds;
        int x0 = Math.Min(0, fp.X), y0 = Math.Min(0, fp.Y);
        int x1 = Math.Max(baseMap.Width, fp.Right), y1 = Math.Max(baseMap.Height, fp.Bottom);
        return new IntRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// 두 yaml origin으로 초기 자세 (같은 SLAM 좌표계로 그린 맵일 때만 의미 있음, 해상도 같아야 함).
    /// 해상도가 다르면 null
    /// </summary>
    public static MapPose? FromMeta(MapMeta baseMeta, int baseHeight, MapMeta movingMeta, int movingHeight)
    {
        double res = baseMeta.Resolution;
        if (res <= 0 || Math.Abs(res - movingMeta.Resolution) > res * 1e-6) return null;
        // 맞출 맵 연속 좌표 p → 월드 → 현재 맵 연속 좌표 q (y축 반대)
        double tx = (movingMeta.OriginX - baseMeta.OriginX) / res;
        double ty = baseHeight - movingHeight + (baseMeta.OriginY - movingMeta.OriginY) / res;
        return new MapPose(0, tx, ty);
    }

    /// <summary>
    /// 같은 지점 두 쌍(맞출 맵 m1, m2 ↔ 현재 맵 b1, b2)으로 회전 + 이동 계산 (축척 없음).
    /// scaleRatio = 현재 맵 두 점 거리 / 맞출 맵 두 점 거리 (1에서 멀면 잘못 찍은 점)
    /// </summary>
    public static MapPose FromTwoPoints(PointD m1, PointD m2, PointD b1, PointD b2, out double scaleRatio)
    {
        double am = Math.Atan2(m2.Y - m1.Y, m2.X - m1.X), ab = Math.Atan2(b2.Y - b1.Y, b2.X - b1.X);
        double lm = Math.Sqrt((m2.X - m1.X) * (m2.X - m1.X) + (m2.Y - m1.Y) * (m2.Y - m1.Y));
        double lb = Math.Sqrt((b2.X - b1.X) * (b2.X - b1.X) + (b2.Y - b1.Y) * (b2.Y - b1.Y));
        scaleRatio = lm > 1e-9 ? lb / lm : 0;
        double ang = (ab - am) * 180 / Math.PI;
        // 두 점 중점이 겹치도록 이동
        var rot = new MapPose(MapPose.NormalizeDeg(ang), 0, 0);
        PointD mc = rot.ToBase(new PointD((m1.X + m2.X) / 2, (m1.Y + m2.Y) / 2));
        return rot with { Tx = (b1.X + b2.X) / 2 - mc.X, Ty = (b1.Y + b2.Y) / 2 - mc.Y };
    }

    /// <summary>
    /// 두 맵의 벽 방향(주축)이 평행해지도록 현재 각도에 더할 회전량 (-45°, 45°].
    /// 남는 90° 단위 방향은 사용자가 정함. 장애물이 부족하면 null
    /// </summary>
    public static double? AxisSnapDelta(MapImage baseMap, MapImage moving, byte thr, double currentAngleDeg)
    {
        double? ab = WallCleanup.EstimateAxis(baseMap, thr, null), am = WallCleanup.EstimateAxis(moving, thr, null);
        if (ab == null || am == null) return null;
        return WallCleanup.NormalizeAxis(ab.Value - (am.Value + currentAngleDeg));
    }

    /// <summary>
    /// 대략 맞춘 자세(start)에서 이동 ±radius px, 회전 ±angleRange° 안을 거친 → 세밀 순으로 탐색해
    /// 두 맵이 가장 잘 맞는 자세를 찾음.
    /// - 특징점은 장애물 경계 픽셀 (속이 찬 랙 · 기둥도 모서리로 각도를 잡음). 현재 맵이 Unknown인 곳은 평가 제외
    /// - 거친 탐색 후보는 두 기준에서 고루 뽑음: 시작 자세에서 겹친 점 고정(대략 맞춘 위치 근처 우선) / 위치마다 겹친 점 합
    /// - 세밀 탐색은 후보 위치에서 겹친 점을 고정해서 (겹침 증감에 휘둘리지 않게) 0.02° · 0.25px까지
    /// - 최종 비교 = 후보 위치에서 겹친 점당 평균: 새 맵 경계 ↔ 현재 맵 경계 일치(양방향) − 새 맵 Free가 현재 맵 벽 위에 놓인 수
    /// - 반복 구조(랙 열 등)처럼 점수가 거의 같은 후보가 여럿이면 대략 맞춘 위치에 가까운 쪽을 고르고 나머지는 Alternatives로
    /// 맞출 맵 장애물이 부족하거나 겹치는 범위가 없으면 null
    /// </summary>
    public static AlignResult? Refine(MapImage baseMap, MapImage moving, byte thr, MapPose start, int radius, double angleRange,
                                      IProgress<AlignProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new AlignProgress(1, 0));
        int mw = moving.Width, mh = moving.Height, bw = baseMap.Width, bh = baseMap.Height;
        byte[] md = moving.Data, bd = baseMap.Data;

        // 1) 평가 범위와 현재 맵 경계 거리장 (chamfer, 3 = 1px). -1 = Unknown
        IntRect fp = FootprintBounds(moving, start);
        double half = Math.Sqrt((double)mw * mw + (double)mh * mh) / 2;
        int margin = radius + (int)Math.Ceiling(half * Math.Sin(Math.Min(angleRange, 90) * Math.PI / 180)) + 4;
        IntRect rect = new IntRect(fp.X - margin, fp.Y - margin, fp.Width + 2 * margin, fp.Height + 2 * margin)
            .Intersect(baseMap.Bounds);
        if (rect.IsEmpty) return null;
        int rw = rect.Width, rh = rect.Height;
        DistanceField bdf = DistanceField.Compute(rect, (x, y) => bd[y * bw + x] >= thr && IsEdge(bd, bw, bh, x, y, thr));
        var cost = new short[rw * rh];
        var bObs = new bool[rw * rh];
        var baseEdges = new List<PointD>();
        for (int y = 0; y < rh; y++)
        for (int x = 0; x < rw; x++)
        {
            int gx = rect.X + x, gy = rect.Y + y;
            byte v = bd[gy * bw + gx];
            if (v == MapValues.Unknown) { cost[y * rw + x] = -1; continue; }
            bObs[y * rw + x] = v >= thr;
            double d = bdf.DistanceAt(gx, gy);
            cost[y * rw + x] = (short)(d >= 10000 ? 30000 : Math.Min(30000, Math.Round(d * 3)));
            if (d == 0) baseEdges.Add(new PointD(gx + 0.5, gy + 0.5));
        }
        DistanceField mdf = DistanceField.Compute(moving.Bounds, (x, y) => md[y * mw + x] >= thr && IsEdge(md, mw, mh, x, y, thr));

        // 2) 새 맵 점 (시작 자세로 현재 맵 좌표에 놓음): 경계 E, Free F
        var eAll = new List<PointD>();
        var fAll = new List<PointD>();
        for (int y = 0; y < mh; y++)
        for (int x = 0; x < mw; x++)
        {
            byte v = md[y * mw + x];
            if (v >= thr) { if (IsEdge(md, mw, mh, x, y, thr)) eAll.Add(start.ToBase(new PointD(x + 0.5, y + 0.5))); }
            else if (v == MapValues.Free) fAll.Add(start.ToBase(new PointD(x + 0.5, y + 0.5)));
        }
        if (eAll.Count < 30) return null;
        List<PointD> e = Thin(eAll, MaxPoints), f = Thin(fAll, MaxPoints / 2), bEdge = Thin(baseEdges, MaxPoints);
        int n = e.Count;

        // 회전 중심 = 시작 자세에서 겹친 경계점의 무게중심 (없으면 전체). 점 좌표는 중심 기준으로 보관
        bool InOverlap(double qx, double qy)
        {
            int x = (int)Math.Floor(qx) - rect.X, y = (int)Math.Floor(qy) - rect.Y;
            return (uint)x < (uint)rw && (uint)y < (uint)rh && cost[y * rw + x] >= 0;
        }
        double cx = 0, cy = 0, ocx = 0, ocy = 0;
        int on = 0;
        foreach (PointD p in e)
        {
            cx += p.X;
            cy += p.Y;
            if (!InOverlap(p.X, p.Y)) continue;
            ocx += p.X;
            ocy += p.Y;
            on++;
        }
        if (on < 20) return null;   // 시작 자세에서 겹치는 부분이 거의 없음
        var pivot = new PointD(ocx / on, ocy / on);
        var ex = new double[n];
        var ey = new double[n];
        double maxR = 0;
        for (int j = 0; j < n; j++)
        {
            ex[j] = e[j].X - pivot.X;
            ey[j] = e[j].Y - pivot.Y;
            maxR = Math.Max(maxR, Math.Sqrt(ex[j] * ex[j] + ey[j] * ey[j]));
        }

        float[] Kernel(double sigma)
        {
            var lut = new float[30001];
            for (int c = 0; c <= 30000; c++) lut[c] = (float)Math.Max(0, 1 - c / 3.0 / sigma);
            return lut;
        }
        var ix = new int[n];
        var iy = new int[n];
        void Rotate(double deg, double ox = 0, double oy = 0)
        {
            double r = deg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
            for (int j = 0; j < n; j++)
            {
                ix[j] = (int)Math.Floor(c * ex[j] - s * ey[j] + pivot.X + ox) - rect.X;
                iy[j] = (int)Math.Floor(s * ex[j] + c * ey[j] + pivot.Y + oy) - rect.Y;
            }
        }
        // set이 null이면 그 위치에서 겹친 점 전부, 아니면 고정된 점 집합의 합 (밖으로 나간 점은 0점)
        double Score(int[]? set, int dx, int dy, float[] lut)
        {
            double sum = 0;
            if (set == null)
            {
                for (int j = 0; j < n; j++) Add(j);
            }
            else
            {
                foreach (int j in set) Add(j);
            }
            return sum;

            void Add(int j)
            {
                int x = ix[j] + dx, y = iy[j] + dy;
                if ((uint)x >= (uint)rw || (uint)y >= (uint)rh) return;
                short c = cost[y * rw + x];
                if (c >= 0) sum += lut[c];
            }
        }
        int[] OverlapSet(int dx, int dy, int max)
        {
            var list = new List<int>();
            for (int j = 0; j < n; j++)
            {
                int x = ix[j] + dx, y = iy[j] + dy;
                if ((uint)x < (uint)rw && (uint)y < (uint)rh && cost[y * rw + x] >= 0) list.Add(j);
            }
            if (list.Count <= max) return list.ToArray();
            int stride = (list.Count + max - 1) / max;
            var sub = new List<int>();
            for (int k = 0; k < list.Count; k += stride) sub.Add(list[k]);
            return sub.ToArray();
        }

        // 3) 거친 탐색: 두 기준(시작 자세 고정 집합 / 위치마다 겹친 점)에서 후보를 고루
        ct.ThrowIfCancellationRequested();
        progress?.Report(new AlignProgress(2, 10));
        int tStep = Math.Max(1, radius / 10);
        double aStep = angleRange > 0 ? Math.Max(0.25, angleRange / 16) : 1;
        var byFixed = new List<(double Score, double Ang, int Dx, int Dy)>();
        var byOpen = new List<(double Score, double Ang, int Dx, int Dy)>();
        {
            Rotate(0);
            int[] fixedSet = OverlapSet(0, 0, 6000);
            int[] sample = Enumerable(n, 6000);
            float[] lut = Kernel(Math.Max(2, tStep * 0.9));
            for (double a = -angleRange; a <= angleRange + 1e-9; a += aStep)
            {
                ct.ThrowIfCancellationRequested();
                if (angleRange > 0) progress?.Report(new AlignProgress(2, 10 + 35 * (a + angleRange) / (2 * angleRange)));
                Rotate(a);
                for (int dy = -radius; dy <= radius; dy += tStep)
                for (int dx = -radius; dx <= radius; dx += tStep)
                {
                    byFixed.Add((Score(fixedSet, dx, dy, lut), a, dx, dy));
                    byOpen.Add((Score(sample, dx, dy, lut), a, dx, dy));
                }
                if (angleRange <= 0) break;
            }
        }
        var seeds = new List<(double Score, double Ang, int Dx, int Dy)>();
        void PickSeeds(List<(double Score, double Ang, int Dx, int Dy)> list, int count)
        {
            list.Sort((p, q) => q.Score.CompareTo(p.Score));
            int added = 0;
            foreach (var c in list)
            {
                // 세밀 탐색이 다시 훑는 범위(이동 ±1칸, 회전 ±2칸) 안이면 같은 봉우리
                if (seeds.Exists(s0 => Math.Abs(s0.Dx - c.Dx) <= tStep && Math.Abs(s0.Dy - c.Dy) <= tStep &&
                                       Math.Abs(s0.Ang - c.Ang) <= 2 * aStep + 1e-9)) continue;
                seeds.Add(c);
                if (++added >= count) break;
            }
        }
        PickSeeds(byFixed, 8);
        PickSeeds(byOpen, 6);

        // 4) 후보마다 세밀 탐색: 그 위치에서 겹친 점 고정 → 0.1° · 1px → 0.02° · 0.25px
        var refined = new List<(double Ang, double Dx, double Dy)>();
        {
            float[] lut2 = Kernel(1.5), lut3 = Kernel(1.0);
            for (int si = 0; si < seeds.Count; si++)
            {
                var seed = seeds[si];
                ct.ThrowIfCancellationRequested();
                progress?.Report(new AlignProgress(3, 45 + 50.0 * si / seeds.Count));
                Rotate(seed.Ang);
                int[] set = OverlapSet(seed.Dx, seed.Dy, 8000);
                if (set.Length < 20) continue;
                (double Sc, double Ang, int Dx, int Dy) best = (double.MinValue, seed.Ang, seed.Dx, seed.Dy);
                double span = angleRange > 0 ? 2 * aStep : 0;
                for (double a = seed.Ang - span; a <= seed.Ang + span + 1e-9; a += 0.1)
                {
                    if (Math.Abs(a) > angleRange + 1e-9) continue;
                    Rotate(a);
                    for (int dy = seed.Dy - tStep; dy <= seed.Dy + tStep; dy++)
                    for (int dx = seed.Dx - tStep; dx <= seed.Dx + tStep; dx++)
                    {
                        double sc = Score(set, dx, dy, lut2);
                        if (sc > best.Sc) best = (sc, a, dx, dy);
                    }
                    if (span == 0) break;
                }
                Rotate(best.Ang);
                set = OverlapSet(best.Dx, best.Dy, 16000);
                (double Sc, double Ang, double Dx, double Dy) fin = (double.MinValue, best.Ang, best.Dx, best.Dy);
                for (double a = best.Ang - 0.1; a <= best.Ang + 0.1 + 1e-9; a += 0.02)
                {
                    if (angleRange > 0 ? Math.Abs(a) > angleRange + 1e-9 : Math.Abs(a) > 1e-9) continue;
                    for (double oy = -0.75; oy <= 0.75 + 1e-9; oy += 0.25)
                    for (double ox = -0.75; ox <= 0.75 + 1e-9; ox += 0.25)
                    {
                        Rotate(a, best.Dx + ox, best.Dy + oy);
                        double sc = Score(set, 0, 0, lut3);
                        if (sc > fin.Sc) fin = (sc, a, best.Dx + ox, best.Dy + oy);
                    }
                }
                refined.Add((fin.Ang, fin.Dx, fin.Dy));
            }
        }
        if (refined.Count == 0) return null;
        ct.ThrowIfCancellationRequested();
        progress?.Report(new AlignProgress(3, 95));

        // 5) 후보 최종 비교: 그 위치에서 겹친 점당 평균 (양방향 경계 일치 − Free 충돌)
        float[] k15 = Kernel(1.5);
        (double Quality, double Match, int Overlap) Evaluate(MapPose pose)
        {
            MapPose delta = Delta(start, pose);   // 시작 자세로 놓인 점 → pose 자세
            double dr = delta.AngleDeg * Math.PI / 180, dc = Math.Cos(dr), ds = Math.Sin(dr);
            double sum = 0;
            int cnt = 0, ov = 0, hit = 0;
            foreach (PointD p in e)
            {
                int x = (int)Math.Floor(dc * p.X - ds * p.Y + delta.Tx) - rect.X, y = (int)Math.Floor(ds * p.X + dc * p.Y + delta.Ty) - rect.Y;
                if ((uint)x >= (uint)rw || (uint)y >= (uint)rh) continue;
                short c = cost[y * rw + x];
                if (c < 0) continue;
                ov++;
                cnt++;
                if (c <= MatchDist * 3) hit++;
                sum += k15[c];
            }
            foreach (PointD p in f)
            {
                int x = (int)Math.Floor(dc * p.X - ds * p.Y + delta.Tx) - rect.X, y = (int)Math.Floor(ds * p.X + dc * p.Y + delta.Ty) - rect.Y;
                if ((uint)x < (uint)rw && (uint)y < (uint)rh && bObs[y * rw + x]) sum -= 1;   // 새 맵이 비어 있다고 본 곳에 현재 맵 벽
            }
            foreach (PointD p in bEdge)
            {
                PointD m = pose.ToMoving(p);
                int x = (int)Math.Floor(m.X), y = (int)Math.Floor(m.Y);
                if ((uint)x >= (uint)mw || (uint)y >= (uint)mh || md[y * mw + x] == MapValues.Unknown) continue;
                cnt++;
                double d = mdf.DistanceAt(x, y);
                if (d < 10000) sum += k15[(int)Math.Min(30000, Math.Round(d * 3))];
            }
            return (cnt == 0 ? -1 : sum / cnt, ov == 0 ? 0 : (double)hit / ov, ov);
        }

        PointD center = start.ToMoving(pivot);
        var poses = new List<(MapPose Pose, double Quality, double Match, int Overlap, double Move)>();
        foreach ((double ang, double dx, double dy) in refined)
        {
            MapPose pose = start.RotateAround(pivot, ang).Translate(dx, dy);
            if (poses.Exists(p => Distance(p.Pose, pose, center, maxR) < 3)) continue;
            var ev = Evaluate(pose);
            double move = Distance(start, pose, center, maxR);
            poses.Add((pose, ev.Quality, ev.Match, ev.Overlap, move));
        }
        // 조금만 걸쳐서 우연히 잘 맞는 후보 제외
        int maxOv = 0;
        foreach (var p in poses) maxOv = Math.Max(maxOv, p.Overlap);
        poses.RemoveAll(p => p.Overlap < maxOv * 0.3);
        poses.Sort((p, q) => q.Quality.CompareTo(p.Quality));
        // 점수가 거의 같으면(반복 구조 등) 대략 맞춘 위치에 가까운 쪽
        double top = poses[0].Quality;
        int pick = 0;
        for (int k = 1; k < poses.Count; k++)
            if (poses[k].Quality >= top - 0.03 && poses[k].Move < poses[pick].Move) pick = k;
        var chosen = poses[pick];
        poses.RemoveAt(pick);

        var alternatives = new List<MapPose>();
        foreach (var p in poses)
            if (p.Quality >= top - 0.1 && p.Quality >= top * 0.7) alternatives.Add(p.Pose);
        var before = Evaluate(start);
        return new AlignResult
        {
            Start = start, Pose = chosen.Pose, Points = n, OverlapPoints = chosen.Overlap,
            MatchBefore = before.Match, MatchAfter = chosen.Match, Alternatives = alternatives,
        };
    }

    private static List<PointD> Thin(List<PointD> list, int max)
    {
        if (list.Count <= max) return list;
        int stride = (list.Count + max - 1) / max;
        var sub = new List<PointD>((list.Count + stride - 1) / stride);
        for (int k = 0; k < list.Count; k += stride) sub.Add(list[k]);
        return sub;
    }

    /// <summary>0..n-1에서 최대 max개 균등 표본</summary>
    private static int[] Enumerable(int n, int max)
    {
        int stride = Math.Max(1, (n + max - 1) / max);
        var a = new int[(n + stride - 1) / stride];
        for (int k = 0, j = 0; k < n; k += stride, j++) a[j] = k;
        return a;
    }

    /// <summary>장애물 경계 픽셀 (4방향 이웃 중 장애물 아닌 것이 있음, 맵 가장자리 포함)</summary>
    private static bool IsEdge(byte[] d, int w, int h, int x, int y, byte thr) =>
        x == 0 || y == 0 || x == w - 1 || y == h - 1 ||
        d[y * w + x - 1] < thr || d[y * w + x + 1] < thr || d[(y - 1) * w + x] < thr || d[(y + 1) * w + x] < thr;

    /// <summary>a로 놓인 점을 b로 옮기는 변환 (현재 맵 좌표 → 현재 맵 좌표): b ∘ a⁻¹</summary>
    private static MapPose Delta(MapPose a, MapPose b)
    {
        // q_b = R_b·p + T_b, p = R_a⁻¹(q_a − T_a) → q_b = R_(b−a)·q_a + (T_b − R_(b−a)·T_a)
        var rot = new MapPose(b.AngleDeg - a.AngleDeg, 0, 0);
        PointD ta = rot.ToBase(new PointD(a.Tx, a.Ty));
        return new MapPose(b.AngleDeg - a.AngleDeg, b.Tx - ta.X, b.Ty - ta.Y);
    }

    /// <summary>두 자세 차이 (px): 맞출 맵 장애물 무게중심(맞출 맵 좌표 center)의 위치 차 + 회전에 따른 반경 maxR 끝 이동</summary>
    private static double Distance(MapPose a, MapPose b, PointD center, double maxR)
    {
        PointD p = a.ToBase(center), q = b.ToBase(center);
        double ang = Math.Abs(MapPose.NormalizeDeg(a.AngleDeg - b.AngleDeg)) * Math.PI / 180;
        return Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y)) + ang * maxR;
    }

    /// <summary>
    /// 맞출 맵을 현재 맵과 비교 (맞출 맵 픽셀 기준). 1px 어긋남은 같은 벽으로 봄.
    /// 추가 = 새 맵에만 장애물, 삭제 = 현재 맵에만 장애물, 새 영역 = 현재 맵이 모르는 곳
    /// </summary>
    public static AlignDiff Compare(MapImage baseMap, MapImage moving, MapPose pose, byte thr)
    {
        int mw = moving.Width, mh = moving.Height, bw = baseMap.Width, bh = baseMap.Height;
        byte[] md = moving.Data, bd = baseMap.Data;
        var kind = new byte[md.Length];
        var diff = new AlignDiff(kind);
        double r = pose.AngleDeg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);

        // 맞출 맵 픽셀 → 현재 맵 픽셀 위치 (범위 밖 -1)
        var map = new int[md.Length];
        for (int y = 0; y < mh; y++)
        for (int x = 0; x < mw; x++)
        {
            double px = x + 0.5, py = y + 0.5;
            int qx = (int)Math.Floor(c * px - s * py + pose.Tx), qy = (int)Math.Floor(s * px + c * py + pose.Ty);
            map[y * mw + x] = (uint)qx < (uint)bw && (uint)qy < (uint)bh ? qy * bw + qx : -1;
        }

        bool BaseObstacleNear(int bi)
        {
            int x = bi % bw, y = bi / bw;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((uint)nx < (uint)bw && (uint)ny < (uint)bh && bd[ny * bw + nx] >= thr) return true;
            }
            return false;
        }
        bool MovingObstacleNear(int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((uint)nx < (uint)mw && (uint)ny < (uint)mh && md[ny * mw + nx] >= thr) return true;
            }
            return false;
        }

        for (int y = 0; y < mh; y++)
        for (int x = 0; x < mw; x++)
        {
            int i = y * mw + x;
            byte mv = md[i];
            if (mv == MapValues.Unknown) continue;
            int bi = map[i];
            if (bi < 0 || bd[bi] == MapValues.Unknown)
            {
                kind[i] = AlignDiff.NewArea;
                diff.NewAreaCount++;
                continue;
            }
            bool mo = mv >= thr, bo = bd[bi] >= thr;
            if (mo && bo) { kind[i] = AlignDiff.Same; diff.SameCount++; }
            else if (mo && !BaseObstacleNear(bi)) { kind[i] = AlignDiff.Added; diff.AddedCount++; }
            else if (!mo && bo && !MovingObstacleNear(x, y)) { kind[i] = AlignDiff.Removed; diff.RemovedCount++; }
            else if (mo) { kind[i] = AlignDiff.Same; diff.SameCount++; }
        }
        return diff;
    }

    /// <summary>비교 결과의 추가 · 삭제 덩어리를 현재 맵 좌표 범위로 (면적 큰 순, minArea 이상)</summary>
    public static List<(IntRect Bounds, int Area, bool Added)> DiffRegions(AlignDiff diff, MapImage moving, MapPose pose, int minArea)
    {
        var result = new List<(IntRect, int, bool)>();
        foreach (bool added in new[] { true, false })
        {
            var mask = new MapImage(moving.Width, moving.Height);
            byte want = added ? AlignDiff.Added : AlignDiff.Removed;
            for (int i = 0; i < mask.Data.Length; i++)
                if (diff.Kind[i] == want) mask.Data[i] = MapValues.Obstacle;
            foreach (Blob b in BlobDetector.Detect(mask, MapValues.Obstacle, int.MaxValue, int.MaxValue, null, collectPixels: false))
            {
                if (b.Area < minArea) continue;
                // 범위 네 모서리를 현재 맵 좌표로
                IntRect r = b.Bounds;
                double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                foreach (PointD p in new[] { new PointD(r.X, r.Y), new PointD(r.Right, r.Y), new PointD(r.Right, r.Bottom), new PointD(r.X, r.Bottom) })
                {
                    PointD q = pose.ToBase(p);
                    x0 = Math.Min(x0, q.X); y0 = Math.Min(y0, q.Y);
                    x1 = Math.Max(x1, q.X); y1 = Math.Max(y1, q.Y);
                }
                int ix0 = (int)Math.Floor(x0), iy0 = (int)Math.Floor(y0);
                result.Add((new IntRect(ix0, iy0, (int)Math.Ceiling(x1) - ix0, (int)Math.Ceiling(y1) - iy0), b.Area, added));
            }
        }
        result.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return result;
    }

    /// <summary>
    /// 두 맵을 합친 새 맵. 결과 크기 = 현재 맵 + 맞출 맵의 알려진 부분(볼록 껍질)을 모두 담는 범위 (맞출 맵 둘레 Unknown 여백은 제외),
    /// 현재 맵 좌표는 (OffsetX, OffsetY)만큼 밀림.
    /// 맞출 맵은 회전 · 이동 후 Nearest로 다시 찍고, 맞출 맵의 Unknown은 절대 덮어쓰지 않음.
    /// region은 RegionMovingFirst에서 새 맵이 우선할 범위 (현재 맵 좌표)
    /// </summary>
    /// <summary>
    /// 두 맵 합치기. trimUnknown = false면 두 맵 이미지 전체를 담는 크기, true면 맞출 맵은 알려진 부분만 담음
    /// </summary>
    public static MergeResult Merge(MapImage baseMap, MapImage moving, MapPose pose, MergeRule rule, byte thr, PixelRegion? region,
                                    bool trimUnknown = false)
    {
        IntRect mb = MergedBounds(baseMap, trimUnknown ? KnownHull(moving) : FullRect(moving), pose);
        int w = mb.Width, h = mb.Height, ox = -mb.X, oy = -mb.Y;

        var img = new MapImage(w, h) { MaxVal = baseMap.MaxVal, Format = baseMap.Format };
        img.Comments.AddRange(baseMap.Comments);
        for (int y = 0; y < baseMap.Height; y++)
            Array.Copy(baseMap.Data, y * baseMap.Width, img.Data, (y + oy) * w + ox, baseMap.Width);

        int from = Paint(img, ox, oy, moving, pose, (gx, gy, cur, mv) =>
        {
            bool inBase = (uint)gx < (uint)baseMap.Width && (uint)gy < (uint)baseMap.Height;
            return rule switch
            {
                MergeRule.MovingFirst => true,
                MergeRule.ObstacleUnion => cur < thr && (mv >= thr || cur == MapValues.Unknown),
                MergeRule.RegionMovingFirst => (inBase && region != null && region.Contains(gx, gy)) || cur == MapValues.Unknown,
                _ => cur == MapValues.Unknown,
            };
        }, null);
        return new MergeResult(img, ox, oy, from);
    }

    /// <summary>
    /// 현재 맵 크기 그대로 맞출 맵 내용을 덮어씀 (새 맵이 알고 있는 곳만, region이 있으면 그 안만).
    /// EditTracker를 거치므로 실행 취소 · 보호 영역이 적용됨. 바뀐 픽셀 수 반환
    /// </summary>
    public static int ApplyInto(EditTracker t, MapImage moving, MapPose pose, PixelRegion? region)
    {
        MapImage map = t.Map;
        int n = 0;
        Paint(map, 0, 0, moving, pose, (gx, gy, cur, mv) => region == null || region.Contains(gx, gy), (x, y, v) =>
        {
            if (t.Set(x, y, v)) n++;
        });
        return n;
    }

    /// <summary>
    /// 맞출 맵을 dst(현재 맵 좌표가 (ox, oy)만큼 밀린 캔버스)에 찍음. take(현재 맵 좌표 x, y, 기존 값, 새 값)가 true인 곳만.
    /// set이 null이면 dst에 직접 기록. 기록한 픽셀 수 반환
    /// </summary>
    private static int Paint(MapImage dst, int ox, int oy, MapImage moving, MapPose pose,
                             Func<int, int, byte, byte, bool> take, Action<int, int, byte>? set)
    {
        IntRect fp = FootprintBounds(moving, pose);
        IntRect area = new IntRect(fp.X + ox, fp.Y + oy, fp.Width, fp.Height).Intersect(dst.Bounds);
        double r = pose.AngleDeg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        int mw = moving.Width, mh = moving.Height, dw = dst.Width, n = 0;
        for (int y = area.Y; y < area.Bottom; y++)
        {
            double qy = y - oy + 0.5 - pose.Ty;
            for (int x = area.X; x < area.Right; x++)
            {
                double qx = x - ox + 0.5 - pose.Tx;
                int mx = (int)Math.Floor(c * qx + s * qy), my = (int)Math.Floor(-s * qx + c * qy);
                if ((uint)mx >= (uint)mw || (uint)my >= (uint)mh) continue;
                byte mv = moving.Data[my * mw + mx];
                if (mv == MapValues.Unknown) continue;
                int i = y * dw + x;
                byte cur = dst.Data[i];
                if (cur == mv || !take(x - ox, y - oy, cur, mv)) continue;
                if (set != null) set(x, y, mv);
                else
                {
                    dst.Data[i] = mv;
                    n++;
                }
            }
        }
        return n;
    }

    /// <summary>
    /// 합친 맵의 yaml origin: 현재 맵의 월드 좌표가 그대로 유지되도록 (현재 맵 좌하단이 결과에서 (ox, oy + baseHeight)에 놓임)
    /// </summary>
    public static (double OriginX, double OriginY) MergedOrigin(MapMeta baseMeta, int baseHeight, MergeResult r)
    {
        double res = baseMeta.Resolution;
        int bottomGap = r.Image.Height - (r.OffsetY + baseHeight);   // 현재 맵 아래쪽으로 늘어난 줄 수
        return (baseMeta.OriginX - r.OffsetX * res, baseMeta.OriginY - bottomGap * res);
    }

    /// <summary>
    /// 표시용 색 (Pbgra32). 겹쳐보기: 장애물 자홍 · Free 옅은 청록.
    /// 비교: 같음 회색 · 추가 빨강 · 삭제 파랑 · 새 영역 장애물 자홍 / Free 옅은 청록
    /// </summary>
    public static void FillLayer(MapImage moving, byte thr, AlignDiff? diff, uint[] dest)
    {
        uint obstacle = Premul(235, 0xD9, 0x46, 0xEF), free = Premul(45, 0x06, 0xB6, 0xD4), low = Premul(30, 0x06, 0xB6, 0xD4);
        uint same = Premul(150, 0x6B, 0x72, 0x80), added = Premul(235, 255, 40, 40), removed = Premul(235, 30, 110, 255);
        byte[] md = moving.Data;
        for (int i = 0; i < md.Length; i++)
        {
            byte v = md[i];
            if (v == MapValues.Unknown) { dest[i] = 0; continue; }
            if (diff == null)
            {
                dest[i] = v >= thr ? obstacle : v == MapValues.Free ? free : low;
                continue;
            }
            dest[i] = diff.Kind[i] switch
            {
                AlignDiff.Same => same,
                AlignDiff.Added => added,
                AlignDiff.Removed => removed,
                AlignDiff.NewArea => v >= thr ? obstacle : free,
                _ => 0u,
            };
        }
    }

    private static uint Premul(int a, int r, int g, int b) =>
        ((uint)a << 24) | ((uint)(r * a / 255) << 16) | ((uint)(g * a / 255) << 8) | (uint)(b * a / 255);
}
