"""Crops a region of a screenshot and scales it up with sharp pixels, for README close-ups of small UI.

Usage: python tests/crop-shot.py SOURCE.png X Y WIDTH HEIGHT SCALE OUT.png
  X, Y, WIDTH, HEIGHT in the source's pixels (readme-shots.sh takes them at 1280x720). Needs Pillow.
"""
import sys

from PIL import Image

if len(sys.argv) != 8:
    sys.exit(__doc__)
src, x, y, w, h, scale, out = sys.argv[1], *map(int, sys.argv[2:7]), sys.argv[7]
im = Image.open(src).convert("RGB").crop((x, y, x + w, y + h))
im.resize((w * scale, h * scale), Image.NEAREST).save(out, optimize=True)
print(f"{out}: {w * scale}x{h * scale}")
