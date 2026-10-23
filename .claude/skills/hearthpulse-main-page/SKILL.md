---
name: hearthpulse-main-page
description: Use when adding or changing a page, tab, section or settings card in the Hearthpulse main window - WPF MainWindow.xaml(.cs) and Avalonia MainView.axaml(.cs) - including sidebar navigation (NavItem/ShowPage), MainViewModel.Feature.cs partials, My Stats views in Main/Stats, the account card, Constructed meta tabs, WPF/Avalonia parity, compact mode, and the page's render scene. Triggers - "новая страница/вкладка", "add a tab", "settings section", "My Stats", "History page", "Arena page", "main window".
---

# Adding or changing a main-window page or tab

Main window = sidebar (Home, Modes: Battlegrounds / Constructed / Arena, Library: History, Settings at the bottom) +
one visible page + the floating live bar at the bottom. All UI text is **English**, hardcoded in XAML (no resx):
"Your session, rating and recent games", "No games yet. Finished games appear here automatically.",
"Changes apply instantly.". The only Cyrillic is in data (Russian archetype names in `ArchetypeNames.cs`, the
attribution caption "... HSGuru / HSReplay данные").

Look and tokens: [hearthpulse-design](../hearthpulse-design/SKILL.md). PNG proof:
[hearthpulse-visual-check](../hearthpulse-visual-check/SKILL.md). Overlay panels are a different recipe:
[hearthpulse-overlay-panel](../hearthpulse-overlay-panel/SKILL.md).

## Files

| Role | WPF `src/Hearthpulse.App/Main/` | Avalonia `src/Hearthpulse.UI/Main/` |
|---|---|---|
| Sidebar + pages | `MainWindow.xaml` (Window 1260x840, min 1040x660) | `MainView.axaml` (UserControl; window in `src/Hearthpulse.Desktop/MainWindow.axaml`) |
| Navigation code | `MainWindow.xaml.cs`: `ShowPage`, `CurrentPage`, `OnNavigate`, `PlacePill`, `Reveal`, `ShowConstructedTab`, tab handlers `OnBgTab`/`OnCtTab` | `MainView.axaml.cs`: `ShowPage`, `UpdatePages`, `UpdateTabs`, `ApplyWidth` (compact), `ApplyChoice` |
| View model | `MainViewModel.cs` + `.Account` `.BgCounters` `.Counters` `.MyStats` `.Constructed` (WPF only) `.Theme` (WPF only) | `MainViewModel.cs` + `.Account` `.BgCounters` `.Counters` `.MyStats` |
| My Stats views | `Stats/BattlegroundsStatsView`, `ConstructedStatsView`, `ArenaStatsView`, `StatsResources.xaml` | same names, `.axaml` |
| Composition | `src/Hearthpulse.App/App.xaml.cs` (+ `App.CardPool.cs`) | `src/Hearthpulse.UI/AppHost.cs` (+ `AppHost.CardPool.cs`) |
| Logic | `src/Hearthpulse.Core` (e.g. `Stats/BattlegroundsStats.cs`, `ConstructedStats.cs`, `ArenaStats.cs`) | same |

Element names match between the UIs (`NavArena`/`PageArena`, `TabHeroes`...): WPF `x:Name`, Avalonia `Name`.

## Recipe: a new sidebar page "Foo"

Navigation is hand-written lists, not data binding - a new page must be added to **every** list.

**WPF** (`MainWindow.xaml` / `.xaml.cs`):

1. Sidebar item next to its group, an MDI glyph in `Tag`:
   `<RadioButton x:Name="NavFoo" Style="{StaticResource NavItem}" Content="Foo" Tag="&#xF0xxx;" GroupName="Nav" Checked="OnNavigate" ToolTip="..." />`
2. Page in the content grid (Row 1), collapsed:
   `<ScrollViewer x:Name="PageFoo" Grid.Row="1" VerticalScrollBarVisibility="Auto" Visibility="Collapsed">`
   -> `<StackPanel Margin="40,0,40,120">` -> `TextBlock Style="{StaticResource Display}"` title ->
   `StackPanel Style="{StaticResource PageSubtitle}"` caption -> `Border Style="{StaticResource Card}"` sections with
   `c:Ornament` headings, `Margin="0,0,0,24"` between cards. (History is the exception: a `Grid` with a virtualizing
   list.)
3. Code-behind: add `"Foo"` to the `ShowPage` switch and to `CurrentPage`; add `PageFoo.Visibility = ...` and
   `PageFoo` to the `Reveal` array in `OnNavigate`; add `NavFoo` to the `PlacePill` array. Unknown names fall back to
   Home silently - test `ShowPage("Foo")`.
4. Animations exist only behind `SystemParameters.ClientAreaAnimation` (already handled by `Reveal`/`PlacePill`).

**Avalonia** (`MainView.axaml` / `.axaml.cs`):

1. `<RadioButton Name="NavFoo" Classes="nav" Content="Foo" Tag="&#xF0xxx;" GroupName="Nav" ... />`.
2. `<ScrollViewer Name="PageFoo" Grid.Row="1" Classes="page" VerticalScrollBarVisibility="Auto" IsVisible="False">`
   (the `page` class gives the 40/120 padding, 16 in compact).
3. Code-behind: add `NavFoo` to the nav array in the `MainView()` constructor (it subscribes `IsCheckedChanged`), add
   `PageFoo.IsVisible = NavFoo.IsChecked == true;` to `UpdatePages`, add `"Foo"` to `ShowPage`.
4. Compact (< 900 px, class `compact` on `MainView`): nav shows icons only, `.wide` hides, `.narrow` shows,
   `UniformGrid` tiles drop to 2 columns unless `fixed`. Check that the page stacks instead of clipping
   (render `main-compact` style scenes).

**View model**: a partial `MainViewModel.Foo.cs` in **both** UIs (properties with `Set(ref ...)`, `RelayCommand`
for actions/filters, data loaded off the UI thread). Heavy work (history scans, chart series) goes to `Task.Run` with
a generation counter so a stale result never overwrites a newer one (my-stats review fix); errors go to `Diag`, not
swallowed in a fire-and-forget task. Computation itself belongs in Core so it can be tested.

## Recipe: a tab inside a page

- Tabs are `RadioButton`s with a shared `GroupName`: WPF `Style="{StaticResource Tab}"` + a handler
  (`OnBgTab`, `OnCtTab`) that toggles the cards; Avalonia `Classes="segment"` + `UpdateTabs()`.
- Existing: Battlegrounds `TabHeroes`/`TabComps`/`TabTrinkets`/`TabCards`/`TabMine` (both UIs);
  Constructed `TabMyDecks` "My stats" / `TabMetaTiers` "Meta tier list" / `TabMetaDecks` "Top decks" (**WPF only**,
  `ShowConstructedTab("Tiers"|"Top")`).
- Filters are segment bars bound to VM commands (`SetBgPeriod`, `SetCtFormat`, `SetHistoryFilter`...), placed top
  right of the page header.

## Recipe: a settings card

Settings page sections in order: "Hearthpulse account", "Theme" (WPF only), "In-game overlay", "Panel layout",
"Leaderboard hover", "Counters and mulligan", "App", "About". Content `MaxWidth="780"`, left aligned.

- New setting: property + default + `/// <summary>` in `src/Hearthpulse.Core/AppSettings.cs`; bind in both UIs
  (WPF `Style="{StaticResource Switch}"`, Avalonia `Classes="switch"`). Toggles are picked up automatically (WPF
  `ToggleButton.Checked/Unchecked="OnSettingChanged"` on the settings StackPanel; Avalonia hooks every CheckBox), and
  the host saves settings.
- Choice rows: WPF segment buttons bound to commands; Avalonia radios with `Tag="PickHints.X"` applied in
  `ApplyChoice`.
- Account card: `MainViewModel.Account.cs` (`Account*` properties, `SignIn`/`SignOut`; WPF `CopyAccountCode`,
  Avalonia `AccountCodeCopied`); states signed out / waiting for code / signed in / session ending soon. Contract:
  [hearthpulse-data-sources](../hearthpulse-data-sources/SKILL.md).

## Parity

Every user-facing page goes into both UIs. Known accepted gaps (check before "fixing" them, and do not add new
ones silently - list them in [hearthpulse-design](../hearthpulse-design/SKILL.md)):

- WPF only: Constructed meta tabs and filters (`MainViewModel.Constructed.cs`; Avalonia `AppHost.LoadMetaAsync`
  loads meta only for the mulligan), theme picker (`MainViewModel.Theme.cs`), page reveal/nav pill animation, hero
  banner, overlay tribe focus.
- Avalonia only: compact layout, separate Hearthstone/memory status dots, an extra overlay switch in Settings.

## Render scene

- Pages in the render loop: Overview, Battlegrounds, Constructed, History, Settings (`DevRender.cs`,
  `OffscreenRenderer.cs`); My Stats full/hover/compact in `DevRender.MyStats.cs` / `OffscreenRenderer.MyStats.cs`;
  WPF Constructed tabs `main-constructed-tiers|top`. **Arena is not in the loop** (only `main-arena-full`).
- A new page: add its name to both page loops (the PNG becomes `main-foo.png`); a tab or scrolled section: a public
  helper on `MainWindow`/`MainView` (like `ShowConstructedTab`, `HoverBattlegroundsGame`) called from your own
  render partial. See [hearthpulse-visual-check](../hearthpulse-visual-check/SKILL.md).

## Checklist

- [ ] Nav + page + every code-behind list in both UIs; `ShowPage("Foo")` works in both.
- [ ] VM partial in both UIs; logic in Core with tests.
- [ ] Layout: 40/120 page margins, cards 24 apart, Ornament headings, empty state text, compact mode (Avalonia).
- [ ] Tokens only; WPF rendered with one non-default theme.
- [ ] Render scene added; PNGs of both UIs inspected side by side.
