# Hearthpulse design tokens (reference)

Source of truth: `src/Hearthpulse.App/Themes/Theme.xaml` (WPF) and `src/Hearthpulse.UI/Themes/Theme.axaml`
(Avalonia). Values below are the built-in **Tavern** palette. Under another theme the WPF app recolours every hex in
Theme.xaml through `ThemePalette.Map` (see SKILL.md), so never copy these hex values into a view - reference the key.

## Base colours (`<Color x:Key="...Color">`, identical in both files)

| Key | Hex | Brush key | Use |
|---|---|---|---|
| BgColor | #140D08 | `Bg` | window background, ink |
| SurfaceColor | #21160E | `Surface` | lowest panel |
| Surface2Color | #2B1D12 | `Surface2` | Avalonia `card` fill |
| Surface3Color | #3A2817 | `Surface3` | raised plate, live bar, Avalonia segmentBar |
| Surface4Color | #4A331E | `Surface4` | pills, chips, icon-button fill |
| BorderColor | #3D2A19 | `Border`, `PanelEdge` | hairlines, chart grid |
| OutlineColor | #6E4E2C | `Outline` | stronger edge (live bar) |
| TextColor | #F4E7C9 | `Text` | body text (parchment) |
| MutedColor | #CDB894 | `Muted`, `TierC` | secondary text, icons, labels |
| FaintColor | #8F7A5C | `Faint` | tertiary text, disabled |
| AccentColor | #E9B949 | `Accent`, `GoldFill` | gold accent |
| AccentDeepColor | #C48E2C | `AccentDeep` | darker gold |
| AccentContainerColor | #5A3D17 | `AccentContainer` | icon cookie fill behind gold glyphs |
| SecondaryContainerColor | #3F2C19 | `SecondaryContainer` | neutral container |
| PositiveColor | #8CCF63 | `Positive` | wins, gains (fixed) |
| NegativeColor | #EC6A52 | `Negative`, `TierD` | losses, errors (fixed) |
| InfoColor | #6FB8F2 | `Info` | neutral information (fixed) |

## Other brushes (both files unless noted)

| Key | Value | Use |
|---|---|---|
| AccentSoft | #1FE9B949 | faint gold wash |
| AccentLine | #99E9B949 | gold line |
| OnAccent | #2A1906 | text on gold (PrimaryButton, chart tag) |
| OnAccentContainer | #FBE3A6 | text on AccentContainer |
| OnSecondaryContainer | #EBDDBD | ghost-button text |
| PositiveSoft / NegativeSoft | #228CCF63 / #24EC6A52 | tinted backgrounds (fixed) |
| Hover / Pressed | #12F4E7C9 / #1FF4E7C9 | state layers |
| Decor / DecorStrong | #0EE9B949 / #16E9B949 | decorative shapes |
| OverlayPanel | #F0201510 | overlay panel fill |
| OverlayPanelSolid | WPF: gradient #FA2C1F14→#FA1C130C; Avalonia: solid #FA201510 | opaque overlay panel |
| OverlayBorder | #40E9B949 | overlay panel edge |
| OverlayBadgeShade | #CC140D08 | shade behind overlay badges (both files) |
| OverlayPlaceholder | #E6201510 | layout-mode placeholder (WPF only) |
| Row | #2E1F13 | list row plate |
| TileFade | gradient from Row | card-tile art fade |
| Ink | = BgColor | Avalonia only |
| GoldTrim | #8A6533 | ornament/frame trim, Wave default stroke |
| SelectedEdge | WPF #A27839, Avalonia #8A6533 | selected plate edge |
| SelectedFill, PrimaryFill, PrimaryFillHover | gradients | selected plate, gold button |
| ManaGem | #3D7BE0 | flat mana blue (fixed); cost badges draw the `ManaCost` control (`Assets/mana-gem.png`), not this brush |
| RarityCommon/Rare/Epic/Legendary | #D8CDB8 / #5B9BFF / #B98AF5 / #FFB547 | rarity (fixed) |
| TierS/A/B/C/D | #FFC857 / #8CCF63 / #7CC0FF / Muted / Negative | tier letters (fixed) |

WPF-only "Gilded Tavern" gradients (Theme.xaml lines ~85-157): `GildedText` (Display headings), `StatText` (Stat
numbers), `WindowFill`, `ContentFill`, `ContentEdge`, `CardFill`, `CardEdge`, `GhostFill`, `GhostEdge`,
`PrimaryEdge`, `BestEdge`, `BestHalo` (gold glow behind the recommended option, a gradient not a bitmap effect),
`Sheen`, `GrooveFill`, `GrooveEdge` (SegmentBar), `NavSelectedFill`, `NavSelectedEdge`, `OverlayCardFill`,
`OverlayEdge`. Legacy aliases kept so older templates resolve: `PanelFill`, `OverlayFill`, `SidebarFill`, `WindowBg`.

Avalonia-only: Fluent overrides `SystemAccentColor*`, `ScrollBar*`, `Slider*` so built-in Fluent controls use the
same gold/wood palette.

## Fonts

| Key | WPF | Avalonia |
|---|---|---|
| UiFont | `Segoe UI Variable Text, Segoe UI` | `fonts:Inter#Inter`; replaced on Windows at startup with `Segoe UI Variable Text, Segoe UI` (`HearthpulseApp.axaml.cs`) |
| DisplayFont | `pack://application:,,,/Assets/Fonts/#Montserrat` | `avares://Hearthpulse.UI/Assets/Fonts#Montserrat` |
| IconFont | `pack://application:,,,/Assets/Fonts/#Material Design Icons` | `avares://Hearthpulse.UI/Assets/Fonts#Material Design Icons` |

Font files (both projects, `Assets/Fonts/`): `Montserrat-Bold.ttf`, `Montserrat-ExtraBold.ttf`,
`MaterialDesignIcons.ttf`. Icons are MDI code points written as XML entities, e.g. `&#xF02DC;` (Home nav),
`&#xF0787;` (Battlegrounds), `&#xF063A;` (Constructed), `&#xF02DA;` (History), `&#xF08BB;` (Settings),
`&#xF012A;` (Rating ornament), `&#xF01BE;` (arrange overlay), `&#xF0EB7;` (live bar).

## Type ramp

| WPF style (`TextBlock`) | Avalonia class | Font | Size | Weight | Colour |
|---|---|---|---|---|---|
| `Base` | (inherited from view: UiFont 13) | UiFont | 13 | Normal | Text |
| `Display` | `display` | DisplayFont | WPF 36 / Avalonia 34 | ExtraBold | WPF `GildedText` |
| `H1` | - | DisplayFont | 30 | ExtraBold | Text |
| `H2` | `h2` | DisplayFont | 16 | Bold | Text |
| `Label` | `label` | UiFont | 12 | SemiBold | Muted |
| `Caption` | `caption` | UiFont | 12.5 | Normal | Muted |
| `Stat` | `stat` | DisplayFont | 28 | Bold | WPF `StatText` |
| - | `number` | DisplayFont | 15 | SemiBold | - |
| `Icon` | `icon` | IconFont | 18 | Normal | Muted |

WPF windows set base type through the `AppWindow` style (UiFont 13, Text, tabular numerals) rather than an implicit
TextBlock style, so icon glyph TextBlocks inside templates are not restyled.

## Surfaces, buttons, inputs

| WPF key | Avalonia selector | Shape |
|---|---|---|
| `Card` (Border) | `Border.card` | r12, padding 22,18, 1px edge. WPF fill `CardFill`/`CardEdge`; Avalonia `Surface2`/`PanelEdge` |
| `OverlayCard` | `Border.overlayCard` | r8, 1px edge |
| `Pill` | `Border.pill` | Surface4, r6, padding 9,2 |
| `SegmentBar` | `Border.segmentBar` | r8, padding 4; WPF Groove fill/edge, Avalonia Surface3 |
| `GhostButton` | `Button.ghost` | padding 18,9, SemiBold 13, template r8, OnSecondaryContainer text |
| `PrimaryButton` (BasedOn GhostButton) | `Button.ghost.primary` (use both classes) | PrimaryFill gold, OnAccent text, Bold, top sheen |
| `IconButton` | `Button.icon` | 32x32 circle, IconFont 17, Muted |
| `CaptionButton`, `CloseCaptionButton` | - | window controls (WPF only) |
| - | `Button.bare` | unstyled button (Avalonia) |
| `NavItem` (RadioButton) | `RadioButton.nav` | height 46, SemiBold 14, glyph in `Tag`; WPF selected look is the sliding `NavPill` in MainWindow |
| `Segment` (RadioButton) | `RadioButton.segment` | chip inside a SegmentBar, 12.5 SemiBold |
| `Tab` (BasedOn Segment, 2px gold underline) | - (Avalonia tabs use `segment`) | page tabs |
| `Switch` (CheckBox) | `CheckBox.switch` | rounded track, gold when on |
| `FocusRing*` | Fluent focus | thin gold outline per shape (Chip, Nav, Row, Round, Switch) |
| implicit `Slider`, `ScrollBar`, `TextBox`, `ToolTip` | `TextBox`, `ToolTip`, Fluent slider/scrollbar overrides | |
| - | `Border.row`, `ListBox.plain`, `ListBox.selectable` | Avalonia list rows |

View-local styles: WPF `MainWindow.xaml` resources `TableHead`, `RowBorder`, `TierBadge`, `NavGroup`, `PageSubtitle`,
`StatTileTemplate`, `GameRowTemplate`, `ThemeTile`, `ThemeSwatch`, `HueStrip`, `TilesPanel`; Avalonia
`MainView.axaml` selectors `TextBlock.tableHead`, `Border.tierBadge`, `Grid.navGroup`, `StackPanel.pageSubtitle`,
`ScrollViewer.page`, `Border.placeBar(.top/.first)`, `Grid.choiceRow`, plus `m|MainView.compact ...` rules.
