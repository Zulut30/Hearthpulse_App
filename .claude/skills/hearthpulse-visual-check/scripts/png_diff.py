#!/usr/bin/env python3
"""Compare two folders of --render-ui PNGs (before / after) and write side-by-side + diff images.

Usage (isolated interpreter, needs Pillow):
  python -I .claude/skills/hearthpulse-visual-check/scripts/png_diff.py <before-dir> <after-dir> <out-dir>

For every PNG name in either folder prints: only-before / only-after / size change / changed-pixel share.
For changed files writes <out-dir>/<name>: before | after | diff (changed pixels in red over a dimmed after),
so one Read of that file shows the change. Identical files are skipped.
"""
import sys
from pathlib import Path

from PIL import Image, ImageChops

THRESHOLD = 24  # per-channel RGBA difference that counts as a change (ignores antialiasing noise)


def compare(before: Path, after: Path, out: Path) -> str:
    with Image.open(before) as source:
        a = source.convert("RGBA")
    with Image.open(after) as source:
        b = source.convert("RGBA")
    note = "" if a.size == b.size else f" size {a.size[0]}x{a.size[1]} -> {b.size[0]}x{b.size[1]}"
    width, height = max(a.width, b.width), max(a.height, b.height)
    pad_a = Image.new("RGBA", (width, height)); pad_a.paste(a)
    pad_b = Image.new("RGBA", (width, height)); pad_b.paste(b)
    # Luminance weights blue at only 11.4%: a blue-only regression of 200 used to read as "same".
    # Alpha matters too: a formerly visible overlay becoming transparent must not pass.
    channels = ImageChops.difference(pad_a, pad_b).split()
    maximum = channels[0]
    for channel in channels[1:]:
        maximum = ImageChops.lighter(maximum, channel)
    mask = maximum.point(lambda v: 255 if v > THRESHOLD else 0)
    changed = mask.histogram()[255] / (width * height)
    if changed == 0 and not note:
        return "same"
    # Composite the previews on a neutral surface so transparency is visible in the comparison sheet.
    background = Image.new("RGBA", (width, height), (30, 30, 30, 255))
    shown_a = Image.alpha_composite(background, pad_a).convert("RGB")
    shown_b = Image.alpha_composite(background, pad_b).convert("RGB")
    diff = Image.blend(Image.new("RGB", (width, height)), shown_b, 0.35)
    diff.paste(Image.new("RGB", (width, height), (255, 40, 40)), mask=mask)
    sheet = Image.new("RGB", (width * 3 + 20, height), (30, 30, 30))
    for i, image in enumerate((shown_a, shown_b, diff)):
        sheet.paste(image, (i * (width + 10), 0))
    if sheet.width > 3000:
        sheet = sheet.resize((3000, int(sheet.height * 3000 / sheet.width)))
    out.mkdir(parents=True, exist_ok=True)
    sheet.save(out / before.name)
    return f"changed {changed:.1%}{note}"


def main() -> int:
    if len(sys.argv) != 4:
        print(__doc__)
        return 2
    before, after, out = (Path(p) for p in sys.argv[1:])
    names = sorted({p.name for p in before.glob("*.png")} | {p.name for p in after.glob("*.png")})
    for name in names:
        if not (after / name).exists():
            print(f"{name}: only before (scene no longer rendered?)")
        elif not (before / name).exists():
            print(f"{name}: only after (new scene)")
        else:
            print(f"{name}: {compare(before / name, after / name, out)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
