#!/usr/bin/env python3
"""Move XAML/AXAML literals onto the design scale (D1 tokens, hearthpulse-design "Scale (D1)").

Usage (repository root):  python -I tools/design/migrate_tokens.py <file.xaml|file.axaml> ...

- FontSize="N" and <Setter Property="FontSize" Value="N"> become {StaticResource FontSize<Role>} (the ramp below);
  off-ramp sizes go to the nearest ramp step listed in FONT.
- CornerRadius and RoundClip.Radius values snap to RADII; a fully rounded corner (2r >= the element's size, or its
  parent's size less the element's margin) is kept, so circles and pills stay round.
- Margin/Padding values snap to the spacing scale: 0, 1, 2, 4, 8, 12, 16, 24, 32 and multiples of 4 from 40 up.
- Ties go down (6 -> 4, 10 -> 8): a migrated row never grows wider than it was.
- Nothing inside a ControlTemplate changes: template geometry (tracks, knobs, underlines) is drawn to its own size.
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
RADIUS_KEYS = ("CornerRadius", "RoundClip.Radius")

TOKEN = re.compile(r'<!--.*?-->|<(/?)([\w:.|]+)((?:\s+[\w:.()|-]+\s*=\s*"[^"]*")*)\s*(/?)>', re.S)
ATTR = re.compile(r'([\w:.()|-]+)(\s*=\s*")([^"]*)(")')


def num(text):
    try:
        v = float(text)
    except ValueError:
        return None
    return int(v) if v == int(v) else v


def fmt(v):
    return str(int(v)) if v == int(v) else str(v)


def nearest(v, scale):
    return min(scale, key=lambda c: (abs(c - v), c))  # ties go down


def on_spacing(v):
    a = abs(v)
    return a in SPACES or (a >= 40 and a == int(a) and int(a) % 4 == 0)


def snap_spacing(v):
    if on_spacing(v):
        return v
    a = abs(v)
    best = nearest(a, SPACES + [40]) if a < 40 else max(40, nearest(a, [4 * (int(a) // 4), 4 * (int(a) // 4 + 1)]))
    return best if v >= 0 else -best


def snap_radius(v):
    return v if v in RADII else nearest(v, [r for r in RADII if r != 999])


def thickness(value, snap):
    vals = [num(p.strip()) for p in value.split(",")]
    if any(v is None for v in vals):
        return value
    return ",".join(fmt(snap(v)) for v in vals)


def font(value):
    v = num(value)
    return value if v is None or v not in FONT else "{StaticResource FontSize" + FONT[v] + "}"


def local(name):
    return name.split(":")[-1]


def size(attrs):
    sizes = [num(attrs[k]) for k in ("Width", "Height") if k in attrs]
    sizes = [s for s in sizes if s is not None]
    return min(sizes) if sizes else None


def margin(attrs):
    vals = [num(p.strip()) for p in attrs.get("Margin", "0").split(",")]
    if any(v is None for v in vals):
        return 0
    l, t, r, b = (vals * 4)[:4] if len(vals) == 1 else (vals + vals)[:4] if len(vals) == 2 else vals[:4]
    return max(l + r, t + b)


def rounded(attrs, parent, value):
    """A pill or a circle: the radius reaches half of the element's smaller side (or its parent's, inside its border)."""
    r = num(value)
    if r is None:
        return False
    own = size(attrs)
    if own is None and parent is not None and size(parent) is not None:
        own = size(parent) - margin(attrs) - 2 * max([num(v) or 0 for v in parent.get("BorderThickness", "0").split(",")])
    return own is not None and 2 * r >= own


def rewrite(attrs, parent, body):
    setter = attrs.get("Property") if "Setter" in attrs.get("#tag", "") else None

    def fix(a):
        key, value = local(a.group(1)), a.group(3)
        if value.startswith("{"):
            return a.group(0)
        kind = setter if setter and key == "Value" else key
        if kind == "FontSize":
            new = font(value)
        elif kind in RADIUS_KEYS or (setter is None and a.group(1).endswith("RoundClip.Radius")):
            new = value if (not setter and rounded(attrs, parent, value)) else thickness(value, snap_radius)
        elif kind in ("Margin", "Padding"):
            new = thickness(value, snap_spacing)
        else:
            return a.group(0)
        return a.group(1) + a.group(2) + new + a.group(4)

    return ATTR.sub(fix, body)


def migrate(text):
    out, pos, stack, template = [], 0, [], 0
    for m in TOKEN.finditer(text):
        if m.group(0).startswith("<!--"):
            continue
        closing, name, body, empty = m.group(1), m.group(2), m.group(3), m.group(4)
        if closing:
            if stack and stack[-1][0] == name:
                stack.pop()
            if local(name) == "ControlTemplate":
                template -= 1
            continue
        attrs = {local(a.group(1)): a.group(3) for a in ATTR.finditer(body)}
        attrs["#tag"] = local(name)
        if template == 0 and local(name) != "ControlTemplate":
            parent = next((p for n, p in reversed(stack) if "." not in local(n)), None)
            start = m.start() + 1 + len(name)
            out.append(text[pos:start] + rewrite(attrs, parent, body))
            pos = start + len(body)
        if not empty:
            stack.append((name, attrs))
            if local(name) == "ControlTemplate":
                template += 1
    out.append(text[pos:])
    return "".join(out)


def main(paths):
    for path in map(Path, paths):
        raw = path.read_bytes()
        text = raw.decode("utf-8-sig")
        new = migrate(text)
        if new != text:
            bom = b"\xef\xbb\xbf" if raw.startswith(b"\xef\xbb\xbf") else b""
            path.write_bytes(bom + new.encode("utf-8"))
            print("migrated", path)


if __name__ == "__main__":
    main(sys.argv[1:])
