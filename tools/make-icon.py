# -*- coding: utf-8 -*-
"""生成 DirPorter 应用图标（多尺寸 .ico）。

设计：圆角方形品牌底（主题蓝 #2F6FED 渐变）+ 白色文件夹 + 蓝色搬运箭头（挖空效果）。
小尺寸（16/20/24/32）单独绘制：箭头加粗、间距收紧，保证可读性。

用法：python tools/make-icon.py
产出：
  src/DirPorter.App/Resources/app.ico     （16/20/24/32/40/48/64/128/256 九帧，PNG 编码）
  tools/icon-preview.png                  （放大预览，仅供人眼检查，不参与打包）
"""

import io
import struct
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
OUT_ICO = ROOT / "src" / "DirPorter.App" / "Resources" / "app.ico"
OUT_PREVIEW = ROOT / "tools" / "icon-preview.png"

# 品牌色（与 Themes/Light.xaml AccentBg 一致）
C_TOP = (0x4C, 0x86, 0xF5)   # 渐变亮端
C_BOT = (0x20, 0x50, 0xBE)   # 渐变暗端
C_FOLDER = (0xFF, 0xFF, 0xFF)
C_ARROW = (0x1B, 0x49, 0xA8)  # 深蓝箭头（挖空感）

BASE = 256  # 内部高分辨率基准


def _rounded_gradient(size: int) -> Image.Image:
    """圆角方形 + 垂直渐变底。"""
    w = size
    img = Image.new("RGBA", (w, w), (0, 0, 0, 0))

    # 渐变（上亮下暗）
    grad = Image.linear_gradient("L").resize((w, w), Image.LANCZOS)
    top = Image.new("RGB", (w, w), C_TOP)
    bot = Image.new("RGB", (w, w), C_BOT)
    body = Image.composite(top, bot, grad).convert("RGBA")

    # 圆角蒙版（留 2% 边距，避免抗锯齿贴边）
    pad = max(1, round(w * 0.012))
    inner = w - pad * 2
    mask = Image.new("L", (w, w), 0)
    md = ImageDraw.Draw(mask)
    md.rounded_rectangle((pad, pad, pad + inner, pad + inner), radius=round(inner * 0.225), fill=255)
    img.paste(body, (0, 0), mask)
    return img


def _folder_arrow(size: int, k: float) -> Image.Image:
    """白色文件夹 + 蓝色箭头主图形（透明背景）。"""
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(canvas)

    def r(v):  # 以 256 为基准换算
        return round(v * k)

    # 小尺寸（≤24）统一加粗 / 放大箭头，保证 16px 可读
    small = size <= 24
    thick_base = 38 if small else 26
    span_base = 50 if small else 38
    head_x_base = 128 if small else 134
    tip_x_base = 196 if small else 194
    shaft_x0_base = 68 if small else 72

    # ---- 文件夹（标签 + 主体，同一白色联合形状）----
    # 基准坐标（256 系）：标签 42..150，主体 42..214
    tab_x0, tab_x1 = r(42), r(150)
    tab_y0, tab_y1 = r(58), r(112)
    body_x0, body_x1 = r(42), r(214)
    body_y0, body_y1 = r(84), r(206)
    d.rounded_rectangle((tab_x0, tab_y0, tab_x1, tab_y1), radius=r(14), fill=C_FOLDER)
    d.rounded_rectangle((body_x0, body_y0, body_x1, body_y1), radius=r(20), fill=C_FOLDER)

    # ---- 搬运箭头（挖空风：深蓝叠在白文件夹上）----
    cy = (body_y0 + body_y1) // 2
    thick = r(thick_base)
    d.rounded_rectangle(
        (r(shaft_x0_base), cy - thick // 2, r(head_x_base), cy + thick // 2),
        radius=thick // 2,
        fill=C_ARROW,
    )
    span = r(span_base)
    d.polygon(
        [(r(head_x_base), cy - span), (r(head_x_base), cy + span), (r(tip_x_base), cy)],
        fill=C_ARROW,
    )
    return canvas


def draw(size: int) -> Image.Image:
    """按目标尺寸绘制（16~32 用小尺寸强化参数）。"""
    ss = 8 if size <= 48 else 2   # 小尺寸超采样倍率更高，抗锯齿更干净
    w = size * ss
    k = w / BASE

    img = _rounded_gradient(w)
    folder = _folder_arrow(w, k)

    if size <= 24:
        # 极小尺寸：箭头再加粗一点（视觉补偿），文件夹稍收窄让白块更饱满
        folder = folder.filter(ImageFilter.SMOOTH)

    img.alpha_composite(folder)
    img = img.resize((size, size), Image.LANCZOS)
    return img


def pack_ico(frames: dict[int, Image.Image], out: Path) -> None:
    """手写 ICO 容器（PNG 帧，Vista+ 兼容）。"""
    entries, blobs = [], []
    offset = 6 + 16 * len(frames)
    for size in sorted(frames):
        buf = io.BytesIO()
        frames[size].save(buf, format="PNG", optimize=True)
        data = buf.getvalue()
        dim = 0 if size >= 256 else size
        entries.append(struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset))
        blobs.append(data)
        offset += len(data)

    with open(out, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(frames)))
        for e in entries:
            f.write(e)
        for b in blobs:
            f.write(b)


def main() -> None:
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = {s: draw(s) for s in sizes}

    OUT_ICO.parent.mkdir(parents=True, exist_ok=True)
    pack_ico(frames, OUT_ICO)

    # 预览图：各尺寸放大后并排（仅人工检查用）
    pad = 16
    zoom = 4
    cell = 160
    total_w = pad + len(sizes) * (cell + pad)
    h = cell + pad * 2
    preview = Image.new("RGBA", (total_w, h), (245, 246, 248, 255))
    x = pad
    for s in sizes:
        big = frames[s].resize((cell, cell), Image.NEAREST if s <= 32 else Image.LANCZOS)
        preview.alpha_composite(big, (x, pad))
        x += cell + pad
    preview.save(OUT_PREVIEW)

    print(f"OK: {OUT_ICO} ({OUT_ICO.stat().st_size} bytes, {len(sizes)} frames)")
    print(f"PREVIEW: {OUT_PREVIEW}")


if __name__ == "__main__":
    main()
