#!/usr/bin/env python3
"""Move XAML/AXAML literals onto the design scale (D1 tokens, docs/design-identity.md).

Usage (repository root):  python -I tools/design/migrate_tokens.py <file.xaml|file.axaml> ...

- FontSize="N" and <Setter Property="FontSize" Value="N"> become {StaticResource FontSize<Role>} (the ramp below);
  off-ramp sizes go to the nearest ramp step listed in FONT.
- CornerRadius values snap to RADII; a fully rounded corner (2r >= the element's Width/Height) is kept.
- Margin/Padding values snap to the spacing scale: 0, 1, 2, 4, 8, 12, 16, 24, 32 and multiples of 4 from 40 up.
Rules match tests/Hearthpulse.Tests/DesignTokenTests.cs, which counts what is left.
"""
import re
import sys
from pathlib import Path

# size -> token (the ramp is 10 11 12 13 14 16 18 20 24 28 32; same values in both theme files)
FONT = {
    8: "Micro", 9: "Micro", 9.5: "Micro", 10: "Micro", 10.5: "Small", 11: "Small", 11.5: "Label", 12: "Label",
    12.5: "Label", 13: "Body", 13.5: "Lead", 14: "Lead", 14.5: "Lead", 15: "Title", 16: "Title", 17: "Icon",
    18: "Icon", 19: "Subhead", 20: "Subhead", 21: "Headline", 22: "Headline", 24: "Headline", 25: "Headline",
    26: "Stat", 28: "Stat", 30: "Display", 32: "Display", 34: "Display", 36: "Display",
}
RADII = [0, 1, 2, 4, 8, 12, 16, 999]
SPACES = [0, 1, 2, 4, 8, 12, 16, 24, 32]

TAG = re.compile(r'<([\w:.|]+)((?:\s+[\w:.()|-]+\s*=\s*"[^"]*")*)\s*(/?)>')
ATTR = re.compile(r'([\w:.()|-]+)(\s*=\s*")([^"]*)(")')


def num(text):
    try:
        v = float(text)
    except ValueError:
        return None
    return int(v) if v == int(v) else v


def fmt(v):
    return str(int(v)) if v == int(v) else str(v)


def on_spacing(v):
    a = abs(v)
    return a in SPACES or (a >= 40 and a == int(a) and int(a) % 4 == 0)


def snap_spacing(v):
    if on_spacing(v):
        return v
    a = abs(v)
    cands = SPACES + [40] if a < 40 else [4 * round(a / 4) if 4 * round(a / 4) >= 40 else 40]
    best = min(cands, key=lambda c: (abs(c - a), -c))  # ties go up: more air
    return best if v >= 0 else -best


def snap_radius(v):
    return v if v in RADII else min((r for r in RADII if r != 999), key=lambda c: (abs(c - v), -c))


def thickness(value, snap):
    parts = [p.strip() for p in value.split(",")]
    vals = [num(p) for p in parts]
    if any(v is None for v in vals):
        return value
    return ",".join(fmt(snap(v)) for v in vals)


def font(value):
    v = num(value)
    if v is None or v not in FONT:
        return value
    return "{StaticResource FontSize" + FONT[v] + "}"


def rounded(attrs, value):
    """Fully rounded corners (a pill or a circle) stay: their radius follows the element's size."""
    r = num(value)
    if r is None:
        return False
    sizes = [num(attrs[k]) for k in ("Width", "Height") if k in attrs]
    sizes = [s for s in sizes if s is not None]
    return bool(sizes) and 2 * r >= min(sizes)


def rewrite_tag(m):
    name, body = m.group(1), m.group(2)
    attrs = {a.group(1): a.group(3) for a in ATTR.finditer(body)}
    setter = name == "Setter" and attrs.get("Property") in ("FontSize", "CornerRadius", "Margin", "Padding")

    def fix(a):
        key, value = a.group(1), a.group(3)
        if value.startswith("{"):
            return a.group(0)
        kind = attrs["Property"] if setter and key == "Value" else key
        if kind == "FontSize":
            new = font(value)
        elif kind == "CornerRadius":
            new = value if (not setter and rounded(attrs, value)) else thickness(value, snap_radius)
        elif kind in ("Margin", "Padding"):
            new = thickness(value, snap_spacing)
        else:
            return a.group(0)
        return a.group(1) + a.group(2) + new + a.group(4)

    start = 1 + len(name)
    return m.group(0)[:start] + ATTR.sub(fix, body) + m.group(0)[start + len(body):]


def main(paths):
    for path in map(Path, paths):
        raw = path.read_bytes()
        text = raw.decode("utf-8-sig")
        new = TAG.sub(rewrite_tag, text)
        if new != text:
            bom = b"\xef\xbb\xbf" if raw.startswith(b"\xef\xbb\xbf") else b""
            path.write_bytes(bom + new.encode("utf-8"))
            print("migrated", path)


if __name__ == "__main__":
    main(sys.argv[1:])
