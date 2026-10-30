---
name: hearthpulse-overlay-panel
description: Use when adding or changing an in-game overlay panel in Hearthpulse (WPF OverlayWindow.xaml and Avalonia OverlayView.axaml) - a new MovablePanel with an Id, an extracted UserControl like MulliganPanel/OpponentHandPanel/CardPoolPanel, an OverlayViewModel.Feature.cs partial, OverlayModels records, a Show* setting or mode, its default position and layout mode, click-through (OverlayHit, platform layer), and its render scene. Triggers - "панель оверлея", "overlay panel", "show X in game", "move/resize panel", "panel steals clicks", "deck/opponent/counters/mulligan/card-pool panel".
---

# Adding or changing an overlay panel

The overlay is a transparent, click-through window over the game. Every panel is a `MovablePanel` the player can
drag, resize and reset in layout mode. Both UIs have the same panels with the same Ids. At `7657bb5` there are 26:
`deck`, `opponent`, `opponent-archetype`, `ladder-session`, `turn-timer-opponent`, `turn-timer`, `opponent-hand`,
`secrets`, `counters`, `bg-counters`, `card-pool`, `card-pool-browser`, `mulligan-bars`, `mulligan-summary`, `bgbar`,
`hero-row`, `trinket-row`, `tavern-row`, `odds`, `choice-row`, `plan`, `session`, `lobby`, `toast`, `arena-picks`,
`arena-draft`. The two surface files are authoritative; compare their `MovablePanel` Id sets after a change.
Arena draft panels show in the `DRAFT` scene, outside games (`docs/arena-draft.md`).

Look and tokens: [hearthpulse-design](../hearthpulse-design/SKILL.md). Data into the snapshot:
[hearthpulse-tracking](../hearthpulse-tracking/SKILL.md). PNG proof: [hearthpulse-visual-check](../hearthpulse-visual-check/SKILL.md).

## Files

| Role | WPF `src/Hearthpulse.App/Overlay/` | Avalonia `src/Hearthpulse.UI/Overlay/` |
|---|---|---|
| Surface with all panels | `OverlayWindow.xaml` (+ `.xaml.cs`: layout, click-through, `RenderToFileAsync`) | `OverlayView.axaml` (+ `.axaml.cs`: layout, `IsInteractiveAt`) |
| Panel host control | `MovablePanel.cs` | `MovablePanel.cs` (also defines `OverlayHit`) |
| Platform VM adapter | `OverlayViewModel.Platform.cs` (framework values/timer), `.Focus.cs` (WPF feature) | `OverlayViewModel.Platform.cs` |
| Platform records | `OverlayModels.Platform.cs` (`HandMark`, WPF-only members) | `OverlayModels.Platform.cs` (`HandMark`) |
| Extracted panels | `MulliganPanel`, `OpponentHandPanel`, `CardPoolPanel`, `CardPoolBrowser` (`.xaml`) | same (`.axaml`) |
| Window host | `App.xaml.cs` | `src/Hearthpulse.Desktop/OverlayWindow.cs` + `Platform/*OverlayPlatform.cs` |

Common view models and display records live in `src/Hearthpulse.Presentation/Overlay/`: `OverlayViewModel.cs`
and feature partials, plus `OverlayModels.cs` / `.Widgets.cs`. Both UI projects compile the same sources into their
existing namespaces/assemblies. Edit those files once; retain framework types (WPF `GridLength`, framework-specific
`Thickness`, dispatcher timers) and intentional platform-only members in the local adapters. Domain rules belong
in Core. See `src/Hearthpulse.Presentation/README.md`; the Windows presentation suite checks both compiled variants,
while the cross-platform test project continues to reference Avalonia UI.

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
   Add English/Russian localization keys and bind with `{i:Loc Key}`; use sentence case (`docs/localization.md`).
4. **VM partial** `src/Hearthpulse.Presentation/Overlay/OverlayViewModel.X.cs`, maintained once for both UIs:
   ```csharp
   public bool ShowX { get => _showX; private set => Set(ref _showX, value); }
   private void UpdateX(ConstructedView? view, AppSettings settings)
   {
       ShowX = settings.ShowX && view?.X is { Count: > 0 };
       // build display records once per change, not per publish
   }
   ```
   Follow the existing conditional namespace/import header. Call it with **one line** from `Update(...)` in the
   shared `OverlayViewModel.cs`, next to `UpdateLive`, `UpdateHandMarks`,
   `UpdateCardPool`, `UpdateBgCounters` (Constructed-only parts may hang off `UpdateLive` in `.Live.cs`, as
   `UpdateMulligan` does). `Update` runs up to 10 times a second: compare inputs and skip rebuilding
   when nothing changed (`Replace<T>` helper in `OverlayViewModel.cs` diffs collections).
5. **Models**: small immutable records in shared `Presentation/Overlay/OverlayModels.cs` - display strings, flags,
   card ids; no brushes. Framework-specific member types stay in each UI's `OverlayModels.Platform.cs`.
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
8. **Clicks** (below), **render scene** (below), tests for Core rules and presentation behavior; build both flavors
   and run `Hearthpulse.Presentation.Tests` on Windows when changing shared models/adapters.

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
- Look per panel: `AppSettings.PanelLooks` (`PanelLook`: `Opacity` 0.3-1, `Compact`) by `Id`, apart from `Panels`.
  The tag's buttons (shown under the mouse in layout mode and with Ctrl in a game) step size, opacity, compact and
  reset; Shift+wheel fades, (Ctrl+)right-click resets everything. Opacity fades the content only (`Body`).
  A panel with card lists sets `CanCompact="True"`; its row templates react to compact - WPF: a
  `<Trigger SourceName="RowRoot" Property="o:MovablePanel.Compact" Value="True">` (inherited attached property),
  Avalonia: `o|MovablePanel.compact Grid.cardRow` styles (heights in styles, not local values, or they never yield).
- Dragging snaps to the screen guides and to the edges of other shown panels (`PanelSnap.Snap` with neighbours).
- During a game Ctrl over a shown panel arms it (`IsDragArmed`; the overlay tick, `ArmedArea`): frame, the
  `Overlay.Move.Hint` tag and the corner marker `PART_Grip`, whose drag resizes in wheel steps
  (`PanelSnap.CornerScale`, top-left fixed). A drag ends on button up or when the panel loses the mouse
  (`OnLostMouseCapture` / `PointerCaptureLost`); the overlay keeps the mouse while a panel `IsDragging`. Unlocked
  panels (`AppSettings.OverlayPanelsUnlocked`, `OverlayViewModel.PanelsUnlocked` -> `MovablePanel.IsUnlocked`) arm
  under the pointer without Ctrl and show their frame (`IsFree` / class `free`). A locked panel the pointer rests on
  for `PanelArming.OfferDelay` shows `PART_Lock` ("Unlock panels"; "Lock panels" once unlocked, `OffersLock`), the
  only part of it that takes the mouse. Panels on the game's cards (`SitsOnGame`: `GameAnchored` or a wide row's
  `PlaceholderAlignment`) never unlock or offer it. The rule and the log lines live once in the shared `PanelArming`;
  the hosts only find the panel under the pointer. The first
  show over the game puts a one-time tip at the top centre (`ShowMoveTip` via `OverlayViewModel.OfferMoveTip`,
  `AppSettings.OverlayMoveTipShown`), hidden by the toast timer.
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

Find it by Id in both XAML files and the shared VM partial named after it; edit the shared behavior once and each
affected platform view/adapter; re-render before/after. If a common entry point (`Update`) or both platform controls
(`MovablePanel`) must change and another session also touches it, agree
on one owner ([hearthpulse-parallel-work](../hearthpulse-parallel-work/SKILL.md)).

## Checklist

- [ ] `Show*` setting + toggle in both main windows; panel hidden when there is nothing to show.
- [ ] Same Id/Title/default spot in both UIs, direct child of `Surface`, not over game controls.
- [ ] Only the clickable parts carry `OverlayHit.Interactive`.
- [ ] Tokens only, no hex; Avalonia compiled bindings build without warnings.
- [ ] No hidden information on the panel ([hearthpulse-tracking](../hearthpulse-tracking/SKILL.md)).
- [ ] Render scene in both UIs, PNGs inspected, `overlay-layout.png` checked.
