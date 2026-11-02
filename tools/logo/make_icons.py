"""Renders every raster of the Hearthpulse logo from its vector (src/Hearthpulse.App/Main/Logo.xaml).

Writes the window/exe icon (.ico, both UIs and the installer), the macOS .icns, the Linux PNGs and the NSIS wizard
bitmaps. Needs Windows (the renderer is WPF), the .NET SDK and Pillow. Run from the repository root:
  python -I tools/logo/make_icons.py
"""
import pathlib, shutil, struct, subprocess, tempfile
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parents[2]
LOGO = ROOT / 'src/Hearthpulse.App/Main/Logo.xaml'
SMALL_LOGO = ROOT / 'tools/logo/hearthpulse-logo-small.xaml'  # the simplified mark for 16, 24 and 32
SMALL = (16, 24, 32)
ASSETS = ROOT / 'installer/assets'
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]
PNG_SIZES = [16, 24, 32, 48, 64, 128, 256, 512]
# icns entries (type, pixels), as Apple names them: ic11/ic12/ic13/ic14 are the @2x of 16/32/128/256
ICNS = [('ic07', 128), ('ic08', 256), ('ic09', 512), ('ic10', 1024), ('ic11', 32), ('ic12', 64), ('ic13', 256), ('ic14', 512)]


def render(logo, jobs, tmp):
    """jobs: name -> (width, height, logo height, centre x, centre y); returns name -> PNG path."""
    args = [f'{tmp / name}:{w}:{h}:{lh}:{cx}:{cy}' for name, (w, h, lh, cx, cy) in jobs.items()]
    subprocess.run(['dotnet', 'run', str(ROOT / 'tools/logo/render.cs'), '--', str(logo), *args], check=True)
    return {name: tmp / name for name in jobs}


def png_bytes(path):
    return path.read_bytes()


def write_ico(path, frames):
    head = struct.pack('<HHH', 0, 1, len(frames)); entries = b''; data = b''; offset = 6 + 16 * len(frames)
    for size, blob in frames:
        entries += struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(blob), offset + len(data))
        data += blob
    path.write_bytes(head + entries + data)


def write_icns(path, entries):
    toc = b'TOC ' + struct.pack('>I', 8 + 8 * len(entries)) + b''.join(t.encode() + struct.pack('>I', 8 + len(b)) for t, b in entries)
    body = toc + b''.join(t.encode() + struct.pack('>I', 8 + len(b)) + b for t, b in entries)
    path.write_bytes(b'icns' + struct.pack('>I', 8 + len(body)) + body)


def main():
    tmp = pathlib.Path(tempfile.mkdtemp())
    try:
        sizes = sorted(set(ICO_SIZES + PNG_SIZES + [s for _, s in ICNS]))
        jobs = {f'icon-{s}.png': (s, s, s, s / 2, s / 2) for s in sizes if s not in SMALL}
        # NSIS wizard: the side panel (164x314, logo 128 high, centred at 82,125) and the header (150x57, logo at the right)
        jobs['side.png'] = (164, 314, 128, 82, 125)
        jobs['header.png'] = (150, 57, 49, 121.5, 28.5)
        out = render(LOGO, jobs, tmp)
        out.update(render(SMALL_LOGO, {f'icon-{s}.png': (s, s, s, s / 2, s / 2) for s in SMALL}, tmp))

        ico = [(s, png_bytes(out[f'icon-{s}.png'])) for s in ICO_SIZES]
        for target in [ASSETS / 'hearthpulse.ico', ROOT / 'src/Hearthpulse.App/Assets/hearthpulse.ico',
                       ROOT / 'src/Hearthpulse.UI/Assets/hearthpulse.ico']:
            write_ico(target, ico)
        write_icns(ASSETS / 'hearthpulse.icns', [(t, png_bytes(out[f'icon-{s}.png'])) for t, s in ICNS])
        for s in PNG_SIZES:
            shutil.copyfile(out[f'icon-{s}.png'], ASSETS / f'icons/hearthpulse-{s}.png')
        for name, target, background in [('side.png', 'wizard-side.bmp', (10, 14, 11)), ('header.png', 'wizard-header.bmp', (255, 255, 255))]:
            logo = Image.open(out[name]).convert('RGBA')
            canvas = Image.new('RGBA', logo.size, background + (255,))
            canvas.alpha_composite(logo)
            canvas.convert('RGB').save(ASSETS / target)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == '__main__':
    main()
