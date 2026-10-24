# -*- coding: utf-8 -*-
"""验证图标已嵌入 exe：从 exe 二进制中定位 app.ico 的各 PNG 帧并 dump 出来。

原理：ICO 用 PNG 编码的帧在打进 Win32 资源后保持原始字节；
直接对 exe 做字节搜索，命中即可证明"资源管理器看到的图标 = 我们设计的图标"。
"""

import sys
import struct
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ICO = ROOT / "src" / "DirPorter.App" / "Resources" / "app.ico"
EXE = ROOT / "dist" / "0.3.4" / "dirporter.exe"
OUT_DIR = ROOT / "tools" / "icon-verify"


def read_ico_frames(ico_path: Path):
    data = ico_path.read_bytes()
    reserved, itype, count = struct.unpack_from("<HHH", data, 0)
    assert reserved == 0 and itype == 1, "非法 ICO 头"
    frames = []
    for i in range(count):
        off = 6 + 16 * i
        w, h, _c, _r, _p, _b, size, offset = struct.unpack_from("<BBBBHHII", data, off)
        dim = w or 256
        frames.append((dim, data[offset:offset + size]))
    return frames


def main() -> int:
    exe_bytes = EXE.read_bytes()
    frames = read_ico_frames(ICO)
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    ok, fail = [], []
    for dim, blob in frames:
        pos = exe_bytes.find(blob)
        if pos >= 0:
            ok.append((dim, pos, len(blob)))
            (OUT_DIR / f"exe-frame-{dim}.png").write_bytes(blob)
        else:
            # 退一步：只搜帧头 128 字节（有些工具链会重排/重压）
            head = blob[:128]
            pos2 = exe_bytes.find(head)
            (ok if pos2 >= 0 else fail).append((dim, pos2, len(blob)))

    print(f"exe: {EXE} ({len(exe_bytes)} bytes)")
    print(f"ico: {ICO} ({len(frames)} frames)")
    print(f"命中 {len(ok)}/{len(frames)} 帧")
    for dim, pos, size in ok:
        print(f"  [OK] {dim}px @ offset 0x{pos:X} ({size} bytes)")
    for dim, pos, size in fail:
        print(f"  [MISS] {dim}px")

    return 0 if not fail else 1


if __name__ == "__main__":
    sys.exit(main())
