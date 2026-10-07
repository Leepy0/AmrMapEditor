using System;

namespace AmrMapEditor.Core;

/// <summary>캔버스 크기 변경 결과: 새 이미지, 기존 픽셀 (0, 0)이 놓인 위치, 잘려 나간 알려진(Unknown 아닌) 픽셀 수</summary>
public sealed record CanvasResult(MapImage Image, int OffsetX, int OffsetY, long LostKnown);

public static class MapCanvas
{
    /// <summary>
    /// 캔버스 크기 변경 (픽셀 크기 그대로, 확대 · 축소 아님). 기존 이미지를 (offX, offY)에 놓고
    /// 늘어난 곳은 Unknown, 밖으로 나간 곳은 잘림. 음수 오프셋은 왼쪽 · 위를 자름
    /// </summary>
    public static CanvasResult Resize(MapImage src, int newW, int newH, int offX, int offY)
    {
        var dst = new MapImage(newW, newH) { MaxVal = src.MaxVal, Format = src.Format };
        dst.Comments.AddRange(src.Comments);
        long lost = 0;
        for (int y = 0; y < src.Height; y++)
        {
            int ty = y + offY;
            int row = y * src.Width;
            if ((uint)ty >= (uint)newH)
            {
                for (int x = 0; x < src.Width; x++)
                    if (src.Data[row + x] != MapValues.Unknown) lost++;
                continue;
            }
            // 이 행에서 새 캔버스 안에 들어가는 x 범위
            int x0 = Math.Max(0, -offX), x1 = Math.Min(src.Width, newW - offX);
            for (int x = 0; x < src.Width; x++)
                if ((x < x0 || x >= x1) && src.Data[row + x] != MapValues.Unknown) lost++;
            if (x1 > x0) Array.Copy(src.Data, row + x0, dst.Data, ty * newW + x0 + offX, x1 - x0);
        }
        return new CanvasResult(dst, offX, offY, lost);
    }

    /// <summary>
    /// 기준 위치(앵커)에 따른 오프셋. ax · ay: 0 = 왼쪽 · 위, 1 = 가운데, 2 = 오른쪽 · 아래.
    /// 기준 쪽에 붙이고 반대쪽을 늘리거나 자름
    /// </summary>
    public static (int X, int Y) AnchorOffset(int oldW, int oldH, int newW, int newH, int ax, int ay)
    {
        int dw = newW - oldW, dh = newH - oldH;
        int x = ax switch { 0 => 0, 1 => (int)Math.Floor(dw / 2.0), _ => dw };
        int y = ay switch { 0 => 0, 1 => (int)Math.Floor(dh / 2.0), _ => dh };
        return (x, y);
    }
}
