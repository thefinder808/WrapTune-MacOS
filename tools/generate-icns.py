#!/usr/bin/env python3
"""
Generate WrapTuneMacOS.icns — the "Bicolor Stack" mark, ported from WrapTune's
Generate-Icon.ps1 (which uses Windows-only System.Drawing). Three iso-stacked
slabs on a cream rounded tile. Uses Pillow to draw + macOS `iconutil` to pack.

Usage:  python3 tools/generate-icns.py [output.icns]
Default output: src/WrapTuneMacOS/WrapTuneMacOS.icns
"""
import os
import subprocess
import sys

from PIL import Image, ImageDraw

TILE = (244, 239, 231, 255)   # #F4EFE7 cream tile
BOT = (14, 138, 122, 255)     # #0E8A7A bottom slab
MID = (43, 191, 169, 255)     # #2BBFA9 middle slab
TOP = (94, 234, 212, 255)     # #5EEAD4 top slab

# Slabs on a 100x100 viewBox (matches Generate-Icon.ps1 exactly).
SLABS = [
    (BOT, [(50, 70), (80, 56), (50, 42), (20, 56)]),
    (MID, [(50, 56), (80, 42), (50, 28), (20, 42)]),
    (TOP, [(50, 42), (80, 28), (50, 14), (20, 28)]),
]

SUPERSAMPLE = 4   # draw large, downscale → smooth edges


def render(size: int) -> Image.Image:
    big = size * SUPERSAMPLE
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    radius = max(2, int(0.22 * big))
    d.rounded_rectangle([0, 0, big - 1, big - 1], radius=radius, fill=TILE)
    scale = big / 100.0
    for color, pts in SLABS:
        d.polygon([(x * scale, y * scale) for x, y in pts], fill=color)
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        repo, "src", "WrapTuneMacOS", "WrapTuneMacOS.icns")
    iconset = os.path.splitext(out)[0] + ".iconset"
    os.makedirs(iconset, exist_ok=True)

    # (base, scale) → Apple's required iconset members.
    cache: dict[int, Image.Image] = {}
    for base, scale in [(16, 1), (16, 2), (32, 1), (32, 2), (128, 1),
                        (128, 2), (256, 1), (256, 2), (512, 1), (512, 2)]:
        px = base * scale
        cache.setdefault(px, render(px))
        suffix = "@2x" if scale == 2 else ""
        cache[px].save(os.path.join(iconset, f"icon_{base}x{base}{suffix}.png"))

    subprocess.run(["iconutil", "-c", "icns", iconset, "-o", out], check=True)
    # iconutil keeps the .iconset around; remove it so it isn't committed.
    for f in os.listdir(iconset):
        os.remove(os.path.join(iconset, f))
    os.rmdir(iconset)
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
