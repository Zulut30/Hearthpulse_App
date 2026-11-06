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

def rot(p, deg, c=(C, C)):
    a = math.radians(deg)
    x, y = p[0] - c[0], p[1] - c[1]
    return c[0] + x * math.cos(a) - y * math.sin(a), c[1] + x * math.sin(a) + y * math.cos(a)

def leaf(base, tip, left, right):
    return f"M{pt(*base)} Q{pt(*left)} {pt(*tip)} Q{pt(*right)} {pt(*base)} Z"

def grove():
    """Druid's grove: a ring of twelve leaves with their veins round an inner wreath of six."""
    main = [circle(C, C, 470), circle(C, C, 180)]
    for i in range(12):
        a = 30 * i
        base = rot((500, 300), a)
        main.append(leaf(base, rot((500, 90), a), rot((420, 190), a), rot((580, 190), a)))
        main.append(line(base, rot((500, 140), a)))
    for i in range(6):
        a = 60 * i + 30
        main.append(leaf(rot((500, 360), a), rot((500, 250), a), rot((455, 305), a), rot((545, 305), a)))
    alt = [circle(C, C, 10)] + [circle(*rot((500, 40), 30 * i + 15), 6) for i in range(12)]
    return main, alt

def void():
    """The void: three spiral arms pulling into a double ring."""
    main = [circle(C, C, 470)]
    for arm in range(3):
        pts = []
        for k in range(90):
            t = k / 89
            ang = arm * 2 * math.pi / 3 + t * 3.4 * math.pi
            r = 40 + 400 * t
            pts.append((C + r * math.cos(ang), C + r * math.sin(ang)))
        main.append(poly(pts, close=False))
    main += [circle(C, C, 60), circle(C, C, 28)]
    alt = [sparkle(x, y, r, 0.18) for x, y, r in ((170, 190, 22), (820, 160, 16), (880, 640, 24), (150, 760, 14), (640, 880, 18), (300, 420, 10), (720, 330, 12))]
    return main, alt

def bloodsteel():
    """Blood and steel: a warrior's shield behind two crossed blades."""
    main = ["M500,150 L780,240 C780,560 690,760 500,870 C310,760 220,560 220,240 Z",
            "M500,200 L735,276 C735,550 655,720 500,815 C345,720 265,550 265,276 Z"]
    for deg in (218, 142):
        main.append(poly([rot(p, deg) for p in ((486, 120), (514, 120), (514, 700), (500, 740), (486, 700))]))
        main.append(poly([rot(p, deg) for p in ((430, 690), (570, 690), (570, 712), (430, 712))]))
        main.append(poly([rot(p, deg) for p in ((492, 712), (508, 712), (508, 820), (492, 820))]))
        main.append(circle(*rot((500, 840), deg), 18))
    main.append(circle(C, C, 470))
    alt = [circle(500, 470, 14)] + [diamond(*rot((500, 30), 45 * i + 22.5), 10, 10) for i in range(8)]
    return main, alt

def sands():
    """Ancient lands: a stepped pyramid under a rayed sun, dunes below."""
    main = [poly([(500, 330), (800, 800), (200, 800)])]
    for y in (470, 590, 700):
        t = (y - 330) / 470
        main.append(line((500 - 300 * t, y), (500 + 300 * t, y)))
    main += [line((500, 330), (560, 800)), circle(500, 220, 70)]
    for i in range(12):
        a = 30 * i
        main.append(line(polar(90, a, 500, 220), polar(130, a, 500, 220)))
    main += ["M40,860 C200,820 330,820 500,860 C670,900 800,900 960,860",
             "M80,930 C230,900 360,900 500,930 C640,960 770,960 920,930"]
    alt = [diamond(x, y, 9, 9) for x, y in ((140, 300), (860, 300), (200, 520), (800, 520), (110, 700), (890, 700))]
    return main, alt

def midnight():
    """Midnight: a dial of hour ticks round a constellation."""
    main = [circle(C, C, 470)]
    for i in range(24):
        a = 15 * i
        main.append(line(polar(410 if i % 6 == 0 else 440, a), polar(455, a)))
    stars = [(300, 300), (420, 250), (560, 330), (650, 260), (720, 420), (600, 560), (430, 600), (330, 520)]
    main.append(poly(stars, close=False))
    alt = [sparkle(x, y, 26, 0.18) for x, y in stars] + [sparkle(x, y, 12, 0.18) for x, y in ((200, 700), (780, 720), (520, 800), (830, 200))]
    return main, alt

def mech():
    """Battlegrounds mechs: a twelve-tooth cog with spokes and bolts."""
    pts = [polar(400 if k % 4 in (1, 2) else 340, 360 * k / 48) for k in range(48)]
    main = [poly(pts), circle(C, C, 250), circle(C, C, 110)]
    for i in range(6):
        a = 60 * i
        main.append(line(polar(110, a), polar(250, a)))
    alt = [circle(*polar(180, 60 * i + 30), 14) for i in range(6)] + [circle(C, C, 30)]
    return main, alt

MOTIFS = [("Tavern", tavern), ("Frost", frost), ("Fel", fel), ("Arcane", arcane), ("Ember", ember), ("Tide", tide), ("Shadow", shadow),
          ("Grove", grove), ("Void", void), ("Bloodsteel", bloodsteel), ("Sands", sands), ("Midnight", midnight), ("Mech", mech)]

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