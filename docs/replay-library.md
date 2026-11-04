# Replay library ("библиотека реплеев")

The History page is the library of match replays: tabs **Recent / Saved / Imported**, a star, "Share", "Import".
Replays themselves (format, what they may show) are in [match-replay.md](match-replay.md). Code: `Core/Replay`
(`ReplayLibrary`, `ReplayImport`, `ReplayShare`, `MatchPage.File`), shared view models
`Presentation/Main/MainViewModel.Library.cs` and `Presentation/Main/Match/ReplayLibraryViewModel.cs`, views
`Main/Match/ReplayLibraryView` in both UIs.

## Folders

| Tab | Folder | Kept |
|---|---|---|
| Recent | `<data>/replays` (History's files) | History's rows; files trimmed at 300 MB / 3 000 (`ReplayStore`) |
| Saved | `Documents/Hearthpulse/Replays/Saved` | forever: neither the trim nor updates nor uninstalling touch it |
| Imported | `Documents/Hearthpulse/Replays/Imported` | the last 500 files / 200 MB, the file imported first goes first (`PruneByWriteTime`: an old log's games are not trimmed at once) |

With `HEARTHPULSE_DATA`, in a portable copy or without a Documents folder both live in `<data>/library/{saved,imported}`
(`AppPaths.LibraryRoot`), so development never writes to the real Documents. Copies made for Share go to
`<data>/cache/share/<folder>/` and are deleted after 24 h (at every share and every start, `CacheCleanup`), as are
work folders a broken Power.log import left in `<data>/cache/import` after an hour.

- **Star** copies the file into Saved under the same name (the History row keeps its own file); the star is lit by
  name. A History game whose file the trim deleted opens from its Saved copy. Taking the star off deletes the copy;
  when it is the last copy of the replay the card asks first. Imported games can be starred too (the copy outlives the
  500 limit).
- **Recent** cards are History's rows (the History filter applies); the final board, hand, hero powers and trinkets
  come from the file's header (`ReplayStore.ReadHeader`: the first two lines only, off the UI thread, kept per file). A
  file of format 3 or older shows what the row has. A game without a file shows the "no replay" mark instead of the
  star and Share.
- **Imported** never goes into `history.db` or the stats, a Power.log's games included (the owner, 2026-11-01): the
  log may be somebody else's, and the player's own logs already reach History through the log import.
- **Opening a Saved or Imported file without a History row** goes through the import filter again
  (`MatchPage.LoadFile`): a file put in the folder by hand shows no ranks, ratings or hidden cards, and a file the
  filter refuses shows as unreadable. The cards of Saved and Imported are built from the filtered header too
  (`ReplayLibrary.CheckedHeader`, once per file and write time); a refused file is a "damaged" card that can only be
  deleted (Imported) or unstarred (Saved). Every replay file is read with the 32 MB text limit (`ReplayStore.Load`).
- The header cache keys on the file's write time: a file trimmed away or written again is read afresh. A star on a
  game whose file was written again later (a reconnect completed it) replaces the older copy.
- Cards never show names or BattleTags (also on Recent, whose rows hold them); imported cards have no ranks or rating.
  A file opened without a History row says "You" and "Opponent" (`MatchPage.RecordOf`).

## Import

Import (button, or Ctrl+V on History with a file or a path on the clipboard) takes a `.hpr` (packed or not) or a
Power.log (`ReplayImport.ImportFile`). A foreign file is untrusted:

- **Size:** at most 16 MB packed and 32 MB of text; unpacking stops at the limit (gzip bomb). Event lines at most as
  many as the recorder writes (60 000 Constructed, 100 000 Battlegrounds), post-game lines at most 20 000.
- **Format line** `HPR2`-`HPR5` (`HPR1` and newer ones: "format not supported"; anything else: "not a replay").
- **Grammar:** every line must match its format (Constructed: `ConstructedLine` in `ReplayImport`, the format 3 table
  of match-replay.md; Battlegrounds: `BattlegroundsReplayGrammar`; post-game `N`/`K`), else the whole file is refused
  ("damaged or altered"). Card ids look like the game's (letters and digits with an underscore, or one of the 11 real
  ids without one, `ReplayImport.Card`): a player's name in a card's place is refused (property tests, Etalon T4).
  Tags come from the recorder's lists, numbers are bounded, nothing else may be on a line. A refused file leaves nothing
  behind.
- **Hidden information is cleaned, not refused:** a card id in the opponent's hand or secret (a quest stays face up
  only when the card data says it is one) becomes a card back and its tag lines go; Battlegrounds: Bob's offer during
  a combat and the combat opponent outside one are dropped, and cards still shown when they must leave view get an `X`
  line (Bob's at `F`, the opponent's before the next `T`); lines about a card not shown go.
- **Rebuilt, not copied:** the header is written anew from its known fields only: kind, the summary (each field
  checked: card ids, plain words, bounded numbers, the deck code only when it decodes, the archetype only as a plain
  name), truncated, the event count. Never the ranks, the ratings, the legend number or any unknown field. Format 2 and
  3 files get a summary from their events (heroes from `E ... HERO`, the result from `R`) and no date (their stored
  name starts with `game-`). The rounds and lobby of a
  Battlegrounds `G` line are rebuilt the same way. What is stored is this format 4 copy.
- **Power.log:** its finished games are recorded as the log import does (`LogBackfill.Parse`) into a work folder under
  `<data>/cache/import`, then each goes through the same filter into Imported; the work folder is deleted. The day comes
  from a `Hearthstone_yyyy_MM_dd_HH_mm_ss` folder name, else from the file's own dates.
- The stored name is `<game start yyyyMMddHHmmss>-<12 hex of SHA-256 of the rebuilt text>.hpr`: the same file imported
  twice is "already in Imported".

## Share

`ReplayShare.Prepare`: the replay through the same filter, with the start reduced to the date of play (as the player's
clock showed it), written to a folder of its own under the share folder as `hearthpulse-replay-<yyyyMMdd>-<mode>.hpr`
(mode in lower case: `ranked`, `battlegrounds`, `duos`...). The replay folder's own name is not used: it hashes the game
key, which holds the BattleTag. WPF puts the file on the clipboard as a file; Avalonia as a file where the platform
takes one, else the copy's folder opens with the file selected and the status line says to drag it into the chat
(the path itself is never put on the clipboard: it holds the system user's name).

A shared file is a valid import: it is format 4 with a summary, no names, BattleTags, account ids, device data,
ranks, ratings or legend number, and no time of day. "Upload to the site" sends the same copy without the post-game
`N`/`K` lines of a Constructed game, and the server checks it again: [web-replays.md](web-replays.md).

## Screens

Tabs, the folder button (Saved, Imported) and Import on the first row; under it the search box (up to 420 wide) and
the result filter "All / Wins / Losses" (plan R1); a status line under them (success, error, "Importing…"), gone at the
next tab, filter, opened match or action; then the cards.

Search and the result filter (`ReplayLibraryViewModel.Search`, `ResultFilter`) apply to every tab together with the
History mode filter in the page's header: a card shows when its hero or class (`Title`), caption (mode, format, deck),
opponent, the opponent's archetype, result or date holds the text (substring, case ignored, spaces at the ends
dropped) and its result passes ("Win" / "Loss"; Battlegrounds: places 1–4 win, Duos 1–2; ties, games without a result
only under "All"). Nothing is read from the files. Ctrl+F on History focuses the box, Esc in it clears the text, the
✕ clears it too. When the tab and mode give games but the search lets none through, the empty state says so
("Nothing matches “…”", or that no game of the tab has the result) with "Clear search" (text and result); an empty
tab or mode keeps its own text. A card chosen for the player (an import, a game sent from a deck page) clears a search
that would hide it. The search and the result are not kept between runs. Avalonia compact: the box takes the whole
width, the result filter goes under it. Battlegrounds card: hero, name and
mode · turns · tier, hero powers and trinkets (up to 3, "+N"), the final board as small ovals with stats (golden,
taunt, divine shield), the hand (up to 3, "+N"), place and rating change, when · how long, star, Share, delete
(Imported). Constructed card: our hero with the opponent's portrait over its corner, deck (else class) and class ·
mode · format · rank · turns, "vs class" and the archetype, result, rating change, when · how long. Delete and the
last copy's unstar ask inside the card. Avalonia compact hides the opponent column. Hover on a portrait shows the
full card.

## Known limits

- The Saved tab shows the History row of a starred game only while it is among the last 400 games read; older ones
  show from the file's summary.
- A Power.log outside a session folder takes its day from the file's dates: games after midnight may get the
  previous day.
- Not checked on a live game or by render (the task had no render step).
