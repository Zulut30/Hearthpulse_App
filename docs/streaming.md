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
| Hearthpulse — Session | `session` | `StreamSessionPanel` (window only) | 266 × 184 | 166 × 104 | while it is on: the rank or rating since the stream started, the change, the record (Ranked) or the last places (Battlegrounds); "No games yet" before the first | — | window off |

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
- **Session window** (`TASK-2026-11-07-growth-app`): counts from the player's "Start a new session" (Settings → Stream,
  or the window's menu; `AppSettings.StreamSessionStart`) when that is later than the ladder session's start, else from
  the ladder session's start (Hearthstone's start or a break longer than the setting). Ranked or Battlegrounds, whichever
  was played last (the game being played decides first). Built in `GameSession.StreamSession.cs` only while the window
  is on (`LiveSnapshot.StreamSession`), shown by `OverlayViewModel.StreamSession.cs`. The player's own numbers only.
- **Not done:** hiding the overlay itself from capture (`WDA_EXCLUDEFROMCAPTURE`, an idea for later), a browser source,
  a show delay against stream sniping (the Twitch extension has its own delay, below).

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

## Twitch extension

Viewers of the player's Twitch channel see the deck in the Hearthpulse extension (panel and video overlay, the front
end in `twitch-extension/`): the cards left, a card large on hover (`TASK-2026-11-07-twitch-extension`). The path:
app → `POST /api/v1/twitch/state` on hearthpulse.net (our server, the EBS) → Twitch Extension PubSub → the extension.
No Twitch secret is in the app: it signs in with the account's own token (device flow, scope `tracker.write`).

- **Hidden until the wide release** (owner, 2026-11-07): the card shows only with `"TwitchPreview": true` in
  settings.json or `HEARTHPULSE_TWITCH=1` (`AppSettings.TwitchCardShown`); the code, settings and tests stay.
- **Settings → Stream → Twitch extension** (both UIs): the switch `AppSettings.TwitchExtension` (off by default), the
  delay `TwitchDelaySeconds` (0–600 s, a slider, whole seconds), a status line with a dot (green while the site takes
  the deck) and *Link Twitch*, shown while the account has no Twitch linked; it opens the status's `linkUrl`
  (else `<site>/account/twitch`) in the browser.
- **Status line:** off — none; *Checking the Twitch link…*; *Sign in to your Hearthpulse account*; *No Twitch account is
  linked*; *The extension is not set up on the site yet* (`503 twitch_not_configured`); *Connected as @login*; *The deck
  is not being sent: <reason>* (offline, the site has no extension yet, Twitch unavailable, the site refused the data).
  The login is the channel's own name, so it stays in streamer mode.
- **What is sent** (`Core/Twitch/TwitchState.cs`): exactly the OBS deck window's rule — a game other than Battlegrounds
  with its deck known gives `mode: "constructed"` with the deck's name and hero (or `null`), `format` (`standard`,
  `wild`, `twist`, `classic` by the log's format in Ranked, Friendly and Practice; `casual` for Casual; `other` for
  Arena, Brawl and the rest), the cards left in the deck and the rows of the deck panel in its order (cost, name — not
  the game's deck order) with `id`, `dbf`, `cost`, `name` (interface language), `left`, `total`, and `game.turn`.
  Anything else (no game, Battlegrounds, the Arena draft, a Brawl whose deck is unknown) is `{"mode":"none"}`. Never
  the hand, the deck order or anything about the opponent.
- **When** (`Core/Twitch/TwitchPublisher.cs`, built in `Core/Hosting/AppServices.cs` on every live snapshot): only with
  the switch on and signed in. Each new state waits *delay* seconds in a queue (at least OBS's Stream Delay: the
  extension only holds back what it shows, the data reaches viewers before the video, so this delay is the only
  guard against stream sniping; a shorter delay lets the newest state replace older ones still waiting), then goes out only if it differs from the
  last one sent, at most once a second (the last of a burst). An unchanged deck is sent again every 15 s, so the site
  keeps a fresh copy for viewers who join; `none` goes once. `seq` starts at the Unix time in milliseconds when the app
  starts and grows by one per send, so it also grows across restarts. Switching off clears the channel with one
  `none`, at once.
- **When it stops:** before the first send and after every stop the app asks `GET twitch/status`. Signed out (`401`),
  not linked (`404 twitch_not_linked` or the status), not configured (`503 twitch_not_configured`), any other refusal
  (`400`, `413`, `5xx`) or offline: nothing is sent and the next try is a minute later (or the site's longer
  `Retry-After`), so linking on the site is picked up within a minute. `429` waits its `Retry-After` (5 s without one).
  Signing in or turning the switch on tries at once. The app never throws or loops on an error.
- **Site and front end** are separate packages of the task: the site (`/api/v1/twitch/*`, compression, the 5 KB limit,
  Helix PubSub, `GET twitch/ext/snapshot` for a viewer's cold start) and the extension (`twitch-extension/README.md`).

## Streamer mode

`AppSettings.StreamerMode`, off by default. One setting with three switches: Settings → Stream, the sidebar overlay card's button
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
| History, Home, sidebar status, My Stats, Arena | no names (opponent's class) | unchanged |
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
| Settings → Stream, sidebar overlay card | `Main/MainWindow.xaml` | `Main/MainView.axaml` |
| Shared | `Presentation/Overlay/OverlayViewModel.Streamer.cs`, `StreamWindowSpec.cs`; `Presentation/Main/MainViewModel.Streamer.cs`, `Main/Match/MatchViewModel.Streamer.cs` | same |
| Texts | `Core/Localization/Strings/{en,ru}/stream.json`, `twitch.json` | same |
| Twitch extension | `Main/SettingsTabs/StreamSettings.xaml`; `Core/Twitch/*`, `Presentation/Main/MainViewModel.Twitch.cs` | `Main/SettingsTabs/StreamSettings.axaml`; same |
| Renders | `DevRender.Stream.cs` | `OffscreenRenderer.Stream.cs` (+ `main-settings-stream-compact.png`) |

Render scenes: `stream-deck.png`, `stream-deck-green.png`, `stream-lobby.png`, `stream-odds.png`,
`stream-odds-magenta.png`, `stream-idle.png`, `overlay-constructed-streamer.png`, `main-settings-stream.png`, made-up
games from `Core/Tracking/StreamScene.cs` (no kept log needed).
