"""Cuts Hearthstone's mana gem out of two HearthstoneJSON card renders into Assets/mana-gem.png of both UIs.

The gem is Blizzard artwork. Shipping it is the owner's decision of 2026-10-27, taken against the legal review
(reference/reviews/LEGAL-HeartLens-claude-mana-assets.md). The game's own files hold no flat gem picture: on a card
it is a lit 3D mesh, so the gem is taken from the official renders instead.

How: Wisp costs 0. In its render the gem is the blue region around the gem's centre (the frame behind it is brown,
the outside transparent); the convex hull of that region is the gem's outline. The "0" (white with a black edge) is
painted out with OpenCV inpainting - the ManaCost control draws the cost on top.

Run (needs Pillow, numpy, opencv-python-headless; downloads the render into the given folder):
    python tools/mana/extract_mana_gem.py <download-folder>
"""

import sys
import urllib.request
from pathlib import Path

import cv2
import numpy as np
from PIL import Image

RENDER = "https://art.hearthstonejson.com/v1/render/latest/enUS/512x/{}.png"
CARD = "CS2_231"  # Wisp, cost 0
BOX = (20, 70, 145, 195)  # the gem's corner of a 512 x 776 render, with some margin
CENTRE = (62, 63)  # a point inside the gem, in BOX coordinates
SIZE = 128
SCALE = 4  # supersampling for the outline


def load(folder, card):
    path = folder / f"{card}.png"
    if not path.exists():
        urllib.request.urlretrieve(RENDER.format(card), path)
    return np.asarray(Image.open(path).convert("RGBA").crop(BOX)).astype(np.int16)


def main():
    folder = Path(sys.argv[1])
    folder.mkdir(parents=True, exist_ok=True)
    a = load(folder, CARD)

    # The gem: the blue region (or the white digit) around the gem's centre, opaque; its convex hull is the outline
    r, g, bl = a[..., 0], a[..., 1], a[..., 2]
    blue = ((bl > r + 25) | (a[..., :3].min(axis=2) > 200)) & (a[..., 3] > 250)
    blue = cv2.morphologyEx(blue.astype(np.uint8), cv2.MORPH_CLOSE, np.ones((3, 3), np.uint8))
    blue = cv2.morphologyEx(blue, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
    _, labels = cv2.connectedComponents(blue)
    near = np.zeros(blue.shape, np.uint8)
    cv2.circle(near, CENTRE, 45, 1, -1)
    votes = np.bincount(labels[(near > 0) & (blue > 0)])
    gem = labels == int(np.argmax(votes))
    hull = cv2.convexHull(np.argwhere(gem)[:, ::-1].astype(np.int32))
    inside = np.zeros(gem.shape, np.uint8)
    cv2.fillPoly(inside, [hull.reshape(-1, 2)], 1)
    gem = inside.astype(bool)

    rgb = np.ascontiguousarray(a[..., :3].astype(np.uint8))

    # The digit: near-white pixels inside the gem and the black edge around them
    white = (rgb.min(axis=2) > 200) & gem
    digit = cv2.dilate(white.astype(np.uint8), np.ones((3, 3), np.uint8), iterations=3)
    dark = (rgb.max(axis=2) < 60).astype(np.uint8)
    digit = cv2.dilate(digit | (dark & cv2.dilate(digit, np.ones((5, 5), np.uint8), iterations=2)), np.ones((3, 3), np.uint8))
    bgr = cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR)
    clean = cv2.cvtColor(cv2.inpaint(bgr, digit, 6, cv2.INPAINT_TELEA), cv2.COLOR_BGR2RGB)

    # Square crop around the hull; alpha from the hull drawn at 4x and scaled down for a smooth edge
    x, y, w, h = cv2.boundingRect(hull)
    side = max(w, h) + 2
    left, top = x + (w - side) // 2, y + (h - side) // 2
    mask = np.zeros((side * SCALE, side * SCALE), np.uint8)
    cv2.fillPoly(mask, [((hull.reshape(-1, 2) - (left, top)) * SCALE + SCALE // 2).astype(np.int32)], 255)
    alpha = Image.fromarray(mask).resize((side, side), Image.LANCZOS)
    pad = np.pad(clean, ((side, side), (side, side), (0, 0)), mode="edge")
    tile = Image.fromarray(pad[top + side:top + 2 * side, left + side:left + 2 * side])
    tile.putalpha(alpha)
    tile = tile.resize((SIZE, SIZE), Image.LANCZOS)

    root = Path(__file__).resolve().parents[2]
    for project in ("Hearthpulse.App", "Hearthpulse.UI"):
        tile.save(root / "src" / project / "Assets" / "mana-gem.png", optimize=True)
    print("gem", (x, y, w, h), "digit px", int(digit.sum()))


if __name__ == "__main__":
    main()
