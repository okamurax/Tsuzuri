# アプリのアイコン（Tsuzuri/App.ico）を作る（SPEC 16章）。
# 使い方: Pillow を入れた Python で
#   python make_icon.py ../Tsuzuri/App.ico preview.png
# 2つ目の引数は確認用の一覧画像。
#
# 紺の角丸の地に、重ねた2枚の紙と、それをとめる青い糸（2026-11-02。Tsuzuri＝綴る。前は # を1つ）。
# 小さいサイズでは紙を大きく、糸を太く長くして、潰れないようにする。
import sys
from PIL import Image, ImageDraw

NAVY   = (0x19, 0x23, 0x39, 255)
FRAME  = (0x31, 0x38, 0x4A, 255)
PAPER  = (0xEE, 0xF1, 0xF6, 255)   # 手前の紙
BEHIND = (0x9F, 0xAE, 0xC8, 255)   # 後ろの紙（紺になじむ灰青）
THREAD = (0x4A, 0x7C, 0xE8, 255)   # 糸。本体の強調の青

def render(size):
    S = 8
    W = size * S
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    u = W / 256.0

    small = size < 32
    m = 0 if small else 8
    d.rounded_rectangle([m*u, m*u, (256-m)*u, (256-m)*u], radius=54*u, fill=NAVY,
                        outline=None if small else FRAME, width=0 if small else int(4*u))

    # 紙（左上・右下）。後ろの紙は右下へずらす
    L, T, R, B = (64, 44, 176, 196) if not small else (40, 30, 190, 202)
    off = 22 if not small else 26
    d.rectangle([(L+off)*u, (T+off)*u, (R+off)*u, (B+off)*u], fill=BEHIND)
    d.rectangle([L*u, T*u, R*u, B*u], fill=PAPER)

    # 糸。手前の紙の左端から外へ出る横棒（縦の真ん中）
    t = 14 if not small else 30
    reach = 52 if not small else 74
    y = (T + B) / 2
    d.rectangle([(L-14)*u, (y-t/2)*u, (L+reach)*u, (y+t/2)*u], fill=THREAD)

    return img.resize((size, size), Image.LANCZOS)

out = sys.argv[1]
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
images = [render(s) for s in sizes]
# 中身は PNG ではなくビットマップで入れる。Windows 本体は PNG でも読めるが、
# exe からアイコンを抜き出す外のソフト（Parallels が Mac の Dock に出すとき等）は
# ビットマップしか読めないことがある。
images[-1].save(out, format="ICO", sizes=[(s, s) for s in sizes], append_images=images[:-1],
                bitmap_format="bmp")

if len(sys.argv) > 2:
    sheet = Image.new("RGBA", (sum(sizes) + 20 * len(sizes) + 20, 600), (0x10, 0x18, 0x27, 255))
    x = 20
    for s, im in zip(sizes, images):
        sheet.paste(im, (x, 20), im)
        x += s + 20
    light = Image.new("RGBA", (300, 280), (0xF3, 0xF3, 0xF3, 255))
    for i, (px, idx) in enumerate(((20, 0), (60, 2), (110, 3), (170, 5))):
        light.paste(images[idx], (px, 20), images[idx])
    sheet.paste(light, (20, 300))
    sheet.save(sys.argv[2])
