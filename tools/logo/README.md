# Logo

The Hearthpulse logo (flame with a pulse) belongs to the owner; it is the site's `public/hearthpulse-logo.webp`
(128×139). The app uses a vector copy of it.

| File | What |
|---|---|
| `vectorize.py` | Builds the vector from the webp: `hearthpulse-logo.svg`, `src/Hearthpulse.App/Main/Logo.xaml`, `src/Hearthpulse.UI/Main/Logo.axaml` (the `HearthpulseLogo` `DrawingImage`) and `hearthpulse-logo-small.xaml` (the simplified mark for 16–32 px icons). |
| `make_icons.py` | Renders every raster from the vector: `.ico` (16–256, both UIs and the installer), `.icns`, `installer/assets/icons/*.png`, the NSIS wizard bitmaps. |
| `render.cs` | The WPF renderer both scripts use (`dotnet run` file). |

```bash
python -I tools/logo/vectorize.py ../reference/repos/HeartPulse/public/hearthpulse-logo.webp
python -I tools/logo/make_icons.py
```

Windows only (WPF renders), with the .NET SDK and Pillow. The vector is constructed, not traced pixel by pixel: the
silhouette and the cup's walls are traced and smoothed, the rim, bevel, ring and pulse are strokes clipped to the
silhouette, each colour a vertical gradient fitted to the source. At 128 it differs from the source by about 6 of 255
per channel on average (edges of the pulse, anti-aliasing). The logo keeps its colours in every theme.
