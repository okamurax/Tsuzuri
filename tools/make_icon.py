# アプリのアイコン（Tsuzuri/App.ico）を作る（SPEC 16章）。
# 使い方: Pillow を入れた Python で
#   python make_icon.py ../Tsuzuri/App.ico preview.png
# 2つ目の引数は確認用の一覧画像。
#
# 紺の角丸の地に、# を1つ。縦棒は少し傾けて、手で書く # の形に寄せる。
# 小さいサイズでは線を太く、余白を詰めて、潰れないようにする。
import sys
from PIL import Image, ImageDraw

NAVY  = (0x19, 0x23, 0x39, 255)
FRAME = (0x31, 0x38, 0x4A, 255)
# 本体の青（リンク #5D89EA・強調 #4A7CE8）より一段暗くして、紺の画面になじませる。
BLUE  = (0x3D, 0x66, 0xC2, 255)

def render(size, blue=BLUE, thin=False):
    S = 8
    W = size * S
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    u = W / 256.0

    small = size < 32
    m = 0 if small else 8
    d.rounded_rectangle([m*u, m*u, (256-m)*u, (256-m)*u], radius=54*u, fill=NAVY,
                        outline=None if small else FRAME, width=0 if small else int(4*u))

    # # の大きさと線の太さ。小さいときは大きく・太く
    span = 150 if small else 124      # # 全体の幅と高さ（地の 256 に対して。2026-10-11 に小さくした）
    bar = (20 if small else 13) if thin else (28 if small else 20)   # 線の太さ
    slant = 12                         # 縦棒の傾き（上が右へ）
    c = 128
    lo, hi = c - span / 2, c + span / 2

    # 横棒2本。縦の位置は 1/3 と 2/3 あたり
    for t in (0.33, 0.67):
        y = lo + span * t
        d.rectangle([lo*u, (y - bar/2)*u, hi*u, (y + bar/2)*u], fill=blue)

    # 縦棒2本（平行四辺形）
    for t in (0.33, 0.67):
        x = lo + span * t
        top, bottom = lo, hi
        d.polygon([((x - bar/2 + slant)*u, top*u), ((x + bar/2 + slant)*u, top*u),
                   ((x + bar/2 - slant)*u, bottom*u), ((x - bar/2 - slant)*u, bottom*u)], fill=blue)

    return img.resize((size, size), Image.LANCZOS)

out = sys.argv[1]
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
images = [render(s, thin=True) for s in sizes]
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
