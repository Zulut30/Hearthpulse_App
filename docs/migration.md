# Moving over from HDT / Firestone ("перенос")

A player who comes from Hearthstone Deck Tracker or Firestone gets their past games into History and My Stats and their
decks into the deck library, from the files those trackers keep on this PC. Only their data files are read, never
written, and none of their code is used (HDT: "All Rights Reserved"; Firestone: no licence). Code: `Core/Migration`
(`HdtData`, `HdtImport`, `FirestoneImport`, `MigrationScan`), the card `Main/Migration/MigrationCard` in both UIs over
the shared `Presentation/Main/Migration/MigrationViewModel.cs`. File formats in detail:
`reference/tasks/TASK-2026-11-01-migration/research.md`.

## Where it shows

- **Home page, first start:** a card "Switch from HDT" (or Firestone, or both) with what was found ("561 games and 89
  decks") and Transfer / Not now, while `AppSettings.MigrationOffered` is false and something can be brought over. After
  Transfer on it, it shows the progress and the result until Hide. Firestone with its history on its server only is not
  offered here (there is nothing to press).
- **Settings → Data:** the same card, whole: each source with its counts and folder (or "not found", "history on the
  server"), Transfer / Transfer again, Choose HDT folder… (a portable HDT; kept in `AppSettings.HdtFolder`), the last
  transfer's result (`AppSettings.LastMigration`), and that Hearthpulse is not affiliated with HearthSim or Zero to
  Heroes.
- The search reads every HDT file, so it runs at start only while the home card may still offer the transfer; after
  that, when Settings → Data opens. Transfer waits until the cards are loaded and the first import of the game's logs
  is done (otherwise the games would lack deck codes and classes, and the logs' record of them would come after).
  Closing the app stops a transfer after the game it is at.

## Hearthstone Deck Tracker

Folder `%APPDATA%\HearthstoneDeckTracker` (or the chosen one). Read: `DeckStats.xml` and `DefaultDeckStats.xml` (games),
`PlayerDecks.xml` (decks), `BgsLastGames.xml` (Battlegrounds hero, rating, final board), `Replays\*.hdtreplay`. Never
read: `config.xml`, `hsreplay_oauth`, `upload_token_history.txt`, a game's `ServerInfo`.

- Files are opened with sharing (HDT may run), without DTD or external entities, at most 64 MB; a file that does not
  parse is read from HDT's `.bak` copy; both broken stops the transfer ("damaged").
- **Games:** finished ones (`Win`/`Loss`/`Draw`), each `GameId` once, never `IsClone`, Mercenaries or spectated games.
  A game HDT kept a replay of is rebuilt from that replay's Power.log by our own parser (`LogBackfill.Parse`): the row
  is then exactly what our own log import makes, with a match replay in `<data>/replays`. Otherwise the row comes from
  the XML: game type (`GT_UNKNOWN` read from the mode, else the game is left out), format, start and end (HDT's local
  time with offset), heroes and classes, result, turns (HDT counts the player's own, as History does), first/coin, rank
  (`StarLevel`, `LegendRank`), the deck and the code of the version played, the opponent's revealed cards (turn 0:
  HDT does not say when); Battlegrounds: place (`BattlegroundsDetails`), MMR, anomaly, and from `BgsLastGames.xml` (matched by
  the start instant) the hero card, MMR after and final board; a broken `BgsLastGames.xml` only leaves those out.
  `source = "hdt"`. "Skipped" counts every game left out: unfinished, clones, spectated, Mercenaries, and records with
  no hero, place or known game type.
- **No duplicates:** a game is left out when History has its key, or a game of the same type starting within 2
  minutes against the same opponent class (Battlegrounds: with the same hero) — the same game recorded from the logs.
  The same game twice in HDT's own data (a reconnect saved as two games) keeps its longer, finished record. A game the
  app records itself later (live, from the logs) replaces a transferred row of it (`HistoryStore.Add`). Transferring
  again adds only what is new.
- **Stays on this PC:** games with `source` `hdt` or `firestone` are never sent with the profile upload or to the stats
  pool (`HistoryStore.PageForUpload`, `PoolGame.FromHistory`).
- **Decks:** every deck but Arena runs, Dungeon and Duels decks goes to `saved_decks` (`source = "hdt"`, HDT's deck id):
  name, class, the code of the current list (we build it: `DeckString.Encode`, the class's default hero, the format of
  the deck's last game, else Wild), tags (without HDT's own `All`, `Favorite`, `None`), note, archived, last edit. The
  deck library's page is roadmap 1.4. The versions of one HDT deck played in History become one deck family, named
  after it.
- On the owner's folder (2026-11-01): 560 games, 559 added, 433 rebuilt from HDT's replays, 83 decks, 21 s.

## Firestone

Firestone's history lives on its server. On this PC it may be in:

- the desktop build's `%APPDATA%\Firestone\FirestoneDB.sqlite` (table `matchHistory`, a JSON record in `data`): the
  search only notes that it is there; the transfer reads it from a copy (with its `-wal`) in `<data>/cache/import`, at
  most 500 MB;
- the Overwolf build's older `%APPDATA%\Overwolf\lnknbakkpommmjjdnelmfbjjdbocfpnpbkijjnob\user-match-history.json`, at
  most 64 MB.

The Overwolf build's own cache (Chromium IndexedDB on LevelDB, V8-serialized) is not read: the card says the history
is on Firestone's server. Records: game mode, format, start (`creationTimestamp` minus the game's length), heroes,
classes, result, turns, coin, deck name and code, legend rank; Battlegrounds place (`additionalResult`) and MMR.
Mercenaries and records without a result are left out. `source = "firestone"`, the same duplicate rule.

## Not done

- HDT's panel positions (`config.xml`): HDT keeps percentages of the game window with the deck list's right edge,
  ours are top-left fractions with our own panel sizes; an approximate move would land worse than our layout mode.
- Arena runs with their rewards (`ArenaReward` of HDT's Arena decks): roadmap 1.6.
- A Hearthstone install the app cannot find never runs the first import of the logs, so Transfer keeps waiting there.
