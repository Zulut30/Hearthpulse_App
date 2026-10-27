---
name: hearthpulse-design
description: Use when changing how Hearthpulse looks - colours and design tokens, theme presets (Tavern/Frost/Fel/Arcane/Ember/Tide/Shadow), fonts, MDI icons, styles and classes, custom controls (Scallop, Ornament, Wave, charts, CardPreview), page layout rules, motion, or any XAML/AXAML styling in the WPF app or Avalonia preview. Also the reference for "no hex in views" and the list of accepted WPF/Avalonia gaps. For the step-by-step recipes use hearthpulse-overlay-panel, hearthpulse-main-page and hearthpulse-visual-check.
---

# Hearthpulse visual design

Look: Hearthstone menus - dark wood and leather surfaces, parchment text, gold trim. Two UIs share the look:

| UI | Project | Theme file | Main view | Overlay |
|---|---|---|---|---|
| WPF (what the Windows installer ships) | `src/Hearthpulse.App` | `Themes/Theme.xaml` | `Main/MainWindow.xaml` | `Overlay/OverlayWindow.xaml` |
| Avalonia "preview" (cross-platform) | `src/Hearthpulse.UI` (+ host `src/Hearthpulse.Desktop`) | `Themes/Theme.axaml` | `Main/MainView.axaml` | `Overlay/OverlayView.axaml` |

Full token/style tables with hex values: [reference/tokens.md](reference/tokens.md). Recipes:
[hearthpulse-overlay-panel](../hearthpulse-overlay-panel/SKILL.md), [hearthpulse-main-page](../hearthpulse-main-page/SKILL.md);
PNG checks: [hearthpulse-visual-check](../hearthpulse-visual-check/SKILL.md).

UI text is localized in English and Russian: keys live in `src/Hearthpulse.Core/Localization/Strings/{en,ru}/*.json`,
used through `{i:Loc Key}` in XAML and `Loc.T/F/P` in code (`docs/localization.md`). Use sentence case, not hardcoded
English. Card, hero and deck names follow the card locale, so layouts must survive long Cyrillic names and live
language switching.

Custom Avalonia button templates must forward `ContentTemplate` and content alignment to their `ContentPresenter`.
Without this, a `Button.bare` history row renders the record's `ToString()` instead of the game-row template.
Regression: `LocBindingTests.Bare_history_button_renders_the_game_row_template_instead_of_record_text`.

Display state/records are maintained in `src/Hearthpulse.Presentation`, compiled into both original UI assemblies.
Views, controls, resources and framework-specific values stay local to App/UI. Change common labels/row selection
once in the shared source and visual bindings in both views; do not recreate duplicate VM implementations. WPF
theme/focus and Avalonia compact differences remain intentional. See `src/Hearthpulse.Presentation/README.md`.

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
  the result. Lines whose `x:Key` starts with `Positive|Negative|Info|Rarity|Tier|ManaGem|PoolStat` are never recoloured.
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
  `KeyBrush`, `Rarity*`, the `ManaCost` control for cost gems, `Accent` for the one thing that matters, `Muted`/`Faint` for
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
- `ManaCost` - every card cost badge: the mana gem picture `Assets/mana-gem.png` (`Resource` in App,
  `AvaloniaResource` in UI) with `Text` as a white digit outlined in dark; empty `Text` = the gem alone (Match page
  mana pill). Set only `Width`/`Height` (gem fills the smaller side, digit scales with it); no Border, CornerRadius or
  FontSize around it. Overlay deck rows style it with `CostChip` (WPF) / `c|ManaCost.cost` (Avalonia). The chart axis
  tokens in `CardPoolPanel` (cost/attack/health in the stat's colour) are chart markers, not cost badges, and stay
  `Path`s. The PNG is original artwork drawn by `tools/mana/draw_mana_gem.py` (edit the script and re-run it; it
  writes both copies) - never Blizzard's mana crystal: game files (EULA), HearthstoneJSON renders (All Rights Reserved)
  and HDT `mana.png` (a game texture) are all Blockers (`reference/reviews/LEGAL-HeartLens-claude-mana-assets.md`).
  Keep it recognisably our own: regular hexagon cut, `ManaGem`-token blues, Montserrat digit with a thin edge (no
  thick game-style outline, no Belwe, no game glows), no tracing over screenshots, not named "Hearthstone mana
  crystal", never behind the paywall.
- `Wave` - gold rule: diamond + fading hairline (`Stroke` defaults to `GoldTrim`; Amplitude/Wavelength/Thickness are
  kept only for compatibility).
- `RatingChart` - rating curve (`Points`), min/max guides, latest value tag.
- `StatsLineChart` - line of `IChartValue` points with a hover card (`HoverTemplate`, `HoverIndex`); `Sparkline` (You vs
  a dashed comparison), `MiniBars` (shares with comparison ticks, marker, 1..N labels). Used by the My Stats views in
  `Main/Stats/` (`BattlegroundsStatsView`, `ConstructedStatsView`, `ArenaStatsView` + `StatsResources`) of both UIs.
- `ResultBars` - wins/losses stacked per group with "W–L" and labels (deck page "Results by length",
  `Main/Decks/DeckDetailsView` of both UIs; WPF rows that click use the `RowButton` style, Avalonia `Button.bare` + `row`).
- `RoundClip` (WPF only, attached `RoundClip.Radius`) - clips art to rounded corners; WPF `ClipToBounds` ignores
  CornerRadius.
- `CardPreview` - attached `CardPreview.CardId` / `Caption` / `Placement` shows a full card render on hover through
  the window's `CardPreviewHost` (one preview per window, swapped in place). Inside the overlay the element also
  needs `OverlayHit.Interactive`.

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

## Adding a page, panel or setting

Follow the recipes, they carry the parity and render steps:
[hearthpulse-main-page](../hearthpulse-main-page/SKILL.md) (sidebar pages, tabs, settings cards),
[hearthpulse-overlay-panel](../hearthpulse-overlay-panel/SKILL.md) (overlay panels). Design-side rules for both:
no hex literals outside the theme files, new tokens in both theme files, animations behind
`SystemParameters.ClientAreaAnimation`, and a look at both UIs plus one non-default WPF theme with
[hearthpulse-visual-check](../hearthpulse-visual-check/SKILL.md) (`--render-ui` on a copy of the data folder,
`HEARTHPULSE_THEME=<preset>`).

## Known UI gaps (checked on main 2e8d9be, Oct 2026)

- WPF only: Constructed meta tabs (`TabMyDecks`/`TabMetaTiers`/`TabMetaDecks`, `MainViewModel.Constructed.cs`), the
  theme picker (`MainViewModel.Theme.cs`: `Themes`, `SelectTheme`, custom hue sliders), page reveal and nav pill
  animation, the faded hero banner, and the overlay tribe focus (`CompFocus`, `FocusChoices` in the WPF
  `OverlayViewModel`).
- Avalonia only: compact layout below 900 px, separate Hearthstone/memory status dots, an extra overlay switch in
  Settings. Avalonia has no theme engine (static Tavern).
- Compact hero/comp tier tables (Avalonia): fixed column widths leave no room for the name column at phone width.
