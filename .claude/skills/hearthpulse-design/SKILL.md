---
name: hearthpulse-design
description: Use when changing Hearthpulse UI visuals - XAML/AXAML pages, overlay panels, colours, fonts, icons, styles, theme presets, custom controls - or adding a page/panel to the WPF app or Avalonia preview. Covers tokens, theme recolouring, parity checklist and --render-ui checks.
---

# Hearthpulse visual design

Look: Hearthstone menus - dark wood and leather surfaces, parchment text, gold trim. Two UIs share the look:

| UI | Project | Theme file | Main view | Overlay |
|---|---|---|---|---|
| WPF (what the Windows installer ships) | `src/Hearthpulse.App` | `Themes/Theme.xaml` | `Main/MainWindow.xaml` | `Overlay/OverlayWindow.xaml` |
| Avalonia "preview" (cross-platform) | `src/Hearthpulse.UI` (+ host `src/Hearthpulse.Desktop`) | `Themes/Theme.axaml` | `Main/MainView.axaml` | `Overlay/OverlayView.axaml` |

Full token/style tables with hex values: [reference/tokens.md](reference/tokens.md).

## Rule 1: never hardcode colours in views - use tokens

WPF themes work by **rewriting hex literals inside Theme.xaml only**:

- `src/Hearthpulse.Core/ThemePalette.cs`: `ThemeSpec(Name, AccentHue, BaseHue, BaseTint)`. Presets: `Tavern`
  (default, accent hue 42, base hue 28, tint 1.0), `Frost`, `Fel`, `Arcane`, `Ember`, `Tide`, `Shadow`; anything
  else resolves to `Custom` (`ThemePalette.Resolve`). `Map(argb, theme)` returns the colour unchanged for the default
  theme, for low saturation (s < 0.08) and for hues outside 15-60 deg. Inside that range, saturated mid tones
  (chroma >= 0.3) are treated as gold accents and shifted to `AccentHue`; dull/dark/light tones are wood and
  parchment, shifted to `BaseHue` with saturation scaled by `BaseTint`.
- `src/Hearthpulse.App/Infrastructure/ThemeEngine.cs`: loads the embedded `Hearthpulse.Theme.xaml` (the csproj embeds
  `Themes\Theme.xaml` with that LogicalName), runs every `#RRGGBB`/`#AARRGGBB` through `Map` line by line, and parses
  the result. Lines whose `x:Key` starts with `Positive|Negative|Info|Rarity|Tier|ManaGem` are never recoloured.
  Windows resolve brushes when created, so the host (`App.xaml.cs` `ApplyTheme`) recreates windows after a change.
- Settings: `AppSettings.Theme` (default `"Tavern"`), `ThemeAccentHue`, `ThemeBaseHue`, `ThemeBaseTint`. Picker UI:
  `Main/MainViewModel.Theme.cs` (`ThemeChoice` previews use `ThemeEngine.Brush`).

Consequences:

- A hex literal in `MainWindow.xaml`, `OverlayWindow.xaml` or C# is **not** recoloured and breaks non-default themes.
  Use `{StaticResource Key}` (WPF) / `{StaticResource Key}` or `ThemeBrushes.Get("Key")` (Avalonia,
  `Infrastructure/Converters.cs`); in WPF code `FindResource("Key")` / `TryFindResource`. Existing exceptions are
  deliberate: the hue-slider rainbow (`HueStrip` in MainWindow.xaml) and transparent stops.
- New colour token: add it to **both** theme files with the same key. If its colour carries meaning (win/loss, tier,
  rarity) give it a key starting with one of the fixed prefixes, or it will turn purple/blue under Arcane/Frost.
- Gold-ish hue (15-60) means "will be themed"; pick non-gold hues only for semantic colours.
- Map tokens to meaning, not to looks: `Positive`/`Negative` for results (`SignBrush` converter), `Tier*` via
  `KeyBrush`, `Rarity*`, `ManaGem` for cost gems, `Accent` for the one thing that matters, `Muted`/`Faint` for
  secondary text.
- Avalonia has **no theme engine**: Theme.axaml is static Tavern and `ThemePalette` is not used in `Hearthpulse.UI`.
  Still use tokens so a future engine works.

Core tokens (details in reference): `Bg`, `Surface`, `Surface2`-`Surface4`, `Border`, `Outline`, `Text`, `Muted`,
`Faint`, `Accent`, `AccentDeep`, `AccentSoft`, `AccentLine`, `AccentContainer`, `OnAccent`, `OnAccentContainer`,
`SecondaryContainer`, `OnSecondaryContainer`, `Positive(Soft)`, `Negative(Soft)`, `Info`, `Hover`, `Pressed`, `Decor`,
`DecorStrong`, `Row`, `GoldTrim`, `OverlayPanel`, `OverlayBorder`, `SelectedFill`, `SelectedEdge`, `PrimaryFill`.

## Type and icons

- Fonts: `UiFont` (text), `DisplayFont` = Montserrat Bold/ExtraBold (headings, numbers), `IconFont` = Material Design
  Icons. Glyphs are written as entities `&#xF0xxx;` (e.g. `&#xF02DC;` home). Use the `Icon` style / `icon` class;
  do not add other icon fonts or image icons for UI chrome.
- Ramp (WPF style / Avalonia class): `Display` 36 / `display` 34 page titles; `H1` 30 (WPF only); `H2` / `h2` 16
  section titles; `Label` / `label` 12 SemiBold Muted; `Caption` / `caption` 12.5 Muted; `Stat` / `stat` 28 big
  numbers; `number` 15 (Avalonia); base text 13.
- Section headings use the `Ornament` control (gold icon in a scallop cookie + title + rule), e.g.
  `<c:Ornament Text="Rating" Icon="&#xF012A;" Size="13" />`. Mixed-case text is kept; ALL CAPS is sentence-cased.

## Styles and classes

| Purpose | WPF `Style="{StaticResource X}"` | Avalonia `Classes="x"` |
|---|---|---|
| Framed panel | `Card` (r12, padding 22,18) | `card` |
| Overlay panel | `OverlayCard` (r8) | `overlayCard` |
| Small tag | `Pill` (r6, 9,2) | `pill` |
| Secondary button | `GhostButton` | `ghost` |
| Primary (one per area) | `PrimaryButton` | `ghost primary` |
| Round icon button | `IconButton` (32x32) | `icon` |
| Sidebar item | `NavItem` (h46, glyph in `Tag`) | `nav` |
| Filter chips | `Segment` inside `SegmentBar` | `segment` inside `segmentBar` |
| Page tabs | `Tab` (Segment + 2px gold underline) | `segment` (no tab class) |
| Toggle | `Switch` (CheckBox) | `switch` |
| List row | `RowBorder` (MainWindow) | `row` |

Buttons are rounded rectangles (r8), never stretched ovals; icon buttons stay circles.

## Custom controls

`Controls/` in both UIs (namespace prefix `c:`):

- `Scallop` - Material 3 "cookie" (`Fill`, `Lobes` 9 cookie/4 clover/12 sunny, `Depth`, `Angle`), behind icons and as
  decoration.
- `Ornament` - section heading (`Text`, `Size`, `Centered`, `Icon`).
- `Wave` - gold rule: diamond + fading hairline (`Stroke` defaults to `GoldTrim`; Amplitude/Wavelength/Thickness are
  kept only for compatibility).
- `RatingChart` - rating curve (`Points`), min/max guides, latest value tag.
- `RoundClip` (WPF only, attached `RoundClip.Radius`) - clips art to rounded corners; WPF `ClipToBounds` ignores
  CornerRadius.

## Page layout

- WPF pages: `ScrollViewer x:Name="Page<Name>"` > `StackPanel Margin="40,0,40,120"`; History is a `Grid` with
  `Margin="40,0,40,110"`. Avalonia: `ScrollViewer Classes="page"` (padding `40,0,40,120`; compact `16,0,16,120`).
- Bottom 120 leaves room for the floating live bar ("now playing" bar at the bottom of the main window).
- Settings content: `MaxWidth="780"` `HorizontalAlignment="Left"`.
- Header: `Display` title, then `PageSubtitle` / `pageSubtitle` (horizontal, margin `2,6,0,24`) with a Caption at
  FontSize 14 (Avalonia adds a 26px Scallop + glyph). Filters (SegmentBar) sit top-right of the header grid.
- Card spacing: `Margin="0,0,0,24"` between cards; Ornament section headings above groups.
- Avalonia compact mode: `MainView` gets class `compact` below 900 px (`CompactWidth` in `MainView.axaml.cs`):
  icon-only nav, `.wide` elements hidden, choice rows stack.
- Overlay: laid out on a 1080-unit-high reference surface and scaled to the game client (`ReferenceHeight = 1080` in
  `OverlayWindow.xaml.cs` and `OverlayView.axaml.cs`).

## Motion

WPF only animates when Windows' animation setting is on: `MainWindow.xaml.cs` checks
`SystemParameters.ClientAreaAnimation` before the nav pill slide (340 ms, BackEase) and page reveal (280 ms rise +
220 ms fade, CubicEase). Hover layers fade in 0.12 s / out 0.22 s. Any new animation must take the same guard. The
Avalonia UI currently has no animations.

## Checklist: adding a page, panel or setting

1. Logic and data in `src/Hearthpulse.Core`; both view models (`Hearthpulse.App/Main/MainViewModel*.cs`,
   `Hearthpulse.UI/Main/MainViewModel.cs`) expose it. Settings go in `AppSettings`.
2. WPF: add markup to `MainWindow.xaml` (or `OverlayWindow.xaml`) using styles above; new nav pages need a `NavItem`
   RadioButton with an MDI glyph in `Tag` and handling in `MainWindow.xaml.cs` (`ShowPage`).
3. Avalonia: mirror it in `MainView.axaml` / `OverlayView.axaml` with the matching classes; check compact mode.
4. No hex literals outside the theme files; new tokens added to both theme files.
5. Any animation guarded by `SystemParameters.ClientAreaAnimation` (WPF).
6. If the page should be in render checks, add it to the page list in `DevRender.cs` / `OffscreenRenderer.cs`.
7. Render both UIs (below) and compare the PNGs; for WPF also render at least one non-default theme.

## Visual verification: `--render-ui <dir>`

Both apps render pages off-screen to PNG from the player's own history and latest kept Battlegrounds log, then exit.

- WPF (`App.xaml.cs` reads `--render-ui`, `DevRender.cs` renders; Windows only, single-instance check skipped):
  `dotnet run --project src/Hearthpulse.App -- --render-ui <dir>`. Writes `main-overview|battlegrounds|constructed|
  history|settings.png`, `main-constructed-tiers.png`, `main-constructed-top.png`, `overlay-pick|trinket|discover|
  game.png` (each only if a kept log reaches that moment) and `overlay-layout.png`.
  Theme preview without touching settings: set `HEARTHPULSE_THEME=<preset name>` (only honoured while rendering).
- Avalonia (`Program.cs` -> `OffscreenRenderer.Run`, headless Skia):
  `dotnet run --project src/Hearthpulse.Desktop -- --render-ui <dir>`. Writes the five `main-*.png` at 1260x840,
  `main-compact.png` at 420x860, and the same overlay moments.
- Then view the PNGs (Read tool) and check: spacing, truncation, contrast, token colours, parity between the two UIs.
- Rendering uses the real data folder (`AppPaths`); set `HEARTHPULSE_DATA` to a scratch copy to avoid touching it.

## Known UI gaps (as of this skill's writing)

- Avalonia lacks WPF features: Constructed meta tabs (`TabMyDecks`/`TabMetaTiers`/`TabMetaDecks` exist only in
  `MainWindow.xaml`), the theme picker (`Themes`, `SelectTheme`, custom hue sliders), and the overlay tribe focus
  (`CompFocus`, wired only in `Hearthpulse.App`).
- `RatingChart` empty state draws one unwrapped line ("Play a few rated Battlegrounds games...") centred at
  `(w - width)/2`; in narrow hosts it overflows on both sides (both UIs).
- Compact live bar (Avalonia, `MainView.axaml` "Floating live bar"): title/detail are squeezed by the arrange button and
  Overlay switch and truncate with ellipsis at phone width (`main-compact.png`).
