using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AmrMapEditor.Core;

/// <summary>도면 선분 (선/폴리라인/원/호를 모두 꺾은선으로 변환)</summary>
public sealed class DxfPolyline
{
    public DxfPolyline(string layer, List<PointD> points, bool closed)
    {
        Layer = layer;
        Points = points;
        Closed = closed;
    }

    public string Layer { get; }
    public List<PointD> Points { get; }
    public bool Closed { get; }
}

public sealed class DxfDrawing
{
    public List<DxfPolyline> Polylines { get; } = new();
    public int InsUnits { get; set; }

    /// <summary>도면 단위 → m ($INSUNITS 기준, 미지정이면 mm로 가정)</summary>
    public double UnitToMeter => InsUnits switch
    {
        1 => 0.0254,
        2 => 0.3048,
        5 => 0.01,
        6 => 1.0,
        _ => 0.001,
    };

    public string UnitName => InsUnits switch
    {
        1 => "inch",
        2 => "feet",
        4 => "mm",
        5 => "cm",
        6 => "m",
        _ => "미지정(mm로 가정)",
    };

    public IEnumerable<string> Layers => Polylines.Select(p => p.Layer).Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

    public (double MinX, double MinY, double MaxX, double MaxY)? Extents(Func<string, bool> visible)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (DxfPolyline pl in Polylines)
        {
            if (!visible(pl.Layer)) continue;
            foreach (PointD p in pl.Points)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
        }
        return maxX < minX ? null : (minX, minY, maxX, maxY);
    }
}

/// <summary>ASCII DXF 읽기: LINE, LWPOLYLINE(bulge 포함), POLYLINE, CIRCLE, ARC, INSERT(블록 전개)</summary>
public static class DxfReader
{
    private const int MaxInsertDepth = 8;

    private sealed class Raw
    {
        public Raw(string type) => Type = type;
        public string Type { get; }
        public List<(int Code, string Value)> Pairs { get; } = new();

        public string Str(int code, string def = "") =>
            Pairs.FirstOrDefault(p => p.Code == code).Value ?? def;

        public double Num(int code, double def = 0)
        {
            foreach (var (c, v) in Pairs)
                if (c == code && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            return def;
        }
    }

    private sealed class Block
    {
        public string Name = "";
        public double BaseX, BaseY;
        public List<Raw> Entities = new();
    }

    /// <summary>2D 아핀 변환 x' = a x + b y + e, y' = c x + d y + f</summary>
    private readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

        public PointD Apply(double x, double y) => new(A * x + B * y + E, C * x + D * y + F);

        public Affine Then(Affine o) => new(
            o.A * A + o.B * C, o.A * B + o.B * D,
            o.C * A + o.D * C, o.C * B + o.D * D,
            o.A * E + o.B * F + o.E, o.C * E + o.D * F + o.F);
    }

    public static DxfDrawing Read(string path) => Parse(DecodeText(File.ReadAllBytes(path)));

    /// <summary>UTF-8 우선, 실패 시 CP949, 그다음 Latin1</summary>
    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length > 22 && Encoding.ASCII.GetString(bytes, 0, 18) == "AutoCAD Binary DXF")
            throw new InvalidDataException("바이너리 DXF는 지원하지 않습니다. ASCII DXF로 저장해 주세요.");
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
        }
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949).GetString(bytes);
        }
        catch (Exception)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    public static DxfDrawing Parse(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        var pairs = new List<(int, string)>(lines.Length / 2);
        for (int i = 0; i + 1 < lines.Length; i += 2)
        {
            if (!int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                throw new InvalidDataException($"DXF 형식 오류 ({i + 1}번째 줄)");
            pairs.Add((code, lines[i + 1].Trim()));
        }

        var drawing = new DxfDrawing();
        var blocks = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
        var entities = new List<Raw>();

        string section = "";
        Block? curBlock = null;
        Raw? cur = null;

        for (int i = 0; i < pairs.Count; i++)
        {
            (int code, string value) = pairs[i];
            if (code == 0)
            {
                cur = null;
                switch (value)
                {
                    case "SECTION":
                        if (i + 1 < pairs.Count && pairs[i + 1].Item1 == 2) section = pairs[i + 1].Item2;
                        continue;
                    case "ENDSEC":
                        section = "";
                        continue;
                    case "EOF":
                        i = pairs.Count;
                        continue;
                }

                if (section == "BLOCKS")
                {
                    if (value == "BLOCK")
                    {
                        curBlock = new Block();
                        cur = new Raw("BLOCK");
                        continue;
                    }
                    if (value == "ENDBLK")
                    {
                        if (curBlock != null && curBlock.Name.Length > 0) blocks[curBlock.Name] = curBlock;
                        curBlock = null;
                        continue;
                    }
                    if (curBlock != null)
                    {
                        cur = new Raw(value);
                        curBlock.Entities.Add(cur);
                    }
                }
                else if (section == "ENTITIES")
                {
                    cur = new Raw(value);
                    entities.Add(cur);
                }
                continue;
            }

            if (section == "HEADER" && code == 9 && value == "$INSUNITS" && i + 1 < pairs.Count)
            {
                if (int.TryParse(pairs[i + 1].Item2, out int u)) drawing.InsUnits = u;
                continue;
            }

            if (cur == null) continue;
            if (cur.Type == "BLOCK" && curBlock != null)
            {
                if (code == 2) curBlock.Name = value;
                else if (code == 10) curBlock.BaseX = ParseNum(value);
                else if (code == 20) curBlock.BaseY = ParseNum(value);
                continue;
            }
            cur.Pairs.Add((code, value));
        }

        Emit(entities, Affine.Identity, "0", 0, blocks, drawing.Polylines);
        return drawing;
    }

    private static double ParseNum(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;

    private static void Emit(List<Raw> list, Affine tf, string parentLayer, int depth,
                             Dictionary<string, Block> blocks, List<DxfPolyline> output)
    {
        for (int i = 0; i < list.Count; i++)
        {
            Raw e = list[i];
            string layer = e.Str(8, "0");
            if (layer == "0" && depth > 0) layer = parentLayer;

            switch (e.Type)
            {
                case "LINE":
                    Add(output, layer, tf, new List<PointD> { new(e.Num(10), e.Num(20)), new(e.Num(11), e.Num(21)) }, false);
                    break;

                case "LWPOLYLINE":
                    Add(output, layer, tf, LwPolylinePoints(e, out bool lwClosed), lwClosed);
                    break;

                case "POLYLINE":
                {
                    bool closed = ((int)e.Num(70) & 1) != 0;
                    var pts = new List<PointD>();
                    while (i + 1 < list.Count && list[i + 1].Type == "VERTEX")
                    {
                        i++;
                        pts.Add(new PointD(list[i].Num(10), list[i].Num(20)));
                    }
                    if (i + 1 < list.Count && list[i + 1].Type == "SEQEND") i++;
                    Add(output, layer, tf, pts, closed);
                    break;
                }

                case "CIRCLE":
                    Add(output, layer, tf, ArcPoints(e.Num(10), e.Num(20), e.Num(40), 0, 360), true);
                    break;

                case "ARC":
                {
                    double a0 = e.Num(50), a1 = e.Num(51);
                    if (a1 <= a0) a1 += 360;
                    Add(output, layer, tf, ArcPoints(e.Num(10), e.Num(20), e.Num(40), a0, a1), false);
                    break;
                }

                case "INSERT":
                {
                    if (depth >= MaxInsertDepth || !blocks.TryGetValue(e.Str(2), out Block? blk)) break;
                    double sx = e.Num(41, 1), sy = e.Num(42, 1), rot = e.Num(50) * Math.PI / 180;
                    double cs = Math.Cos(rot), sn = Math.Sin(rot);
                    // 블록 기준점 이동 → 축척 → 회전 → 삽입점 이동
                    var local = new Affine(cs * sx, -sn * sy, sn * sx, cs * sy,
                        e.Num(10) - (cs * sx * blk.BaseX - sn * sy * blk.BaseY),
                        e.Num(20) - (sn * sx * blk.BaseX + cs * sy * blk.BaseY));
                    Emit(blk.Entities, local.Then(tf), layer, depth + 1, blocks, output);
                    break;
                }
            }
        }
    }

    private static void Add(List<DxfPolyline> output, string layer, Affine tf, List<PointD> pts, bool closed)
    {
        if (pts.Count < 2) return;
        output.Add(new DxfPolyline(layer, pts.Select(p => tf.Apply(p.X, p.Y)).ToList(), closed));
    }

    private static List<PointD> LwPolylinePoints(Raw e, out bool closed)
    {
        closed = ((int)e.Num(70) & 1) != 0;
        var verts = new List<(double X, double Y, double Bulge)>();
        double x = 0;
        bool hasX = false;
        foreach (var (code, value) in e.Pairs)
        {
            switch (code)
            {
                case 10:
                    x = ParseNum(value);
                    hasX = true;
                    break;
                case 20 when hasX:
                    verts.Add((x, ParseNum(value), 0));
                    hasX = false;
                    break;
                case 42 when verts.Count > 0:
                    verts[^1] = (verts[^1].X, verts[^1].Y, ParseNum(value));
                    break;
            }
        }

        var pts = new List<PointD>();
        int segs = closed ? verts.Count : verts.Count - 1;
        if (verts.Count > 0) pts.Add(new PointD(verts[0].X, verts[0].Y));
        for (int k = 0; k < segs; k++)
        {
            var a = verts[k];
            var b = verts[(k + 1) % verts.Count];
            if (Math.Abs(a.Bulge) > 1e-9) AddBulge(pts, a.X, a.Y, b.X, b.Y, a.Bulge);
            if (!(closed && k == segs - 1)) pts.Add(new PointD(b.X, b.Y));
        }
        return pts;
    }

    /// <summary>bulge 호의 중간 점 추가 (양수 = 반시계)</summary>
    private static void AddBulge(List<PointD> pts, double x1, double y1, double x2, double y2, double bulge)
    {
        double cx = x2 - x1, cy = y2 - y1, len = Math.Sqrt(cx * cx + cy * cy);
        if (len < 1e-12) return;
        double theta = 4 * Math.Atan(bulge);
        double d = len / 2 / Math.Tan(theta / 2);
        double mx = (x1 + x2) / 2 - cy / len * d, my = (y1 + y2) / 2 + cx / len * d;
        double r = Math.Sqrt((x1 - mx) * (x1 - mx) + (y1 - my) * (y1 - my));
        double a0 = Math.Atan2(y1 - my, x1 - mx);
        int n = Math.Max(4, (int)Math.Ceiling(Math.Abs(theta) / (Math.PI / 18)));
        for (int k = 1; k < n; k++)
        {
            double a = a0 + theta * k / n;
            pts.Add(new PointD(mx + r * Math.Cos(a), my + r * Math.Sin(a)));
        }
    }

    private static List<PointD> ArcPoints(double cx, double cy, double r, double a0Deg, double a1Deg)
    {
        double sweep = a1Deg - a0Deg;
        int n = Math.Max(8, (int)Math.Ceiling(sweep / 10));
        var pts = new List<PointD>(n + 1);
        for (int k = 0; k <= n; k++)
        {
            double a = (a0Deg + sweep * k / n) * Math.PI / 180;
            pts.Add(new PointD(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }
        if (Math.Abs(sweep - 360) < 1e-9) pts.RemoveAt(pts.Count - 1);
        return pts;
    }
}

/// <summary>도면 좌표(y 위쪽) → 맵 픽셀 좌표 변환 (축척·회전·이동)</summary>
public sealed class DxfPlacement
{
    /// <summary>도면 1단위당 픽셀 수</summary>
    public double Scale { get; set; } = 1;

    /// <summary>이미지 좌표 기준 회전 (도)</summary>
    public double RotationDeg { get; set; }

    public double OffsetX { get; set; }
    public double OffsetY { get; set; }

    public PointD ToPixel(PointD p)
    {
        double x = p.X * Scale, y = -p.Y * Scale;
        double r = RotationDeg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        return new PointD(x * c - y * s + OffsetX, x * s + y * c + OffsetY);
    }

    /// <summary>도면 범위 중심을 target 중심에 맞춘 초기 배치</summary>
    public static DxfPlacement CenterOn(DxfDrawing d, Func<string, bool> visible, double scale, IntRect target)
    {
        var p = new DxfPlacement { Scale = scale };
        var ext = d.Extents(visible);
        if (ext is { } e)
        {
            double cx = (e.MinX + e.MaxX) / 2 * scale, cy = -(e.MinY + e.MaxY) / 2 * scale;
            p.OffsetX = target.X + target.Width / 2.0 - cx;
            p.OffsetY = target.Y + target.Height / 2.0 - cy;
        }
        return p;
    }

    /// <summary>
    /// 2점 정렬: 도면 점 a1, a2가 맵 픽셀 b1, b2에 오도록 배치.
    /// fixedScale이 있으면 축척은 유지하고 회전·이동만 맞춤 (두 점의 중점 기준)
    /// </summary>
    public static DxfPlacement? FromTwoPoints(PointD a1, PointD a2, PointD b1, PointD b2, double? fixedScale)
    {
        double ax = a2.X - a1.X, ay = -(a2.Y - a1.Y);
        double bx = b2.X - b1.X, by = b2.Y - b1.Y;
        double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
        if (la < 1e-9 || lb < 1e-9) return null;

        double scale = fixedScale ?? lb / la;
        double rot = Math.Atan2(by, bx) - Math.Atan2(ay, ax);
        var p = new DxfPlacement { Scale = scale, RotationDeg = rot * 180 / Math.PI };

        // 기준점: 축척 고정이면 중점, 아니면 첫 점
        PointD am = fixedScale.HasValue ? new PointD((a1.X + a2.X) / 2, (a1.Y + a2.Y) / 2) : a1;
        PointD bm = fixedScale.HasValue ? new PointD((b1.X + b2.X) / 2, (b1.Y + b2.Y) / 2) : b1;
        PointD mapped = p.ToPixel(am);
        p.OffsetX = bm.X - mapped.X;
        p.OffsetY = bm.Y - mapped.Y;
        return p;
    }
}
