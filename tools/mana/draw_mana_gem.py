"""Draws the mana cost gem shipped as Assets/mana-gem.png in both UIs.

The gem is original artwork drawn from scratch here: a regular pointy-top hexagon cut into six bevel facets around a
flat table, lit from the top left, in shades of the ManaGem token. It is not traced from, sampled from or derived from
Hearthstone's mana crystal or any Blizzard/HDT image (legal review: reference/reviews/LEGAL-HeartLens-claude-mana-assets.md).
The cost digit is drawn over it by the ManaCost control, not baked in.

Run:  python tools/mana/draw_mana_gem.py
"""

import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

SIZE = 128  # shipped size; badges are 9-25 px, so this covers 200% DPI with room to spare
SCALE = 4  # supersampling factor
S = SIZE * SCALE
U = S / 24.0  # drawing units: a 24 x 24 box

RIM = (11, 26, 61, 255)
FACETS = [  # clockwise from the top-right facet, lit from the top left; the right facet is the ManaGem token #3D7BE0
    (121, 174, 255),
    (61, 123, 224),
    (29, 77, 166),
    (39, 95, 192),
    (85, 146, 240),
    (181, 214, 255),
]
TABLE_TOP = (111, 166, 255)
TABLE_BOTTOM = (43, 99, 204)


def hexagon(radius, cx=12.0, cy=12.0):
    return [(cx + radius * math.cos(math.radians(-90 + 60 * k)), cy + radius * math.sin(math.radians(-90 + 60 * k)))
            for k in range(6)]


def px(points):
    return [(x * U, y * U) for x, y in points]


def vertical_gradient(top, bottom):
    gradient = Image.new("RGBA", (S, S))
    draw = ImageDraw.Draw(gradient)
    for y in range(S):
        t = y / (S - 1)
        draw.line([(0, y), (S, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)) + (255,))
    return gradient


def main():
    image = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    outer = hexagon(10.9)
    inner = hexagon(6.0, cy=11.6)
    draw.polygon(px(hexagon(11.9)), fill=RIM)
    for k, colour in enumerate(FACETS):
        quad = [outer[k], outer[(k + 1) % 6], inner[(k + 1) % 6], inner[k]]
        draw.polygon(px(quad), fill=colour + (255,))

    # Facet ridges: a faint light line from each outer corner to the table
    ridges = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ridge_draw = ImageDraw.Draw(ridges)
    for o, i in zip(outer, inner):
        ridge_draw.line(px([o, i]), fill=(255, 255, 255, 70), width=round(0.35 * U))
    image = Image.alpha_composite(image, ridges)

    # The table: a flat face with its own top-to-bottom light
    table_mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(table_mask).polygon(px(inner), fill=255)
    image.paste(vertical_gradient(TABLE_TOP, TABLE_BOTTOM), (0, 0), table_mask)
    ImageDraw.Draw(image).line(px(inner + inner[:1]), fill=(225, 238, 255, 150), width=round(0.3 * U))

    # Glint: a soft white spot on the upper-left facet and a sharp core inside it
    glint = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    glint_draw = ImageDraw.Draw(glint)
    glint_draw.ellipse(px([(5.0, 4.6), (9.4, 8.0)]), fill=(255, 255, 255, 150))
    glint = glint.filter(ImageFilter.GaussianBlur(0.9 * U))
    ImageDraw.Draw(glint).ellipse(px([(6.4, 5.5), (8.0, 6.7)]), fill=(255, 255, 255, 235))
    image = Image.alpha_composite(image, glint)

    # Keep everything inside the rim
    rim_mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(rim_mask).polygon(px(hexagon(11.9)), fill=255)
    image.putalpha(Image.composite(image.getchannel("A"), Image.new("L", (S, S), 0), rim_mask))

    image = image.resize((SIZE, SIZE), Image.LANCZOS)
    root = Path(__file__).resolve().parents[2]
    for project in ("Hearthpulse.App", "Hearthpulse.UI"):
        image.save(root / "src" / project / "Assets" / "mana-gem.png", optimize=True)


if __name__ == "__main__":
    main()
