# OBS windows and streamer mode ("Стрим")

Two things for players who stream (plan 3.3, `TASK-2026-11-02-streamers`), in Settings → Stream of both UIs:

- **Windows for OBS**: three ordinary windows, each with one overlay panel, to add to OBS as a *Window Capture* and
  place anywhere in a scene. OBS's *Game Capture* does not see the overlay (it is a window of its own over the game), and
  *Display Capture* takes the whole overlay and every menu with it.
- **Streamer mode**: one switch that keeps opponents' names, BattleTags and ratings, and the player's own BattleTag, off
  the overlay, the OBS windows and the match page header.

## The windows

| Window (title) | Id | Panel | Default size (DIP) | Smallest | Shows the panel | Background only | "Hearthpulse" tile |
|---|---|---|---|---|---|---|---|
| Hearthpulse — Deck | `deck` | `DeckPanel` | 268 × 720 | 168 × 200 | a game other than Battlegrounds with its deck known | such a game, deck not recognised | no game, a Battlegrounds game, the Arena draft |
| Hearthpulse — Lobby | `lobby` | `LobbyPanel` | 246 × 306 | 156 × 120 | Battlegrounds (Duos too), hero picked, opponents seen | hero pick | no game or not Battlegrounds |
| Hearthpulse — Combat odds | `odds` | `OddsPanel` | 266 × 140 | 166 × 84 | a Battlegrounds fight with its odds | between fights, hero pick | no game or not Battlegrounds |

- **One model.** The windows show the overlay's own panel controls (`Overlay/DeckPanel`, `LobbyPanel`, `OddsPanel` in
  both UIs, also used by the overlay's `deck`, `lobby` and `odds` panels) on the same `OverlayViewModel`. A window can
  never show more than the overlay; the rules of `docs/match-replay.md` (during a game, only what the player can see)
  hold as they are. The states come from `OverlayViewModel.Streamer.cs` (`DeckWindowState`, `LobbyWindowState`,
  `OddsWindowState`: `Idle` / `Waiting` / `Shown`).
- **Independent of the overlay's switches.** A window works with the overlay off, its panel hidden or the overlay put
  away (Ctrl+Shift+H): the model reads the game while the overlay or any window is on. The switches change only whether
  a panel shows, never what it shows.
- **Look.** No title bar or frame (OBS would capture them): drag anywhere, resize at the 6-DIP edges, right-click (or
  Shift+F10) for *Original size*, *Always on top* and *Close window*; the menu is a popup window of its own and never in
  the capture. The panel scales with the window (`Viewbox`, scale = min((W − 16) / panel width, (H − 16) / panel
  height), top centre): while the deck fits, only the width sets the scale, and a deck too long shrinks as a whole. The
  window never sizes itself to the content, so the OBS source keeps its size. Under the panel lies a `Bg` plate with the
  card's radius: the card is a little see-through, and on a key colour it would turn green and lose its edges to the key.
  No hover, card preview or tooltip in the window; the row flash of the deck is the only motion, as on the overlay.
- **Background.** *Theme* (default: the theme's `Bg`, nothing to set up in OBS), *Green* `#00FF00` or *Magenta*
  `#FF00FF`. The two key colours are exactly the presets of OBS's *Color Key* / *Chroma Key* filters; they are a signal
  to OBS, not interface colours, so they are the same in every theme and live in one constant
  (`Presentation/Overlay/StreamWindowSpec.cs`, `StreamBackdrop`), not in `Theme.xaml` / `Theme.axaml`. Magenta is there
  because a green key also eats the green win numbers (`Positive`) and the Fel theme's accent.
- **Place.** Moves and sizes are kept by id in `AppSettings.StreamWindows` (DIP), saved when a move or resize ends. A
  saved window that no longer shows 40 × 40 on any work area (a monitor gone) opens at its default spot: the top left of
  the primary work area, side by side (24, 24), (308, 24), (570, 24).
- **Life.** A window switched on opens without taking the focus, on its place, in the state of the moment. Closing it
  (its menu, Alt+F4, the taskbar) switches it off; quitting the app closes it but keeps it on for the next start, so OBS
  finds it again by its title. The windows are in the taskbar (OBS lists no tool windows) and are not minimized by the
  app; a minimized window gives OBS an empty frame, so the help in Settings says not to minimize them. WPF rebuilds them
  with a theme change (the title stays, OBS picks the window up again in about a second); Avalonia recolours them in place.
- **Not done:** hiding the overlay itself from capture (`WDA_EXCLUDEFROMCAPTURE`, an idea for later), a browser source,
  a Twitch extension, a show delay against stream sniping.

### Adding a window in OBS

1. Turn the window on in Settings → Stream.
2. *Sources* → *+* → *Window Capture*; in *Window* pick "Hearthpulse — Deck" (or Lobby, Combat odds); untick
   *Capture Cursor*.
3. **Capture method:** *Windows 10 (1903 and up)* or *Automatic*, not BitBlt (WPF draws through DirectX; BitBlt gives a
   black frame). **Window match priority:** by title (the window class of WPF / Avalonia windows changes from start to
   start).
4. With a key background: add a *Color Key* filter to the source with the key colour *Green* or *Magenta*.

- **Titles are translated** ("Hearthpulse — Колода" in Russian). After switching the interface language OBS loses a
  window it matched by title: pick it again in the source's *Window* list.
- **Linux:** X11 — *Window Capture (Xcomposite)*, by title as on Windows. Wayland — the window is picked in the
  desktop portal's dialog each time OBS starts (an OBS / portal limit).
- **macOS:** *macOS Screen Capture* → *Window*; *Always on top* works.
- **Scaling:** windows are in DIP and OBS captures pixels: at 150 % the 268-DIP deck window is a 402-px source.

## Streamer mode

`AppSettings.StreamerMode`, off by default. One setting with three switches: Settings → Stream, the live bar's button
(right, beside "Arrange panels", shown in compact too; on = the glyph on an `AccentSoft` circle) and the tray's
"Streamer mode" check item (the tray icon's tooltip then reads "Hearthpulse · streamer mode"). No hotkey. The indicator
is never on the overlay or in the OBS windows: viewers see those.

| Where | Off | On |
|---|---|---|
| Overlay, `opponent` panel title | opponent's name (BattleTag), else class | class; "Opponent" until the class is known |
| Overlay, `opponent` subtitle | "class · rank" | "Opponent" (the line stays, the header keeps its height) |
| Lobby panel and window, leaderboard badges, the player popup | heroes (no player names yet) | the same; once P4 shows names and MMR (`TASK-2026-10-29-bg-lobby-panel`), no name (the hero) and no MMR column (it folds, no "—") |
| Combat odds, deck, `session`, `ladder-session`, end-of-game toast | own data | unchanged: no opponent names or ratings there; the player's own rank and rating stay |
| Match page header (main window) | your name (BattleTag without `#`), opponent's name, both ranks | "You", the opponent's class (Battlegrounds: "Lobby", as always), no opponent rank; your rank or rating stays |
| History, Home, live bar, My Stats, Arena | no names (opponent's class) | unchanged |
| Settings → Account | the site account's name | "Signed in" (review M1, coordinator's decision): the name stays off the stream |

- **Where.** In the overlay model (`OverlayViewModel.OpponentHeader`, `Streamer.cs`), so the OBS windows follow it with
  no code of their own, and in `MatchViewModel.Mask` for the match header. A switch applies on the next overlay update
  (the host pushes the last snapshot again) and to the open match page at once.
- **Only hides.** Nothing is deleted or left unrecorded. Names and MMR are still read while "Battlegrounds opponents'
  MMR" is on; the mode only does not show them (the conditions of `docs/bg-opponent-mmr.md` are not touched).
- **Contract for P4** (opponents' MMR on the lobby panel): a player's name and MMR on a `LobbyRow`, a leaderboard badge
  or the popup are built only while `StreamerMode` is off; the MMR switch in Settings gets the note "Not shown in
  streamer mode" (its key belongs to that task).

## Files

| Part | WPF `src/Hearthpulse.App` | Avalonia `src/Hearthpulse.UI` + `src/Hearthpulse.Desktop` |
|---|---|---|
| Panels | `Overlay/DeckPanel`, `LobbyPanel`, `OddsPanel` (`.xaml`) | `Overlay/DeckPanel`, `LobbyPanel`, `OddsPanel` (`.axaml`); row styles in `Overlay/OverlayLook.axaml` |
| Window | `Overlay/StreamWindow.xaml` (`WindowChrome`, DWM: square corners, no border colour) | content `UI/Overlay/StreamView.axaml`, window `Desktop/StreamWindow.cs` |
| Owner | `Overlay/StreamWindows.cs`, host lines in `App.Stream.cs` | `Desktop/StreamWindows.cs`, host lines in `DesktopApp.cs` |
| Settings → Stream, live bar | `Main/MainWindow.xaml` | `Main/MainView.axaml` |
| Shared | `Presentation/Overlay/OverlayViewModel.Streamer.cs`, `StreamWindowSpec.cs`; `Presentation/Main/MainViewModel.Streamer.cs`, `Main/Match/MatchViewModel.Streamer.cs` | same |
| Texts | `Core/Localization/Strings/{en,ru}/stream.json` | same |
| Renders | `DevRender.Stream.cs` | `OffscreenRenderer.Stream.cs` (+ `main-settings-stream-compact.png`) |

Render scenes: `stream-deck.png`, `stream-deck-green.png`, `stream-lobby.png`, `stream-odds.png`,
`stream-odds-magenta.png`, `stream-idle.png`, `overlay-constructed-streamer.png`, `main-settings-stream.png`, made-up
games from `Core/Tracking/StreamScene.cs` (no kept log needed).
