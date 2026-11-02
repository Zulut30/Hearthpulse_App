"""Builds the Hearthpulse logo as a constructed vector from hearthpulse-logo (128x139).

Shapes: the silhouette S and the dark cup's walls are traced from the source and smoothed; everything else is built on
them, all clipped to S: the yellow fill, the light bevel and the outer rim (strokes of S), the orange ring around the
pulse, the shadow strip under the base line, the cup with the pulse on its top edge, the white pulse. Every colour
is a vertical gradient fitted to the source's own pixels (row medians). The parameters below were fitted by coordinate
descent against the source at 128 (mean difference over grey about 5.8 of 255 per channel).

Writes tools/logo/hearthpulse-logo.svg, src/Hearthpulse.App/Main/Logo.xaml and src/Hearthpulse.UI/Main/Logo.axaml.
Run from the repository root with the site's logo:
  python -I tools/logo/vectorize.py ../reference/repos/HeartPulse/public/hearthpulse-logo.webp
then python -I tools/logo/make_icons.py for the rasters.
"""
import math, pathlib, sys
from PIL import Image

OPT = {k: float(v) for k, v in (a.split('=') for a in sys.argv[2:] if '=' in a)}
P = dict(rim=3.45, bevel=1.5, ring=3.93, pulse=3.55, sigma=1.6, eps=0.3, base=91.2, strip1=96.25, wall_top=97,
         cupedge=0.45, cuplight=2.0)
P.update(OPT)
# pulse centre line, peaks rounded by the stroke's round joins
PULSE = [(-4, P['base']), (39.4, P['base']), (46.9, 76.5), (55.15, 102.4), (63.4, 48.85), (72.75, 110.25), (82.05, 79.25),
         (88.4, P['base']), (132, P['base'])]
for i in range(1, 8):
    if f'px{i}' in OPT: PULSE[i] = (OPT[f'px{i}'], PULSE[i][1])
    if f'py{i}' in OPT: PULSE[i] = (PULSE[i][0], OPT[f'py{i}'])

ROOT = pathlib.Path(__file__).resolve().parents[2]
im = Image.open(sys.argv[1]).convert('RGBA'); W, H = im.size; px = im.load()

def march(field, level=0.5):
    def v(x, y): return field[y][x] if 0 <= x < W and 0 <= y < H else 0.0
    def pt(x0, y0, x1, y1):
        a, b = v(x0, y0), v(x1, y1); t = (level - a) / (b - a)
        return (round(x0 + 0.5 + t * (x1 - x0), 4), round(y0 + 0.5 + t * (y1 - y0), 4))
    segs = []
    for y in range(-1, H):
        for x in range(-1, W):
            tl, tr, br, bl = (v(x, y) >= level, v(x + 1, y) >= level, v(x + 1, y + 1) >= level, v(x, y + 1) >= level)
            T = lambda: pt(x, y, x + 1, y); R = lambda: pt(x + 1, y, x + 1, y + 1)
            B = lambda: pt(x, y + 1, x + 1, y + 1); L = lambda: pt(x, y, x, y + 1)
            idx = tl * 8 + tr * 4 + br * 2 + bl
            table = {1: [(L, B)], 2: [(B, R)], 3: [(L, R)], 4: [(R, T)], 5: [(L, T), (R, B)], 6: [(B, T)], 7: [(L, T)],
                     8: [(T, L)], 9: [(T, B)], 10: [(T, R), (B, L)], 11: [(T, R)], 12: [(R, L)], 13: [(R, B)], 14: [(B, L)]}
            for a, b in table.get(idx, []):
                segs.append((a(), b()))
    nxt = {a: b for a, b in segs}; loops = []
    while nxt:
        start, cur = next(iter(nxt.items())); loop = [start]; del nxt[start]
        while cur != start and cur in nxt:
            loop.append(cur); cur = nxt.pop(cur)
        if len(loop) > 8: loops.append(loop)
    return loops

def turn(pts, i, k=3):
    n = len(pts); a, b, c = pts[(i - k) % n], pts[i], pts[(i + k) % n]
    u, w = (b[0] - a[0], b[1] - a[1]), (c[0] - b[0], c[1] - b[1])
    return abs(math.atan2(u[0] * w[1] - u[1] * w[0], u[0] * w[0] + u[1] * w[1]))

def smooth(loop, closed=True):
    n = len(loop); s = P['sigma']
    corners = set()
    if closed:
        t = [turn(loop, i) for i in range(n)]
        for i in range(n):
            if t[i] > math.radians(70) and t[i] == max(t[(i + j) % n] for j in range(-4, 5)): corners.add(i)
    rad = int(3 * s) + 1; ker = [math.exp(-j * j / (2 * s * s)) for j in range(-rad, rad + 1)]
    out = []
    for i in range(n):
        if i in corners or (not closed and (i < rad or i >= n - rad)):
            out.append(loop[i]); continue
        # do not smooth across a corner
        near = [j for j in range(-rad, rad + 1) if not any(((i + m) % n) in corners for m in range(min(0, j), max(0, j) + 1))]
        wsum = sum(ker[j + rad] for j in near)
        out.append((sum(ker[j + rad] * loop[(i + j) % n][0] for j in near) / wsum, sum(ker[j + rad] * loop[(i + j) % n][1] for j in near) / wsum))
    return out, corners

def dp(points, eps):
    if len(points) < 3: return points
    a, b = points[0], points[-1]; dx, dy = b[0] - a[0], b[1] - a[1]; L = math.hypot(dx, dy)
    best, idx = -1, 0
    for i in range(1, len(points) - 1):
        p = points[i]
        d = abs(dy * (p[0] - a[0]) - dx * (p[1] - a[1])) / L if L > 1e-6 else math.hypot(p[0] - a[0], p[1] - a[1])
        if d > best: best, idx = d, i
    if best <= eps: return [a, b]
    return dp(points[:idx + 1], eps)[:-1] + dp(points[idx:], eps)

def num(v): return (f'{v:.2f}').rstrip('0').rstrip('.')
def fp(p): return f'{num(p[0])},{num(p[1])}'

def bezier(pts, corner, closed=True):
    n = len(pts); d = [f'M{fp(pts[0])}']
    rng = range(n) if closed else range(n - 1)
    for i in rng:
        p0, p1, p2 = pts[i - 1] if (closed or i > 0) else pts[0], pts[i], pts[(i + 1) % n]
        p3 = pts[(i + 2) % n] if (closed or i + 2 < n) else p2
        c1 = corner[i] or (not closed and i == 0); c2 = corner[(i + 1) % n] or (not closed and i + 1 == n - 1)
        t1 = (0, 0) if c1 else ((p2[0] - p0[0]) / 6, (p2[1] - p0[1]) / 6)
        t2 = (0, 0) if c2 else ((p3[0] - p1[0]) / 6, (p3[1] - p1[1]) / 6)
        if t1 == (0, 0) and t2 == (0, 0): d.append(f'L{fp(p2)}')
        else: d.append(f'C{fp((p1[0] + t1[0], p1[1] + t1[1]))} {fp((p2[0] - t2[0], p2[1] - t2[1]))} {fp(p2)}')
    if closed: d.append('Z')
    return ' '.join(d)

def path_of(loop, closed=True):
    sm, corners = smooth(loop, closed)
    if closed:
        start = min(corners) if corners else 0
        sm = sm[start:] + sm[:start]; cs = {(c - start) % len(sm) for c in corners}
        keep = sorted(set(cs) | {0})
        pts, flags = [], []
        bounds = keep + [len(sm)]
        for a, b in zip(bounds, bounds[1:]):
            seg = dp(sm[a:b] + [sm[b % len(sm)]], P['eps'])[:-1]
            for j, q in enumerate(seg): pts.append(q); flags.append(j == 0 and a in cs)
        return bezier(pts, flags, True), pts
    pts = dp(sm, P['eps'])
    return bezier(pts, [False] * len(pts), False), pts

# --- traced shapes
alpha = [[px[x, y][3] / 255 for x in range(W)] for y in range(H)]
S_loop = max(march(alpha), key=len)
S_d, S_pts = path_of(S_loop)

def lum(c): return 0.3 * c[0] + 0.59 * c[1] + 0.11 * c[2]
def navyness(x, y):
    r, g, b, a = px[x, y]
    if y >= 126 and 50 <= x <= 78 and a < 200: return 1.0  # the cup runs out through the bottom of the flame
    if a < 128: return 0.0
    if min(r, g, b) > 150: return 1.0  # the pulse inside the cup
    return max(0.0, min(1.0, (130 - lum((r, g, b))) / 60))
cup = [[navyness(x, y) if y >= P['wall_top'] else (1.0 if 38 <= x <= 92 and y > P['wall_top'] - 3 else 0.0) for x in range(W)] for y in range(H)]
cup_loop = max(march(cup), key=len)
# the walls: the cup's outline below wall_top, left wall going down, around the bottom, right wall up
walls = [p for p in cup_loop if p[1] >= P['wall_top'] + 0.3]
i0 = max(range(len(cup_loop)), key=lambda i: (cup_loop[i][1] >= P['wall_top'] + 0.3 and cup_loop[i - 1][1] < P['wall_top'] + 0.3))
seq = cup_loop[i0:] + cup_loop[:i0]
seq = [p for p in seq if p[1] >= P['wall_top'] + 0.3]
# march loops run clockwise (inside on the right in y-down): starting on the right wall going down; reverse for left->right
if seq[0][0] > seq[-1][0]: seq = seq[::-1]
# the walls end where they reach the lobes' tips at the bottom; from there the cup runs straight out through the bottom
BOTTOM = P.get('tips', 132.5)
k0 = next(i for i, p in enumerate(seq) if p[1] >= BOTTOM)
k1 = len(seq) - 1 - next(i for i, p in enumerate(reversed(seq)) if p[1] >= BOTTOM)
left_d, left_pts = path_of(seq[:k0 + 1], closed=False)
right_d, right_pts = path_of(seq[k1:], closed=False)
walls_d = left_d + ' ' + right_d
walls_pts = left_pts + [(left_pts[-1][0], 150.0), (right_pts[0][0], 150.0)] + right_pts
# the cup: pulse from the left wall's top to the right wall's top, then the walls back
# walls path reversed (right top -> bottom -> left top) appended after the pulse
rev = walls_pts[::-1]
rev_d = (bezier(right_pts[::-1], [False] * len(right_pts), False) + f' L{fp((right_pts[0][0], 150))} L{fp((left_pts[-1][0], 150))} L{fp(left_pts[-1])} '
         + bezier(left_pts[::-1], [False] * len(left_pts), False).split(' ', 1)[1])
cup_d = (f'M{fp(rev[-1])} L{fp(PULSE[1])} ' + ' '.join(f'L{fp(p)}' for p in PULSE[2:8]) + f' L{fp(rev[0])} '
         + rev_d.split(' ', 1)[1] + ' Z')
pulse_d = f'M{fp(PULSE[0])} ' + ' '.join(f'L{fp(p)}' for p in PULSE[1:])

# --- distances for colour sampling
outside = [(x, y) for y in range(H) for x in range(W) if px[x, y][3] < 128 and any(
    0 <= x + dx < W and 0 <= y + dy < H and px[x + dx, y + dy][3] >= 128 for dx in (-1, 0, 1) for dy in (-1, 0, 1))]
EDGED = [[min(math.hypot(x - ox, y - oy) for ox, oy in outside) - 0.5 if px[x, y][3] >= 128 else 0.0 for x in range(W)] for y in range(H)]
def seg_dist(p, a, b):
    dx, dy = b[0] - a[0], b[1] - a[1]; L2 = dx * dx + dy * dy
    t = max(0, min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / L2)) if L2 else 0
    return math.hypot(p[0] - a[0] - t * dx, p[1] - a[1] - t * dy)
def pulse_dist(x, y): return min(seg_dist((x + .5, y + .5), a, b) for a, b in zip(PULSE, PULSE[1:]))
def inside(poly, x, y):
    c = False
    for (x1, y1), (x2, y2) in zip(poly, poly[1:] + poly[:1]):
        if (y1 > y) != (y2 > y) and x < x1 + (y - y1) * (x2 - x1) / (y2 - y1): c = not c
    return c
cup_poly = [PULSE[1]] + PULSE[2:8] + rev

def lum(c): return 0.3 * c[0] + 0.59 * c[1] + 0.11 * c[2]
def median(cs):
    cs = sorted(cs, key=lum); return cs[len(cs) // 2]

def vgrad(sel, y0=0, y1=H, step=4, tol=6):
    """Vertical gradient from row medians of the selected pixels; stops dropped while linear interpolation holds."""
    rows = {}
    for x, y in sel: rows.setdefault(y, []).append(px[x, y][:3])
    ys = sorted(y for y in rows if y0 <= y < y1 and len(rows[y]) >= 1)
    if not ys: return None
    pts = []
    for i in range(0, len(ys), step):
        group = [c for y in ys[i:i + step] for c in rows[y]]
        yy = sum(ys[i:i + step]) / len(ys[i:i + step]) + 0.5
        pts.append((yy, median(group)))
    # drop stops that interpolation predicts within tol
    keep = [pts[0]]
    for i in range(1, len(pts) - 1):
        a, b, c = keep[-1], pts[i], pts[i + 1]
        u = (b[0] - a[0]) / (c[0] - a[0])
        pred = [a[1][j] + (c[1][j] - a[1][j]) * u for j in range(3)]
        if max(abs(pred[j] - b[1][j]) for j in range(3)) > tol: keep.append(b)
    keep.append(pts[-1])
    ya, yb = keep[0][0], keep[-1][0]
    if yb - ya < 0.5: return {'solid': keep[0][1]}
    return {'p0': (0, ya), 'p1': (0, yb), 'stops': [((y - ya) / (yb - ya), c) for y, c in keep]}

opaque = [(x, y) for y in range(H) for x in range(W) if px[x, y][3] >= 250]
def whiteness(x, y): return min(px[x, y][:3])
in_cup = {(x, y) for x, y in opaque if inside(cup_poly, x + .5, y + .5)}
far = lambda x, y: pulse_dist(x, y) > P['pulse'] / 2 + P['ring'] + 1
yellowish = lambda c: c[0] > 200 and c[1] > 120 and c[2] < 90
fill_sel = [(x, y) for x, y in opaque if EDGED[y][x] > P['rim'] + P['bevel'] + 1.2 and (x, y) not in in_cup and far(x, y)
            and yellowish(px[x, y][:3]) and not (P['base'] - 4 < y < P['strip1'] + 1)]
rim_sel = [(x, y) for x, y in opaque if 0.6 <= EDGED[y][x] <= P['rim'] - 0.6 and not (P['base'] - 3 < y < P['strip1'] + 1)]
bevel_sel = [(x, y) for x, y in opaque if P['rim'] + 0.2 <= EDGED[y][x] <= P['rim'] + P['bevel'] - 0.2
             and not (P['base'] - 4 < y < P['strip1'] + 1) and (x, y) not in in_cup]
ring_sel = [(x, y) for x, y in opaque if P['pulse'] / 2 + 0.6 <= pulse_dist(x, y) <= P['pulse'] / 2 + P['ring'] - 0.6
            and (x, y) not in in_cup and EDGED[y][x] > P['rim'] and y < P['base']]
strip_sel = [(x, y) for x, y in opaque if P['base'] + P['pulse'] / 2 + 0.3 <= y + .5 <= P['strip1'] - 0.3 and (x, y) not in in_cup
             and EDGED[y][x] > 1]
cup_sel = [(x, y) for x, y in in_cup if whiteness(x, y) < 90 and EDGED[y][x] > P['rim'] + 0.5 and pulse_dist(x, y) > P['pulse'] / 2 + 0.8]
light_sel = [(x, y) for x, y in opaque if (x, y) not in in_cup and y > P['strip1'] + 1 and EDGED[y][x] > P['rim'] + 1
             and min(seg_dist((x + .5, y + .5), a, b) for a, b in zip(walls_pts, walls_pts[1:])) <= P['cuplight'] - 0.3
             and yellowish(px[x, y][:3])]

fills = {
    'fill': vgrad(fill_sel), 'bevel': vgrad(bevel_sel, step=6), 'rim': vgrad(rim_sel, step=6),
    'ring': vgrad(ring_sel + [(x, y) for x, y in strip_sel], step=2, tol=8), 'strip': vgrad(strip_sel, step=1, tol=4),
    'cup': vgrad(cup_sel, step=3), 'light': vgrad(light_sel, step=6),
    'white': {'solid': median([px[x, y][:3] for x, y in opaque if whiteness(x, y) > 225])},
    'cupedge': {'solid': (0x3a, 0x38, 0x10)},
}

# painter's order; (kind, geometry, brush, pen thickness)
LAYERS = [
    ('fill', S_d, 'fill', None),
    ('stroke', S_d, 'bevel', 2 * (P['rim'] + P['bevel'])),
    ('stroke', S_d, 'rim', 2 * P['rim']),
    ('stroke', pulse_d, 'ring', P['pulse'] + 2 * P['ring']),
    ('rect', f'M-1,{num(P["base"])} H129 V{num(P["strip1"])} H-1 Z', 'strip', None),
    ('stroke', walls_d, 'light', 2 * P['cuplight']),
    ('fill', cup_d, 'cup', None),
    ('stroke', walls_d, 'cupedge', P['cupedge']),
    ('stroke', pulse_d, 'white', P['pulse']),
]

def col(c): return '#%02X%02X%02X' % tuple(int(round(v)) for v in c)

def brush_xaml(b, avalonia):
    if 'solid' in b: return None, col(b['solid'])
    mode = '' if avalonia else ' MappingMode="Absolute"'
    stops = ''.join(f'<GradientStop Offset="{num(o)}" Color="{col(c)}"/>' for o, c in b['stops'])
    return f'<LinearGradientBrush{mode} StartPoint="{fp(b["p0"])}" EndPoint="{fp(b["p1"])}">{stops}</LinearGradientBrush>', None

def xaml(avalonia, layers):
    out = []
    for kind, geo, name, th in layers:
        b = fills[name]
        if b is None: continue
        el, solid = brush_xaml(b, avalonia)
        if kind == 'stroke':
            cap = 'Flat'
            pen_brush = f' Brush="{solid}"' if solid else ''
            inner = f'<Pen.Brush>{el}</Pen.Brush>' if el else ''
            out.append(f'<GeometryDrawing Geometry="{geo}"><GeometryDrawing.Pen><Pen Thickness="{num(th)}" LineJoin="Round"{pen_brush}>{inner}</Pen></GeometryDrawing.Pen></GeometryDrawing>')
        else:
            if solid: out.append(f'<GeometryDrawing Brush="{solid}" Geometry="{geo}"/>')
            else: out.append(f'<GeometryDrawing Geometry="{geo}"><GeometryDrawing.Brush>{el}</GeometryDrawing.Brush></GeometryDrawing>')
    return out

def svg():
    defs = [f'<clipPath id="s"><path d="{S_d}"/></clipPath>']; body = []
    for i, (kind, geo, name, th) in enumerate(LAYERS):
        b = fills[name]
        if b is None: continue
        if 'solid' in b: paint = col(b['solid'])
        else:
            st = ''.join(f'<stop offset="{num(o)}" stop-color="{col(c)}"/>' for o, c in b['stops'])
            defs.append(f'<linearGradient id="g{i}" gradientUnits="userSpaceOnUse" x1="{num(b["p0"][0])}" y1="{num(b["p0"][1])}" x2="{num(b["p1"][0])}" y2="{num(b["p1"][1])}">{st}</linearGradient>')
            paint = f'url(#g{i})'
        if kind == 'stroke': body.append(f'<path d="{geo}" fill="none" stroke="{paint}" stroke-width="{num(th)}" stroke-linejoin="round"/>')
        else: body.append(f'<path d="{geo}" fill="{paint}"/>')
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}">\n<defs>\n'
            + '\n'.join(defs) + '\n</defs>\n<g clip-path="url(#s)">\n' + '\n'.join(body) + '\n</g>\n</svg>\n')

def group(avalonia, layers=None):
    return (f'<GeometryDrawing Brush="Transparent" Geometry="M0,0 H{W} V{H} H0 Z"/>\n'
            f'<DrawingGroup ClipGeometry="{S_d}">\n' + '\n'.join(xaml(avalonia, layers or LAYERS)) + '\n</DrawingGroup>')

HEAD = ('<!-- The Hearthpulse logo, the brand mark: fixed colours that no theme recolours. Generated by\n'
        "     tools/logo/vectorize.py from the site's hearthpulse-logo.webp; do not edit by hand. -->\n")


def dictionary(avalonia, layers=None):
    ns = 'https://github.com/avaloniaui' if avalonia else 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'
    body = '\n'.join('                ' + line for line in group(avalonia, layers).split('\n'))
    return (HEAD + f'<ResourceDictionary xmlns="{ns}"\n                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">\n'
            '    <DrawingImage x:Key="HearthpulseLogo">\n        <DrawingImage.Drawing>\n            <DrawingGroup>\n'
            + body + '\n            </DrawingGroup>\n        </DrawingImage.Drawing>\n    </DrawingImage>\n</ResourceDictionary>\n')


def write(path, text):
    (ROOT / path).write_bytes(text.replace('\n', '\r\n').encode('utf-8'))


# Icons of 16 to 32 pixels cannot resolve three spikes and a bevel: one rise and one fall, a thicker pulse and rim,
# the cup and the fill as in the full logo.
SMALL = [(-4, P['base']), (41, P['base']), (51, 60), (61, 114), (70, P['base']), (132, P['base'])]
small_pulse = f'M{fp(SMALL[0])} ' + ' '.join(f'L{fp(p)}' for p in SMALL[1:])
small_cup = (f'M{fp(rev[-1])} ' + ' '.join(f'L{fp(p)}' for p in SMALL[1:5]) + f' L{fp(rev[0])} ' + rev_d.split(' ', 1)[1] + ' Z')
SMALL_LAYERS = [
    ('fill', S_d, 'fill', None),
    ('stroke', S_d, 'rim', 14),
    ('fill', small_cup, 'cup', None),
    ('stroke', small_pulse, 'white', 13),
]

write('tools/logo/hearthpulse-logo.svg', svg())
write('tools/logo/hearthpulse-logo-small.xaml', dictionary(False, SMALL_LAYERS))
write('src/Hearthpulse.App/Main/Logo.xaml', dictionary(False))
write('src/Hearthpulse.UI/Main/Logo.axaml', dictionary(True))
