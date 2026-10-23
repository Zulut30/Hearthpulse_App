---
name: hearthpulse-overlay-panel
description: Use when adding or changing an in-game overlay panel in Hearthpulse (WPF OverlayWindow.xaml and Avalonia OverlayView.axaml) - a new MovablePanel with an Id, an extracted UserControl like MulliganPanel/OpponentHandPanel/CardPoolPanel, an OverlayViewModel.Feature.cs partial, OverlayModels records, a Show* setting or mode, its default position and layout mode, click-through (OverlayHit, platform layer), and its render scene. Triggers - "панель оверлея", "overlay panel", "show X in game", "move/resize panel", "panel steals clicks", "deck/opponent/counters/mulligan/card-pool panel".
---

# Adding or changing an overlay panel

The overlay is a transparent, click-through window over the game. Every panel is a `MovablePanel` the player can
drag, resize and reset in layout mode. Both UIs have the same panels with the same Ids (18 today: `deck`, `opponent`,
`opponent-hand`, `counters`, `bg-counters`, `card-pool`, `card-pool-browser`, `mulligan`, `bgbar`, `hero-row`,
`trinket-row`, `tavern-row`, `odds`, `choice-row`, `plan`, `session`, `lobby`, `toast`).

Look and tokens: [hearthpulse-design](../hearthpulse-design/SKILL.md). Data into the snapshot:
[hearthpulse-tracking](../hearthpulse-tracking/SKILL.md). PNG proof: [hearthpulse-visual-check](../hearthpulse-visual-check/SKILL.md).

## Files (same names in both UIs)

| Role | WPF `src/Hearthpulse.App/Overlay/` | Avalonia `src/Hearthpulse.UI/Overlay/` |
|---|---|---|
| Surface with all panels | `OverlayWindow.xaml` (+ `.xaml.cs`: layout, click-through, `RenderToFileAsync`) | `OverlayView.axaml` (+ `.axaml.cs`: layout, `IsInteractiveAt`) |
| Panel host control | `MovablePanel.cs` | `MovablePanel.cs` (also defines `OverlayHit`) |
| View model | `OverlayViewModel.cs` + partials `.Live` `.Mulligan` `.Hand` `.Deck` `.CardPool` `.BgCounters` | same files, near-identical code |
| Small records for binding | `OverlayModels.cs` (`DeckTile`, `CounterChip`, `HandTile`, ...) | `OverlayModels.cs` |
| Extracted panels | `MulliganPanel`, `OpponentHandPanel`, `CardPoolPanel`, `CardPoolBrowser` (`.xaml`) | same (`.axaml`) |
| Window host | `App.xaml.cs` | `src/Hearthpulse.Desktop/OverlayWindow.cs` + `Platform/*OverlayPlatform.cs` |

The view models are copies: write the partial once, copy it, then fix only the UI-specific types
(WPF `GridLength`/`Visibility` vs Avalonia `double`/`bool`). Tests cannot reach these projects, so keep logic
(what to show, sorting, numbers) in Core and the VM thin.

## Recipe: new panel "X"

1. **Data**: the value arrives in `LiveSnapshot` (`ConstructedView` / `BattlegroundsView` in
   `src/Hearthpulse.Core/Tracking/Views.cs`). See [hearthpulse-tracking](../hearthpulse-tracking/SKILL.md).
2. **Setting**: `public bool ShowX { get; set; } = true;` with a `/// <summary>` in `src/Hearthpulse.Core/AppSettings.cs`
   next to the other `Show*` flags. A mode (not just on/off) is an enum or a string-keyed dictionary like
   `CounterModes` (missing = default).
3. **Settings toggle in both main windows** - "In-game overlay" card (or "Counters and mulligan"):
   WPF `src/Hearthpulse.App/Main/MainWindow.xaml` (a `CheckBox Style="{StaticResource Switch}"` bound to
   `Settings.ShowX`; changes reach `OnSettingChanged` automatically), Avalonia `src/Hearthpulse.UI/Main/MainView.axaml`
   (`Classes="switch"`; `MainView.axaml.cs` hooks every CheckBox of the view into `OnSettingChanged` at load).
   UI text is English, sentence case: "Show card pool: what a Discover or random card can give".
4. **VM partial** `OverlayViewModel.X.cs` in both UIs:
   ```csharp
   public bool ShowX { get => _showX; private set => Set(ref _showX, value); }
   private void UpdateX(ConstructedView? view, AppSettings settings)
   {
       ShowX = settings.ShowX && view?.X is { Count: > 0 };
       // build display records once per change, not per publish
   }
   ```
   Call it with **one line** from `Update(...)` in `OverlayViewModel.cs`, next to `UpdateLive`, `UpdateHandMarks`,
   `UpdateCardPool`, `UpdateBgCounters` (Constructed-only parts may hang off `UpdateLive` in `.Live.cs`, as
   `UpdateMulligan` does). `Update` runs up to 10 times a second: compare inputs and skip rebuilding
   when nothing changed (`Replace<T>` helper in `OverlayViewModel.cs` diffs collections).
5. **Models**: small immutable records in `OverlayModels.cs` (both UIs) - display strings, flags, card ids; no brushes.
6. **View**: for anything bigger than a few lines, a UserControl `XPanel.xaml` / `XPanel.axaml` in `Overlay/`
   (copy `MulliganPanel` headers: WPF `xmlns:o="clr-namespace:Hearthpulse.App.Overlay"`, local `i:VisibleWhen`;
   Avalonia `xmlns:o="using:Hearthpulse.UI.Overlay"`, `x:DataType="o:OverlayViewModel"`, `i:Truthy`). DataContext is
   inherited from the overlay - do not set it. Card style: WPF `Style="{StaticResource OverlayCard}"`, Avalonia
   `Classes="overlayCard"`; colours only from tokens.
7. **Host it** as a **direct child of the `Surface` grid** (panels are discovered with
   `Surface.Children.OfType<MovablePanel>()`, nested ones are never positioned or saved):
   ```xml
   <o:MovablePanel Id="x" Title="X stats" Shown="{Binding ShowX}"
                   HorizontalAlignment="Center" VerticalAlignment="Bottom" Margin="0,0,0,300">
       <o:XPanel />
   </o:MovablePanel>
   ```
   Same Id, Title, alignment and Margin in `OverlayWindow.xaml` and `OverlayView.axaml`.
8. **Clicks** (below), **render scene** (below), tests for the Core logic.

## Default position and layout mode

- The surface is a `1920 x 1080` reference grid scaled to the game window
  (`scale = clientHeight / 1080 * clamp(OverlayScale, 0.6, 1.6)`; the width follows the aspect ratio). Defaults are
  just the XAML `HorizontalAlignment` / `VerticalAlignment` / `Margin` in those units - anchor to the edge you mean
  (`Right` + `Bottom` for things near End Turn) so wide and narrow screens work.
- Keep game UI clear: the hero portraits, hand, End Turn button, Discover cards and the BG tavern row. Panels of
  different modes may share a spot (Constructed `card-pool` vs BG `bgbar`), panels of the same mode may not.
- The player's moves are saved in `AppSettings.Panels` (`Dictionary<string, PanelPlacement>`: `X`, `Y` as fractions
  of the surface, `Scale` 0.6-1.6) keyed by `Id`. **Never rename an Id** - there is no migration; saved placements
  would be lost.
- Layout mode = `OverlayViewModel.LayoutEditing` (`IsEditing` on every panel through the panel style): placeholder
  with the `Title`, drag to move, wheel to resize, right-click resets one; "Reset all" / "Done" banner. Opened from
  Settings "Arrange overlay panels", the tray, the main window toolbar and Ctrl+Shift+L. Your panel gets all of
  this for free - give it a clear `Title`.
- `MovablePanel.KeepOnScreen` pulls a saved panel back onto the surface using its measured content size; a panel
  that grows while shown (lists, tooltips) should be checked at the bottom/right edge in layout mode.

## Click-through

The window passes every click to the game except over elements marked interactive:

- Mark the clickable element (not the whole panel unless all of it is clickable):
  `o:OverlayHit.Interactive="True"` on the card Border (`counters`, `bg-counters`), the buttons (`MulliganPanel`
  plates), or the items (`OpponentHandPanel` marks). Hover previews (`c:CardPreview.*`) need it too.
- WPF: `OverlayWindow.xaml.cs` `UpdateClickThrough` hit-tests every 33 ms and toggles `WS_EX_TRANSPARENT`.
  Avalonia: `src/Hearthpulse.Desktop/OverlayWindow.cs` `Tick()` asks `OverlayView.IsInteractiveAt` and calls
  `IOverlayPlatform.SetClickThrough` (Windows `WS_EX_TRANSPARENT`, macOS `setIgnoresMouseEvents:`, X11 input shape).
- A panel that must react to the pointer **without** blocking the game (the card pool): keep it non-interactive and
  read the pointer from the platform layer - WPF `PoolInput.Cursor()` / `IsDown` polled in `CardPoolPanel.xaml.cs`,
  Avalonia `IOverlayPlatform.MouseButtons()` passed through `OverlayView.UpdateCardPoolPointer` -> `CardPoolPanel.UpdatePointer`
  (commit `dfd9a97`). Copy that pattern; check it on every desktop or say which ones you only compiled.
- Interactive areas over Discover cards, the hand or End Turn steal the player's clicks - a review major.

## Render scene

- `overlay-layout.png` shows the new placeholder automatically - check that it does not sit on another panel of the
  same mode.
- Add a content scene: build `LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true,
  GameType = ..., Constructed = view }`, call the overlay view model's `Update`, save with WPF
  `_overlay.RenderToFileAsync(path, 1920, 1080, 0)` / Avalonia `overlayView.Fit(1920, 1080)` + `Save(...)`. Put it in
  your own partial (`DevRender.<Feature>.cs`, `OffscreenRenderer.<Feature>.cs`) with one call from the main file.
  Details: [hearthpulse-visual-check](../hearthpulse-visual-check/SKILL.md).

## Changing an existing panel

Find it by Id in both XAML files and the VM partial named after it; change both UIs in the same commit; re-render
before/after. If a shared method (`MovablePanel`, `Update`) must change and another session also touches it, agree
on one owner ([hearthpulse-parallel-work](../hearthpulse-parallel-work/SKILL.md)).

## Checklist

- [ ] `Show*` setting + toggle in both main windows; panel hidden when there is nothing to show.
- [ ] Same Id/Title/default spot in both UIs, direct child of `Surface`, not over game controls.
- [ ] Only the clickable parts carry `OverlayHit.Interactive`.
- [ ] Tokens only, no hex; Avalonia compiled bindings build without warnings.
- [ ] No hidden information on the panel ([hearthpulse-tracking](../hearthpulse-tracking/SKILL.md)).
- [ ] Render scene in both UIs, PNGs inspected, `overlay-layout.png` checked.
