using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AmrMapEditor.Core;

/// <summary>외부 라이브러리 없이 RGB 8bit PNG 저장</summary>
public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void WriteRgb(Stream output, int width, int height, byte[] rgb)
    {
        if (rgb.Length != width * height * 3) throw new ArgumentException("RGB 데이터 길이가 맞지 않습니다.", nameof(rgb));

        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)width);
        WriteBE(ihdr, 4, (uint)height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 2;   // RGB
        WriteChunk(output, "IHDR", ihdr);

        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            {
                int stride = width * 3;
                for (int y = 0; y < height; y++)
                {
                    z.WriteByte(0);   // filter: none
                    z.Write(rgb, y * stride, stride);
                }
            }
            WriteChunk(output, "IDAT", raw.ToArray());
        }
        WriteChunk(output, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, (uint)data.Length);
        s.Write(len);
        byte[] t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFFu, t), data) ^ 0xFFFFFFFFu;
        var c = new byte[4];
        WriteBE(c, 0, crc);
        s.Write(c);
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static void WriteBE(byte[] buf, int offset, uint v)
    {
        buf[offset] = (byte)(v >> 24);
        buf[offset + 1] = (byte)(v >> 16);
        buf[offset + 2] = (byte)(v >> 8);
        buf[offset + 3] = (byte)v;
    }
}

/// <summary>저장 시 변경 이력(diff 이미지 + 요약) 기록</summary>
public static class ChangeHistory
{
    /// <summary>
    /// &lt;맵 폴더&gt;/_history 에 diff PNG, 요약 txt 저장 후 history.log에 한 줄 추가.
    /// 크기가 다르면(기울기 보정 등) 이미지 없이 요약만 기록. 생성한 요약 파일 경로 반환
    /// </summary>
    public static string Write(string mapPath, MapImage before, MapImage after, byte thr, string note)
    {
        string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(mapPath)) ?? ".", "_history");
        Directory.CreateDirectory(dir);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string stem = Path.GetFileNameWithoutExtension(mapPath) + "_" + stamp;

        var sb = new StringBuilder();
        sb.AppendLine($"파일: {Path.GetFileName(mapPath)}");
        sb.AppendLine($"저장 시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        string summary;

        if (before.Width == after.Width && before.Height == after.Height)
        {
            DiffStats st = MapDiff.Count(after, before, thr);
            (long changed, IntRect bounds) = MapStats.Compare(before, after);
            summary = $"변경 {changed:N0} px (추가 장애물 {st.Added:N0}, 사라진 장애물 {st.Removed:N0}, 기타 {st.Other:N0}), 범위 {bounds}";
            sb.AppendLine(summary);
            if (changed > 0)
            {
                using var fs = File.Create(Path.Combine(dir, stem + ".png"));
                PngWriter.WriteRgb(fs, after.Width, after.Height, RenderDiff(before, after, thr));
                sb.AppendLine($"diff 이미지: {stem}.png (빨강 추가, 파랑 삭제, 노랑 기타)");
            }
        }
        else
        {
            summary = $"크기 변경 {before.Width}×{before.Height} → {after.Width}×{after.Height}";
            sb.AppendLine(summary);
        }
        if (!string.IsNullOrWhiteSpace(note)) sb.AppendLine(note);

        string txt = Path.Combine(dir, stem + ".txt");
        File.WriteAllText(txt, sb.ToString(), Encoding.UTF8);
        File.AppendAllText(Path.Combine(dir, "history.log"),
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{Path.GetFileName(mapPath)}\t{summary}\n", Encoding.UTF8);
        return txt;
    }

    /// <summary>저장 후 맵을 표준 색으로 그리고 변경 픽셀을 색으로 표시한 RGB</summary>
    public static byte[] RenderDiff(MapImage before, MapImage after, byte thr)
    {
        uint[] lut = MapPalettes.Create(MapDisplayMode.Standard);
        var rgb = new byte[after.Data.Length * 3];
        for (int i = 0; i < after.Data.Length; i++)
        {
            byte a = after.Data[i], b = before.Data[i];
            uint c = lut[a];
            if (a != b)
            {
                bool ao = a >= thr, bo = b >= thr;
                c = ao && !bo ? 0xFFFF2020u : !ao && bo ? 0xFF1E6EFFu : 0xFFFFC800u;
            }
            rgb[i * 3] = (byte)(c >> 16);
            rgb[i * 3 + 1] = (byte)(c >> 8);
            rgb[i * 3 + 2] = (byte)c;
        }
        return rgb;
    }
}
