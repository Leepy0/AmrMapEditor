using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AmrMapEditor.Core;

/// <summary>PGM(P5 binary / P2 ASCII, 8bit) 입출력. 헤더 주석과 형식을 그대로 보존</summary>
public static class PgmIO
{
    public static MapImage Read(string path) => Read(File.ReadAllBytes(path));

    public static MapImage Read(byte[] bytes)
    {
        int pos = 0;
        var comments = new List<string>();

        string magic = NextToken(bytes, ref pos, comments) ?? throw new InvalidDataException("빈 파일입니다.");
        PgmFormat format = magic switch
        {
            "P5" => PgmFormat.P5,
            "P2" => PgmFormat.P2,
            _ => throw new InvalidDataException($"지원하지 않는 형식입니다: {magic} (P5/P2만 지원)"),
        };

        int width = ReadInt(bytes, ref pos, comments, "폭");
        int height = ReadInt(bytes, ref pos, comments, "높이");
        int maxVal = ReadInt(bytes, ref pos, comments, "maxval");

        if (width <= 0 || height <= 0) throw new InvalidDataException($"이미지 크기가 올바르지 않습니다 ({width}×{height}).");
        if (maxVal < 1 || maxVal > 255) throw new InvalidDataException($"8bit PGM만 지원합니다 (maxval={maxVal}).");
        if ((long)width * height > int.MaxValue) throw new InvalidDataException("이미지가 너무 큽니다.");

        var map = new MapImage(width, height) { MaxVal = maxVal, Format = format };
        map.Comments.AddRange(comments);
        int count = width * height;

        if (format == PgmFormat.P5)
        {
            // maxval 뒤 공백 1바이트 다음부터 바이너리 데이터
            pos++;
            if (bytes.Length - pos < count)
                throw new InvalidDataException($"픽셀 데이터가 부족합니다 ({Math.Max(0, bytes.Length - pos)}/{count} bytes).");
            Buffer.BlockCopy(bytes, pos, map.Data, 0, count);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                string t = NextToken(bytes, ref pos, null) ?? throw new InvalidDataException($"픽셀 데이터가 부족합니다 ({i}/{count}).");
                if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 0)
                    throw new InvalidDataException($"잘못된 픽셀 값: {t}");
                map.Data[i] = (byte)Math.Min(v, 255);
            }
        }

        return map;
    }

    /// <summary>임시 파일에 쓴 뒤 교체 (저장 중 오류 시 원본 보호)</summary>
    public static void Write(string path, MapImage map)
    {
        byte[] bytes = Encode(map);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    public static byte[] Encode(MapImage map)
    {
        using var ms = new MemoryStream(map.Data.Length + 256);

        var header = new StringBuilder();
        header.Append(map.Format == PgmFormat.P5 ? "P5\n" : "P2\n");
        foreach (string c in map.Comments) header.Append('#').Append(c).Append('\n');
        header.Append(map.Width.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(map.Height.ToString(CultureInfo.InvariantCulture)).Append('\n')
              .Append(map.MaxVal.ToString(CultureInfo.InvariantCulture)).Append('\n');
        byte[] hb = Encoding.Latin1.GetBytes(header.ToString());
        ms.Write(hb, 0, hb.Length);

        if (map.Format == PgmFormat.P5)
        {
            ms.Write(map.Data, 0, map.Data.Length);
        }
        else
        {
            // ASCII: 한 줄 70자 이내 권장 → 16개씩
            var sb = new StringBuilder(map.Data.Length * 4);
            for (int i = 0; i < map.Data.Length; i++)
            {
                sb.Append(map.Data[i].ToString(CultureInfo.InvariantCulture));
                sb.Append((i + 1) % 16 == 0 || i == map.Data.Length - 1 ? '\n' : ' ');
            }
            byte[] db = Encoding.Latin1.GetBytes(sb.ToString());
            ms.Write(db, 0, db.Length);
        }

        return ms.ToArray();
    }

    private static int ReadInt(byte[] b, ref int pos, List<string> comments, string name)
    {
        string t = NextToken(b, ref pos, comments) ?? throw new InvalidDataException($"헤더가 잘렸습니다 ({name}).");
        if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            throw new InvalidDataException($"헤더 {name} 값이 올바르지 않습니다: {t}");
        return v;
    }

    /// <summary>공백/주석을 건너뛰고 다음 토큰 반환. 토큰 뒤 구분 문자는 소비하지 않음</summary>
    private static string? NextToken(byte[] b, ref int pos, List<string>? comments)
    {
        while (pos < b.Length)
        {
            byte c = b[pos];
            if (c == (byte)'#')
            {
                int start = ++pos;
                while (pos < b.Length && b[pos] != (byte)'\n' && b[pos] != (byte)'\r') pos++;
                comments?.Add(Encoding.Latin1.GetString(b, start, pos - start));
                continue;
            }
            if (IsSpace(c)) { pos++; continue; }
            break;
        }
        if (pos >= b.Length) return null;

        int s = pos;
        while (pos < b.Length && !IsSpace(b[pos]) && b[pos] != (byte)'#') pos++;
        return Encoding.ASCII.GetString(b, s, pos - s);
    }

    private static bool IsSpace(byte c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f';
}
