using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AmrMapEditor.Core;

/// <summary>맵 yaml(resolution, origin) 정보. 없으면 해상도만 수동 입력</summary>
public sealed class MapMeta
{
    public double Resolution { get; set; } = 0.05;
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    public double OriginTheta { get; set; }
    public string? ImageName { get; set; }
    public string? SourcePath { get; set; }

    /// <summary>픽셀 중심의 월드 좌표 (ROS 규칙: origin = 좌하단 픽셀, y축 위쪽)</summary>
    public (double X, double Y) PixelToWorld(int px, int py, int imageHeight) =>
        (OriginX + (px + 0.5) * Resolution, OriginY + (imageHeight - py - 0.5) * Resolution);

    /// <summary>같은 이름의 yaml, 또는 image 항목이 이 파일을 가리키는 yaml 탐색</summary>
    public static MapMeta? TryLoadForImage(string imagePath)
    {
        try
        {
            string full = Path.GetFullPath(imagePath);
            string dir = Path.GetDirectoryName(full) ?? ".";
            string stem = Path.GetFileNameWithoutExtension(full);

            foreach (string ext in new[] { ".yaml", ".yml" })
            {
                string p = Path.Combine(dir, stem + ext);
                if (File.Exists(p)) return LoadFile(p);
            }

            string fileName = Path.GetFileName(full);
            var candidates = Directory.EnumerateFiles(dir, "*.yaml").Concat(Directory.EnumerateFiles(dir, "*.yml"));
            foreach (string p in candidates)
            {
                MapMeta m = LoadFile(p);
                if (m.ImageName != null &&
                    string.Equals(Path.GetFileName(m.ImageName), fileName, StringComparison.OrdinalIgnoreCase))
                    return m;
            }
        }
        catch (Exception)
        {
            // yaml은 선택 사항이므로 오류 무시
        }
        return null;
    }

    public static MapMeta LoadFile(string path)
    {
        MapMeta m = Parse(File.ReadAllText(path));
        m.SourcePath = path;
        return m;
    }

    public static MapMeta Parse(string text)
    {
        var m = new MapMeta();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;

            string key = line[..colon].Trim();
            string val = line[(colon + 1)..].Trim().Trim('"', '\'');

            switch (key)
            {
                case "resolution":
                    if (TryDouble(val, out double r) && r > 0) m.Resolution = r;
                    break;
                case "origin":
                    string[] parts = val.Trim('[', ']', ' ').Split(',');
                    if (parts.Length >= 2 && TryDouble(parts[0], out double ox) && TryDouble(parts[1], out double oy))
                    {
                        m.OriginX = ox;
                        m.OriginY = oy;
                        if (parts.Length >= 3 && TryDouble(parts[2], out double th)) m.OriginTheta = th;
                    }
                    break;
                case "image":
                    m.ImageName = val;
                    break;
            }
        }
        return m;
    }

    private static bool TryDouble(string s, out double v) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>yaml 텍스트의 origin(필요 시 image) 줄만 교체. 다른 줄과 들여쓰기는 유지</summary>
    public static string RewriteYaml(string text, string? imageName, double originX, double originY, double theta)
    {
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        string origin = string.Format(CultureInfo.InvariantCulture, "origin: [{0:0.######}, {1:0.######}, {2:0.######}]",
            originX, originY, theta);
        bool hasOrigin = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            string indent = lines[i][..(lines[i].Length - trimmed.Length)];
            if (trimmed.StartsWith("origin:", StringComparison.Ordinal))
            {
                lines[i] = indent + origin;
                hasOrigin = true;
            }
            else if (imageName != null && trimmed.StartsWith("image:", StringComparison.Ordinal))
            {
                lines[i] = indent + "image: " + imageName;
            }
        }

        string result = string.Join(nl, lines);
        if (!hasOrigin) result = result.TrimEnd('\r', '\n') + nl + origin + nl;
        return result;
    }

    /// <summary>yaml이 없을 때 새로 만드는 기본 내용</summary>
    public string ToYaml(string imageName) => string.Format(CultureInfo.InvariantCulture,
        "image: {0}\nresolution: {1:0.######}\norigin: [{2:0.######}, {3:0.######}, {4:0.######}]\nnegate: 0\noccupied_thresh: 0.65\nfree_thresh: 0.196\n",
        imageName, Resolution, OriginX, OriginY, OriginTheta);
}
