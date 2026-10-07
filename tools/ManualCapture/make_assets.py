"""ManualCapture 결과(스크린샷 · 프레임)를 설명서용 이미지로 만든다.

    python make_assets.py <capture 출력 폴더> <이미지 출력 폴더>

- shots/*.png   → 그대로, 또는 SPEC에 따라 번호 · 강조 상자를 그리고 잘라냄
- frames/<이름>  → 커서를 그려 넣은 GIF (<이름>.gif)
"""
import json
import os
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFont

ACCENT = (234, 106, 18)      # 번호 · 강조 (앱 파란색과 구분되는 주황)
GIF_WIDTH = 960

# 스크린샷별 후처리
#   marks: [(요소 id | 'status', 번호 또는 None)]  → 주황 상자 + 번호
#   crop:  'dialog' | 요소 id 목록(합친 범위) | None(창 전체)
SPEC = {
    '03-main': {'marks': [('ToolbarRoot', 1), ('RailRoot', 2), ('MapViewer', 3), ('InspectorRoot', 4), ('status', 5)]},
    '04-tooltip': {'crop_box': ('ToolWall', 0, -12, 400, 124)},
    '02-open-dialog': {'crop': 'dialog'},
    '13-deskew-confirm': {'crop': 'dialog'},
    '16-close-confirm': {'crop': 'dialog'},
    '29-save-confirm': {'crop': 'dialog'},
    '43-about': {'crop': 'dialog'},
    '10-clean-tab': {'crop': ['InspectorRoot']},
    '11-noise-found': {'marks': [('NoiseResults', None)]},
    '12-noise-deleted': {'marks': [('status', None)]},
    '20-update-empty': {'crop': ['InspectorRoot']},
    '22-update-area': {'marks': [('AddAreaButton', None)]},
    '23-update-revert': {'marks': [('RevertOutsideButton', None)]},
    '24-update-offset': {'marks': [('OffsetText', None)]},
    '26-update-dup': {'marks': [('DupResults', None)]},
    '28-update-done': {'crop': ['InspectorRoot']},
    '30-saved': {'marks': [('status', None)]},
    '40-tab-second': {'crop': ['InspectorRoot']},
    '41-tab-dxf': {'crop': ['InspectorRoot']},
}

# GIF별 잘라낼 범위 (창 기준 비율 x0, y0, x1, y1) · 기본은 창 전체
GIF_SPEC = {}


def font(size):
    for f in ('C:/Windows/Fonts/malgunbd.ttf', 'C:/Windows/Fonts/arialbd.ttf',
              '/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf'):
        if os.path.exists(f):
            return ImageFont.truetype(f, size)
    return ImageFont.load_default()


def rect_of(rects, key, img):
    if key == 'status':
        wx, wy, ww, wh = rects['window']
        return (wx + 1, wy + wh - 29, wx + ww - 1, wy + wh - 1)
    if key not in rects:
        return None
    x, y, w, h = rects[key]
    return (x, y, x + w, y + h)


def badge(d, cx, cy, n, r=15):
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=ACCENT, outline=(255, 255, 255), width=3)
    f = font(17)
    t = str(n)
    b = d.textbbox((0, 0), t, font=f)
    d.text((cx - (b[2] - b[0]) / 2 - b[0], cy - (b[3] - b[1]) / 2 - b[1]), t, fill=(255, 255, 255), font=f)


def mark(img, box, n):
    d = ImageDraw.Draw(img)
    x0, y0, x1, y1 = box
    d.rounded_rectangle((x0 + 2, y0 + 2, x1 - 2, y1 - 2), radius=6, outline=ACCENT, width=4)
    if n is not None:
        badge(d, min(x0 + 22, x1 - 20), min(y0 + 22, y1 - 20), n)


def process_shot(src, dst, name, rects):
    img = Image.open(src).convert('RGB')
    spec = SPEC.get(name, {})
    for key, n in spec.get('marks', []):
        box = rect_of(rects, key, img)
        if box:
            mark(img, box, n)
    crop = spec.get('crop')
    if crop == 'dialog' and 'dialog' in rects:
        x, y, w, h = rects['dialog']
        img = img.crop((max(0, x), max(0, y), min(img.width, x + w), min(img.height, y + h)))
    elif isinstance(crop, list):
        boxes = [rect_of(rects, k, img) for k in crop]
        boxes = [b for b in boxes if b]
        if boxes:
            img = img.crop((min(b[0] for b in boxes), min(b[1] for b in boxes),
                            max(b[2] for b in boxes), max(b[3] for b in boxes)))
    if 'crop_box' in spec:
        key, dx, dy, w, h = spec['crop_box']
        b = rect_of(rects, key, img)
        if b:
            img = img.crop((max(0, b[0] + dx), max(0, b[1] + dy), min(img.width, b[0] + dx + w), min(img.height, b[1] + dy + h)))
    img.save(dst, optimize=True)


CURSOR = [(0, 0), (0, 17), (4, 13), (7, 20), (10, 19), (7, 12), (12, 12)]


def draw_cursor(img, x, y, down):
    d = ImageDraw.Draw(img)
    if down:
        d.ellipse((x - 16, y - 16, x + 16, y + 16), outline=ACCENT, width=4)
    pts = [(x + px * 1.25, y + py * 1.25) for px, py in CURSOR]
    d.polygon(pts, fill=(20, 20, 20), outline=(255, 255, 255))
    d.line(pts + [pts[0]], fill=(255, 255, 255), width=2)


def process_gif(fdir, dst, name):
    lines = [l.split() for l in open(os.path.join(fdir, 'cursor.txt'), encoding='utf-8') if l.strip()]
    if not lines:
        return
    frames, durs = [], []
    first = Image.open(os.path.join(fdir, '0000.png'))
    W, H = first.size
    x0, y0, x1, y1 = GIF_SPEC.get(name, (0, 0, 1, 1))
    box = (int(W * x0), int(H * y0), int(W * x1), int(H * y1))
    scale = min(1.0, GIF_WIDTH / (box[2] - box[0]))
    prev = None
    for i, (idx, ms, cx, cy, down) in enumerate(lines):
        p = os.path.join(fdir, '%04d.png' % int(idx))
        if not os.path.exists(p):
            continue
        img = Image.open(p).convert('RGB')
        draw_cursor(img, int(cx), int(cy), down == '1')
        img = img.crop(box)
        if scale < 1:
            img = img.resize((int(img.width * scale), int(img.height * scale)), Image.LANCZOS)
        nxt = int(lines[i + 1][1]) if i + 1 < len(lines) else int(ms) + 1500
        dur = max(20, nxt - int(ms))
        if prev is not None and ImageChops.difference(prev, img).getbbox() is None:
            durs[-1] += dur
            continue
        frames.append(img)
        durs.append(dur)
        prev = img
    durs[-1] += 1500  # 끝에서 잠깐 멈춤
    pal = [f.quantize(colors=128, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE) for f in frames]
    pal[0].save(dst, save_all=True, append_images=pal[1:], duration=durs, loop=0, optimize=True, disposal=1)


def main():
    src, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    shots = {s['name']: s['rects'] for s in json.load(open(os.path.join(src, 'shots.json'), encoding='utf-8'))}
    for f in sorted(os.listdir(os.path.join(src, 'shots'))):
        name = f[:-4]
        if name.startswith('_'):
            continue
        process_shot(os.path.join(src, 'shots', f), os.path.join(out, f), name, shots.get(name, {}))
        print('shot', f)
    fr = os.path.join(src, 'frames')
    for name in sorted(os.listdir(fr)) if os.path.isdir(fr) else []:
        d = os.path.join(fr, name)
        if os.path.exists(os.path.join(d, 'cursor.txt')):
            process_gif(d, os.path.join(out, name + '.gif'), name)
            print('gif', name, os.path.getsize(os.path.join(out, name + '.gif')) // 1024, 'KB')


if __name__ == '__main__':
    main()
