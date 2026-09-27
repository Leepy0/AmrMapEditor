using System;

namespace AmrMapEditor.Core;

/// <summary>픽셀 좌표 기준 사각형 (Right/Bottom은 미포함)</summary>
public readonly record struct IntRect(int X, int Y, int Width, int Height)
{
    public static readonly IntRect Empty = new(0, 0, 0, 0);

    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => (long)Width * Height;

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    public bool Contains(IntRect r) =>
        !r.IsEmpty && r.X >= X && r.Y >= Y && r.Right <= Right && r.Bottom <= Bottom;

    /// <summary>두 꼭짓점(둘 다 포함)으로 사각형 생성</summary>
    public static IntRect FromCorners(int x0, int y0, int x1, int y1)
    {
        int l = Math.Min(x0, x1), t = Math.Min(y0, y1);
        int r = Math.Max(x0, x1) + 1, b = Math.Max(y0, y1) + 1;
        return new IntRect(l, t, r - l, b - t);
    }

    public IntRect Intersect(IntRect o)
    {
        int l = Math.Max(X, o.X), t = Math.Max(Y, o.Y);
        int r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r <= l || b <= t ? Empty : new IntRect(l, t, r - l, b - t);
    }

    public IntRect Union(IntRect o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        int l = Math.Min(X, o.X), t = Math.Min(Y, o.Y);
        int r = Math.Max(Right, o.Right), b = Math.Max(Bottom, o.Bottom);
        return new IntRect(l, t, r - l, b - t);
    }

    public IntRect Inflate(int n) => new(X - n, Y - n, Width + 2 * n, Height + 2 * n);

    public override string ToString() => IsEmpty ? "없음" : $"({X}, {Y}) {Width}×{Height}";
}
