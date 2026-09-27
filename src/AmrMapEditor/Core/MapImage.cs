using System;
using System.Collections.Generic;
using System.Globalization;

namespace AmrMapEditor.Core;

public enum PgmFormat { P5, P2 }

/// <summary>8bit 단일 채널 맵 이미지</summary>
public sealed class MapImage
{
    public MapImage(int width, int height, byte[]? data = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "이미지 크기가 올바르지 않습니다.");
        Width = width;
        Height = height;
        Data = data ?? new byte[width * height];
        if (Data.Length != width * height) throw new ArgumentException("데이터 길이가 크기와 맞지 않습니다.", nameof(data));
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }
    public int MaxVal { get; set; } = 255;
    public PgmFormat Format { get; set; } = PgmFormat.P5;

    /// <summary>헤더 주석 ('#' 뒤 텍스트). 저장 시 그대로 보존</summary>
    public List<string> Comments { get; } = new();

    public IntRect Bounds => new(0, 0, Width, Height);

    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

    public byte Get(int x, int y) => Data[y * Width + x];

    public MapImage Clone()
    {
        var copy = new MapImage(Width, Height, (byte[])Data.Clone()) { MaxVal = MaxVal, Format = Format };
        copy.Comments.AddRange(Comments);
        return copy;
    }
}

public enum PixelCategory { Unknown, Free, Obstacle, Probability, Invalid }

/// <summary>AMR 맵 픽셀 값 규칙: Unknown 0, Free 1, 장애물 254, 그 외 확률</summary>
public static class MapValues
{
    public const byte Unknown = 0;
    public const byte Free = 1;
    public const byte Obstacle = 254;
    public const byte Invalid = 255;

    public static PixelCategory Classify(byte v) => v switch
    {
        Unknown => PixelCategory.Unknown,
        Free => PixelCategory.Free,
        Obstacle => PixelCategory.Obstacle,
        Invalid => PixelCategory.Invalid,
        _ => PixelCategory.Probability,
    };

    public static bool IsProbability(byte v) => v >= 2 && v <= 253;

    public static string CategoryName(PixelCategory c) => c switch
    {
        PixelCategory.Unknown => "Unknown",
        PixelCategory.Free => "Free",
        PixelCategory.Obstacle => "장애물",
        PixelCategory.Probability => "확률",
        _ => "범위 외",
    };

    public static string Describe(byte v) => $"{v} (0x{v:X2}) {CategoryName(Classify(v))}";

    /// <summary>값 입력 파싱: 10진수(254), 0xFE, #FEFEFE, FEFEFE 형식 지원</summary>
    public static bool TryParse(string? text, out byte value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();

        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return byte.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

        bool hashed = s.StartsWith('#');
        if (hashed) s = s[1..];

        if (!hashed && byte.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;

        // 색상 표기(RRGGBB 또는 RR)는 첫 바이트 사용
        if ((s.Length == 6 || (hashed && s.Length == 2)) && IsHex(s))
            return byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

        value = 0;
        return false;
    }

    private static bool IsHex(string s)
    {
        foreach (char c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
