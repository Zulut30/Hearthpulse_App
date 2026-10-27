"""Original line-art motifs for the theme backdrops, in a 1000x1000 box centred on 500,500.

Each preset (ThemePalette.Presets) has a stroked line layer, Motif<Name>, and a small filled layer drawn in the second
accent, Motif<Name>Alt. The geometries live in both theme files between the "motifs:begin" and "motifs:end" comments:

    python tools/themes/motifs.py            rewrite that block in Theme.xaml (WPF) and Theme.axaml (Avalonia)
    python tools/themes/motifs.py svg DIR    write one SVG preview per motif into DIR
"""
import math
import sys
from pathlib import Path

C = 500.0

def f(v):
    s = f"{v:.1f}"
    return s[:-2] if s.endswith(".0") else s

def pt(x, y):
    return f"{f(x)},{f(y)}"

def polar(r, deg, cx=C, cy=C):
    a = math.radians(deg)
    return cx + r * math.cos(a), cy + r * math.sin(a)

def circle(cx, cy, r):
    return f"M{pt(cx - r, cy)} A{f(r)},{f(r)} 0 1 0 {pt(cx + r, cy)} A{f(r)},{f(r)} 0 1 0 {pt(cx - r, cy)} Z"

def line(a, b):
    return f"M{pt(*a)} L{pt(*b)}"

def poly(points, close=True):
    s = "M" + pt(*points[0]) + " " + " ".join("L" + pt(*p) for p in points[1:])
    return s + (" Z" if close else "")

def diamond(cx, cy, w, h):
    return poly([(cx, cy - h), (cx + w, cy), (cx, cy + h), (cx - w, cy)])

def sparkle(cx, cy, r, inner=0.22):
    pts = []
    for k in range(8):
        pts.append(polar(r if k % 2 == 0 else r * inner, k * 45 - 90, cx, cy))
    return poly(pts)

def petal(r0, r1, deg, bulge):
    a, b = polar(r0, deg), polar(r1, deg)
    mid = (r0 + r1) / 2
    c1, c2 = polar(mid, deg - bulge), polar(mid, deg + bulge)
    return f"M{pt(*a)} Q{pt(*c1)} {pt(*b)} Q{pt(*c2)} {pt(*a)} Z"

def tavern():
    main = [circle(C, C, 470), circle(C, C, 448), circle(C, C, 300), circle(C, C, 118)]
    for k in range(16):
        main.append(line(polar(132, k * 22.5), polar(288 if k % 2 == 0 else 215, k * 22.5)))
    for k in range(8):
        main.append(petal(304, 440, k * 45, 9))
        main.append(petal(318, 392, k * 45 + 22.5, 5))
    alt = [circle(*polar(459, k * 15), 5) for k in range(24)]
    alt += [diamond(*polar(118, k * 90 + 45), 9, 9) for k in range(4)]
    return main, alt

def frost():
    main = [poly([polar(62, k * 60 - 90) for k in range(6)]), poly([polar(150, k * 60 - 60) for k in range(6)])]
    for k in range(6):
        a = k * 60 - 90
        main.append(line(polar(62, a), polar(470, a)))
        for d, ln in ((190, 120), (290, 96), (380, 62)):
            base = polar(d, a)
            for side in (-1, 1):
                tip = (base[0] + ln * math.cos(math.radians(a + side * 55)), base[1] + ln * math.sin(math.radians(a + side * 55)))
                main.append(line(base, tip))
    alt = [diamond(*polar(470, k * 60 - 90), 13, 13) for k in range(6)]
    alt += [sparkle(*polar(330, k * 60 - 60), 22) for k in range(6)]
    return main, alt

def fel():
    main = [circle(C, C, 470), circle(C, C, 420)]
    teeth = []
    for k in range(36):
        teeth.append(polar(392 if k % 2 == 0 else 336, k * 10))
    main.append(poly(teeth))
    main.append(f"M{pt(190, 500)} Q{pt(500, 270)} {pt(810, 500)} Q{pt(500, 730)} {pt(190, 500)} Z")
    main.append(circle(C, C, 92))
    main.append(f"M{pt(500, 420)} Q{pt(528, 500)} {pt(500, 580)} Q{pt(472, 500)} {pt(500, 420)} Z")
    alt = [diamond(*polar(445, k * 30 + 15), 10, 10) for k in range(12)]
    alt.append(circle(500, 500, 14))
    return main, alt

def arcane():
    main = []
    for rot in (0, 60, 120):
        rx, ry = 450, 150
        a, b = polar(rx, rot), polar(rx, rot + 180)
        main.append(f"M{pt(*a)} A{f(rx)},{f(ry)} {f(rot)} 1 0 {pt(*b)} A{f(rx)},{f(ry)} {f(rot)} 1 0 {pt(*a)} Z")
    main.append(circle(C, C, 470))
    main.append(sparkle(C, C, 120, 0.2))
    def on_orbit(rot, t):
        x, y = 450 * math.cos(math.radians(t)), 150 * math.sin(math.radians(t))
        r = math.radians(rot)
        return C + x * math.cos(r) - y * math.sin(r), C + x * math.sin(r) + y * math.cos(r)
    alt = [circle(*on_orbit(rot, t), 12) for rot, t in ((0, 40), (60, 200), (120, 300), (0, 230), (60, 70))]
    alt += [sparkle(x, y, 26) for x, y in ((160, 170), (850, 240), (790, 840), (210, 800))]
    return main, alt

def flame(x, base, w, h, lean=0.0):
    tip = (x + lean * w, base - h)
    return (f"M{pt(x, base)} C{pt(x - w * 0.55, base)} {pt(x - w * 0.62, base - h * 0.42)} {pt(x - w * 0.22, base - h * 0.66)}"
            f" C{pt(x - w * 0.02, base - h * 0.8)} {pt(tip[0] - w * 0.1, base - h * 0.9)} {pt(*tip)}"
            f" C{pt(x + w * 0.36, base - h * 0.78)} {pt(x + w * 0.58, base - h * 0.5)} {pt(x + w * 0.5, base - h * 0.26)}"
            f" C{pt(x + w * 0.44, base - h * 0.06)} {pt(x + w * 0.2, base)} {pt(x, base)} Z")

def ember():
    main = [flame(500, 930, 540, 820, 0.06), flame(500, 930, 370, 600, -0.05), flame(500, 930, 200, 360, 0.08),
            flame(250, 930, 230, 380, -0.12), flame(760, 930, 250, 440, 0.1)]
    main.append(line((120, 930), (880, 930)))
    alt = [diamond(x, y, 9, 15) for x, y in ((300, 260), (420, 120), (640, 170), (720, 330), (220, 440), (820, 520), (560, 60), (360, 380))]
    return main, alt

def wave(y, amp, length, phase=0.0, x0=0, x1=1000):
    s = f"M{pt(x0, y)}"
    x = x0
    up = True
    while x < x1 - 1:
        nx = min(x + length / 2, x1)
        dy = -amp if up else amp
        s += f" C{pt(x + (nx - x) * 0.36, y + dy)} {pt(nx - (nx - x) * 0.36, y + dy)} {pt(nx, y)}"
        x, up = nx, not up
    return s

def tide():
    main = [wave(560 + i * 100, 32 - i * 3, 250 if i % 2 == 0 else 200) for i in range(4)]
    # A breaking wave: a swell that rises from the left and curls into a spiral at its crest.
    cx, cy, r0 = 640.0, 320.0, 150.0
    sp = []
    for k in range(90):
        t = k / 89
        a = -90 + t * 600
        sp.append(polar(r0 * (1 - 0.86 * t), a, cx, cy))
    crest = sp[0]
    main.append(f"M{pt(0, 480)} C{pt(260, 480)} {pt(400, crest[1])} {pt(*crest)} " + " ".join("L" + pt(*p) for p in sp[1:]))
    main.append(f"M{pt(cx + r0, cy)} C{pt(cx + r0, 420)} {pt(880, 480)} {pt(1000, 480)}")
    alt = [circle(x, y, r) for x, y, r in ((180, 300, 15), (240, 210, 9), (300, 330, 6), (150, 160, 7), (360, 230, 11), (880, 120, 8), (900, 260, 13))]
    return main, alt

def shadow():
    cx, cy, R = 520.0, 500.0, 300.0
    ox, oy, r = 620.0, 430.0, 262.0
    d = math.hypot(ox - cx, oy - cy)
    a = (R * R - r * r + d * d) / (2 * d)
    h = math.sqrt(R * R - a * a)
    px, py = cx + a * (ox - cx) / d, cy + a * (oy - cy) / d
    p1 = (px + h * (oy - cy) / d, py - h * (ox - cx) / d)
    p2 = (px - h * (oy - cy) / d, py + h * (ox - cx) / d)
    main = [f"M{pt(*p1)} A{f(R)},{f(R)} 0 1 0 {pt(*p2)} A{f(r)},{f(r)} 0 1 1 {pt(*p1)} Z"]
    for k in range(18):
        a0, a1 = k * 20 + 3, k * 20 + 14
        s, e = polar(420, a0), polar(420, a1)
        main.append(f"M{pt(*s)} A420,420 0 0 1 {pt(*e)}")
    main.append(circle(C, C, 470))
    alt = [sparkle(x, y, rr) for x, y, rr in ((780, 220, 30), (850, 380, 16), (700, 120, 14), (180, 200, 20), (130, 620, 12), (760, 760, 22), (300, 860, 14))]
    return main, alt

MOTIFS = [("Tavern", tavern), ("Frost", frost), ("Fel", fel), ("Arcane", arcane), ("Ember", ember), ("Tide", tide), ("Shadow", shadow)]

THEMES = [("src/Hearthpulse.App/Themes/Theme.xaml", "Geometry", "    "),
          ("src/Hearthpulse.UI/Themes/Theme.axaml", "StreamGeometry", "            ")]


def write_block(path, tag, indent):
    with open(path, encoding="utf-8", newline="") as fh:
        raw = fh.read()
    eol = "\r\n" if "\r\n" in raw else "\n"
    text = raw.replace("\r\n", "\n")
    begin, end = f"{indent}<!-- motifs:begin -->\n", f"{indent}<!-- motifs:end -->\n"
    start, stop = text.index(begin) + len(begin), text.index(end)
    block = ""
    for name, fn in MOTIFS:
        lines, dots = fn()
        block += f'{indent}<{tag} x:Key="Motif{name}">{" ".join(lines)}</{tag}>\n'
        block += f'{indent}<{tag} x:Key="Motif{name}Alt">{" ".join(dots)}</{tag}>\n'
    with open(path, "w", encoding="utf-8", newline="") as fh:
        fh.write((text[:start] + block + text[stop:]).replace("\n", eol))


if __name__ == "__main__":
    if len(sys.argv) > 2 and sys.argv[1] == "svg":
        for name, fn in MOTIFS:
            lines, dots = fn()
            with open(f"{sys.argv[2]}/{name}.svg", "w", encoding="utf-8") as fh:
                fh.write('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1000 1000" width="500" height="500">'
                         '<rect width="1000" height="1000" fill="#1d140c"/>'
                         f'<path d="{" ".join(lines)}" fill="none" stroke="#e9b949" stroke-width="4"/>'
                         f'<path d="{" ".join(dots)}" fill="#4fc2a8"/></svg>')
    else:
        root = Path(__file__).resolve().parents[2]
        for path, tag, indent in THEMES:
            write_block(root / path, tag, indent)