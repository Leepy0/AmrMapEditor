# 앱 아이콘 생성: python make-icon.py → app.svg · app.ico (16~256) · icon_*.png · preview.png
# 필요: pip install cairosvg pillow
import cairosvg, io
from PIL import Image

def svg(small: bool):
    grid = "" if small else "".join(
        f'<line x1="{x}" y1="64" x2="{x}" y2="192" stroke="#1D1D1F" stroke-opacity="0.07" stroke-width="2"/>' for x in range(72, 200, 16)) + "".join(
        f'<line x1="56" y1="{y}" x2="200" y2="{y}" stroke="#1D1D1F" stroke-opacity="0.07" stroke-width="2"/>' for y in range(80, 192, 16))
    wall = 20 if small else 16
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
<defs>
  <linearGradient id="bg" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="#5E7089"/><stop offset="1" stop-color="#36424F"/>
  </linearGradient>
  <linearGradient id="pen" x1="0" y1="0" x2="1" y2="0">
    <stop offset="0" stop-color="#3B9CFF"/><stop offset="1" stop-color="#0062CC"/>
  </linearGradient>
  <clipPath id="room"><path d="M56 64 H168 V120 H200 V192 H56 Z"/></clipPath>
</defs>
<!-- 배경: Unknown 청회색 -->
<rect x="8" y="8" width="240" height="240" rx="56" fill="url(#bg)"/>
<!-- Free 영역 (흰색) + 픽셀 격자 -->
<path d="M56 64 H168 V120 H200 V192 H56 Z" fill="#FFFFFF"/>
<g clip-path="url(#room)">{grid}</g>
<!-- 벽 (검정) : 오른쪽 아래 구간은 펜이 그리는 중 → 파랑 -->
<path d="M200 184 V120 H168 V64 H56 V192 H140" fill="none" stroke="#1D1D1F" stroke-width="{wall}" stroke-linejoin="miter"/>
<path d="M140 192 H200 V184" fill="none" stroke="#0A84FF" stroke-width="{wall}" stroke-linejoin="miter" stroke-linecap="butt"/>
<!-- 안쪽 장애물 (랙) -->
<rect x="92" y="104" width="44" height="{wall}" fill="#1D1D1F"/>
<!-- 펜 -->
<g transform="translate(202 194) rotate(-45)">
  <path d="M0 0 L14 -24 H-14 Z" fill="#1D1D1F"/>
  <rect x="-14" y="-24" width="28" height="68" fill="url(#pen)"/>
  <rect x="-14" y="-24" width="28" height="10" fill="#FFFFFF" fill-opacity="0.9"/>
  <rect x="-14" y="44" width="28" height="12" rx="4" fill="#0A3F80"/>
  <path d="M0 0 L5 -9 H-5 Z" fill="#9AA3AD"/>
</g>
</svg>'''

sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
imgs = []
for s in sizes:
    png = cairosvg.svg2png(bytestring=svg(s <= 32).encode(), output_width=s, output_height=s)
    im = Image.open(io.BytesIO(png)).convert("RGBA")
    im.save(f"icon_{s}.png")
    imgs.append(im)
open("app.svg", "w").write(svg(False))
imgs[-1].save("app.ico", format="ICO", sizes=[(s, s) for s in sizes], append_images=imgs[:-1])
# 미리보기: 256 + 48 + 32 + 16 나란히 (라이트/다크 배경)
sheet = Image.new("RGBA", (256 + 24 + 48 + 16 + 32 + 16 + 16 + 40, 300), (0, 0, 0, 0))
for bgcol, y in [((242, 242, 247, 255), 0), ((28, 28, 30, 255), 150)]:
    pass
W, H = 520, 320
sheet = Image.new("RGBA", (W, H))
for i, (bgcol, y0) in enumerate([((242, 242, 247, 255), 0), ((28, 28, 30, 255), 160)]):
    sheet.paste(Image.new("RGBA", (W, 160), bgcol), (0, y0))
    x = 16
    for s in [128, 64, 48, 32, 24, 16]:
        im = Image.open(f"icon_{s}.png")
        sheet.alpha_composite(im, (x, y0 + (160 - s) // 2))
        x += s + 20
sheet.save("preview.png")
print("ok")
