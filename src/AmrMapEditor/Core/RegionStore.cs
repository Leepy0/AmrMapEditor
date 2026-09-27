using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AmrMapEditor.Core;

/// <summary>영역 직렬화용 데이터</summary>
public sealed class RegionData
{
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>폴리곤 꼭짓점 [x, y] 목록 (사각형이면 null)</summary>
    public List<double[]>? Polygon { get; set; }
}

public sealed class NamedRegion
{
    public NamedRegion(string name, PixelRegion region)
    {
        Name = name;
        Region = region;
    }

    public string Name { get; }
    public PixelRegion Region { get; }
    public string Text => $"{Name}  {Region}";
}

/// <summary>보호 영역을 맵 옆 사이드카 파일(&lt;맵&gt;.protect.json)로 보관</summary>
public static class RegionStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string PathFor(string mapPath) => mapPath + ".protect.json";

    public static List<NamedRegion> Load(string mapPath, IntRect bounds)
    {
        var result = new List<NamedRegion>();
        string path = PathFor(mapPath);
        if (!File.Exists(path)) return result;

        var list = JsonSerializer.Deserialize<List<RegionData>>(File.ReadAllText(path), Options) ?? new List<RegionData>();
        foreach (RegionData d in list)
        {
            PixelRegion? r = d.Polygon is { Count: >= 3 }
                ? PixelRegion.FromPolygon(d.Polygon.Select(p => new PointD(p[0], p[1])).ToList(), bounds)
                : PixelRegion.FromRect(new IntRect(d.X, d.Y, d.Width, d.Height).Intersect(bounds));
            if (r != null && !r.Bounds.IsEmpty) result.Add(new NamedRegion(d.Name, r));
        }
        return result;
    }

    public static void Save(string mapPath, IEnumerable<NamedRegion> regions)
    {
        var list = regions.Select(n => new RegionData
        {
            Name = n.Name,
            X = n.Region.Bounds.X,
            Y = n.Region.Bounds.Y,
            Width = n.Region.Bounds.Width,
            Height = n.Region.Bounds.Height,
            Polygon = n.Region.Polygon?.Select(p => new[] { p.X, p.Y }).ToList(),
        }).ToList();

        string path = PathFor(mapPath);
        if (list.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        File.WriteAllText(path, JsonSerializer.Serialize(list, Options));
    }
}

/// <summary>입력값 저장 (%AppData%\AmrMapEditor\settings.json)</summary>
public sealed class AppSettings
{
    public Dictionary<string, string> Values { get; set; } = new();

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AmrMapEditor", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            // 설정 파일이 깨져도 기본값으로 시작
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // 설정 저장 실패는 무시
        }
    }
}

/// <summary>맵에 연결된 도면과 배치 정보 (&lt;맵&gt;.dxf.json)</summary>
public sealed class DxfLink
{
    public string DxfPath { get; set; } = "";
    public double Scale { get; set; } = 1;
    public double RotationDeg { get; set; }
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public List<string> HiddenLayers { get; set; } = new();

    public static string PathFor(string mapPath) => mapPath + ".dxf.json";

    public static DxfLink? Load(string mapPath)
    {
        try
        {
            string p = PathFor(mapPath);
            return File.Exists(p) ? JsonSerializer.Deserialize<DxfLink>(File.ReadAllText(p)) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(string mapPath) =>
        File.WriteAllText(PathFor(mapPath), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
