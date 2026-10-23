---
name: hearthpulse-tracking
description: Use when extending what Hearthpulse tracks from Hearthstone logs - a new IPowerListener or tracker, reading a new Power.log tag, a new value in LiveSnapshot/ConstructedView/BattlegroundsView, a new GameRecord field with a SQLite history column (HistoryStore.AddedColumns migration, backfill), or anything about the opponent's cards where the "no hidden information" rule applies. Triggers - "track X", "new tag", "store in history", "add a column", "opponent hand", "parser", "GameSession".
---

# Extending game tracking

Pipeline, threads and project map: [hearthpulse-dev](../hearthpulse-dev/SKILL.md). Log grammar and tags:
[hearthstone-context reference/power-log.md](../hearthstone-context/reference/power-log.md). Counters have their
own recipe: [hearthpulse-counters](../hearthpulse-counters/SKILL.md). Tests: [hearthpulse-testing](../hearthpulse-testing/SKILL.md).

## The rule first: no hidden information

Show and use only what the player could see on screen. The log contains more than the client shows (card ids of
cards drawn by the opponent in some paths, `CREATOR_DBID` of hidden creators, entity ids that survive a shuffle).
Using them is cheating and is a review **blocker**.

- Opponent's hand: a drawn card stays a card back until the game shows it again; a creator is named only through
  `DISPLAYED_CREATOR`/`CREATOR` of a revealed card, never `CREATOR_DBID`
  (`ConstructedTracker.HandSlotOf` and its comment; tests in `tests/Hearthpulse.Tests/OpponentHandTests.cs`).
- A card shuffled into a deck loses its identity: do not follow it by entity id through the deck
  (deck-hand review M1). Other leak paths found in review: DECK -> SETASIDE -> HAND, a `CardId` kept after
  `HIDE_ENTITY`, `CHANGE_ENTITY` treated as a reveal.
- Battlegrounds simulator: never the opponent's hand or secrets (`BattlegroundsTracker.BuildSide`,
  `src/Hearthpulse.Core/Battlegrounds/Effects/README.md`).
- Counters: only entities with a known card id; counters that need hidden data are shown for the local player only.
- Memory reads (`Memory/HearthstoneMemory`) are read-only and only for what the client displays.

When in doubt, write the negative test first ("... stays a card back", "... is not named").

## 1. Find the signal in real logs

Never design from imagination; real line order differs from what you expect (e.g. `TURN=1` is set before the opening
deal, the Coin can be a skin like `JAIL_COIN3` with `COIN_CARD=1`).

- Logs: `<Hearthstone install>/Logs/Hearthstone_yyyy_MM_dd_HH_mm_ss/Power.log` (+ `Power_old.log`, `Decks.log`,
  `LoadingScreen.log`). On this machine the install is `D:\Hearthstone` (settings `HearthstonePath`), so
  `D:\Hearthstone\Logs\`. Helpers: `HearthstoneInstall.LatestLogSession`, `AllLogSessions`.
- Search, then read the surrounding block:
  ```
  grep -n "tag=HERALD_COLOSSAL_AMOUNT" "D:/Hearthstone/Logs/Hearthstone_2026_10_23_13_40_10/Power.log" | head
  grep -n "BLOCK_START BlockType=PLAY" .../Power.log | head
  ```
  State comes from the `GameState.DebugPrint*` lines; the `PowerTaskList.` copies are only used to follow what the
  client is currently showing (BG combat replay) - match the `GameState.` line when you look for a tag.
- Tag names: `src/Hearthpulse.Core/Power/GameTag.cs` holds `const string`s (no enum); add a constant there when a tag
  is used in more than one place, plain literals are tolerated for one-offs. Names follow HearthSim python-hearthstone
  (MIT) - cite it if you copy a list of names.
- `tools/Hearthpulse.Probe` helps for some questions: `dotnet run --project tools/Hearthpulse.Probe -- heroes <Power.log>`
  (BG heroes), `mem`, `hover`, `class <Name>`, `upgrade` (memory; Windows, client running). There is no generic tag
  dump - use grep.
- Write down which logs you checked (file names, line numbers) in the doc/handover; check more than one game and
  both players' perspectives (you first / you second).

## 2. Listen: `IPowerListener`

Defined in `src/Hearthpulse.Core/Power/PowerParser.cs`; every member has a default body, override what you need:
`OnGameStarted`, `OnEntityCreated`, `OnEntityRevealed`, `OnTagChanged(game, entity, tag, previous, value)`,
`OnBlockStart(game, blockType, source)`, `OnBlockEnd`, `OnChoices(game, choiceType, source, options)` (local player
only), `OnChoiceMade`, `OnGameEnded`.

Semantics worth knowing:

- `OnTagChanged` fires for `TAG_CHANGE`, `HIDE_ENTITY` and tag lines after SHOW/CHANGE - not for tags set while an
  entity is being created and not when the value is unchanged.
- `OnEntityCreated`/`OnEntityRevealed` fire when the creation block is flushed (next non-`tag=` line or `Flush()`);
  read the entity's tags there, not on the `FULL_ENTITY` line.
- `OnBlockStart` runs before the block is pushed (`game.OpenBlocks` = enclosing blocks).
- `OnGameEnded`: game entity reached `STATE=COMPLETE`, or `AbandonCurrentGame()`.
- Entity API (`Power/Entity.cs`): `GetInt(tag)`, `GetString(tag)`, `Has(tag)`, `Zone`, `Controller`, `CardType`,
  `InHand`, `InDeck`, `CreatedTurn`, `EnteredHandTurn`, `Mulliganed`, `WasRevealed`, `IsOriginalDeckCard`.
  `GameState`: `LocalPlayerId`, `OpponentPlayerId`, `GameEntity`, `Turn`, `IsBattlegrounds`, `Controlled(player, zone)`.

Where to put new logic:

| You need | Do |
|---|---|
| More state for an existing area | extend `ConstructedTracker` / `BattlegroundsTracker` (or its counters tracker) |
| A fact only for the history row | `Tracking/GameFactsTracker.cs` (today: BG hero offer) |
| A new feature area | a new `sealed class XTracker : IPowerListener` in its own file/folder (pattern: `CardPool/CardPoolTracker.cs`) |

Register it in `GameSession.OnSessionStartedCore` with one line next to the others
(`_parser.AddListener(_cardPool);` ...); order = call order. If the area is big, give `GameSession` a partial
(`Tracking/GameSession.CardPool.cs`). History import has its own parser in `Data/LogBackfill.cs` (`Parse`): register
the tracker there too if the value must reach history rows of imported games. Counter trackers are **not** in the
backfill today.

Threads: parsing runs on the "Hearthpulse log watcher" thread under `GameSession`'s pipeline lock. Keep listener work
cheap (no I/O, no network, no LINQ over all entities per line); heavy work belongs in `BuildView` throttled by
publish, or on a background task with its own cancellation.

## 3. Expose: snapshot -> both UIs

1. Add an init property to the view record in `src/Hearthpulse.Core/Tracking/Views.cs` (`ConstructedView`,
   `BattlegroundsView`; `LiveSnapshot` for session-wide things). Views are positional records with init props - put
   the property inside the right record (a merge once put `CardPool` into the next record).
2. Fill it in the tracker's `BuildView` (`ConstructedTracker.BuildView`, `BattlegroundsTracker.BuildView`).
   `GameSession.Publish()` builds `LiveSnapshot` at most every 100 ms and raises `SnapshotChanged`.
3. Read it in **both** overlay view models (`src/Hearthpulse.App/Overlay/OverlayViewModel*.cs` and
   `src/Hearthpulse.UI/Overlay/OverlayViewModel*.cs`) - see [hearthpulse-overlay-panel](../hearthpulse-overlay-panel/SKILL.md);
   main-window pages: [hearthpulse-main-page](../hearthpulse-main-page/SKILL.md).

Performance: `BuildView` runs on every publish (up to 10/s). Do not rebuild expensive lists each time - cache by
input and rebuild on change (review findings on card pool and mulligan).

## 4. Persist: a new history field

`GameRecord` (sealed record) lives in `src/Hearthpulse.Core/Data/HistoryStore.cs`; rows are built by
`GameRecords.Build` (`Tracking/GameRecords.cs`), live from `GameSession.RecordIfFinished`, imported from
`LogBackfill`. A new column touches:

1. `GameRecord`: a nullable property (old rows have NULL).
2. `HistoryStore.AddedColumns`: append `("snake_name", "TEXT NULL" | "INTEGER NULL")`. Never edit the first-schema
   `CREATE TABLE` and never reorder the list; `AddMissingColumns` uses `PRAGMA table_info` + `ALTER TABLE ADD COLUMN`,
   so it is idempotent.
3. `Add()`: INSERT column list, `$param`, `AddWithValue` (`DBNull.Value` for null, bools as 1/0).
4. `FillMissingFields()`: `col = COALESCE(col, $v)` with the `IS NULL AND $v IS NOT NULL` guard, so backfill fills
   gaps without overwriting.
5. `Query()` mapping: read by `GetOrdinal("col")` with the `NullableString`/`NullableInt` helpers - never by index
   (an older app must still read a newer file).
6. `GameRecords.Build` (and `GameFactsTracker` if it is a fact the final state loses).
7. `tests/Hearthpulse.Tests/HistoryMigrationTests.cs`: extend `An_old_history_file_gets_the_new_columns_and_keeps_its_games`
   (new field null on old rows, reopen is a no-op) and `New_fields_round_trip`.

Old rows are never backfilled unless `LogBackfill` sees their logs again; say so in the UI ("no data for older
games") instead of showing 0. If other sessions consume the columns, write a short contract note
(example: `my-stats-history-fields.md` in the shared notes).

## 5. Checklist

- [ ] Signal confirmed in at least two real logs; line references noted.
- [ ] No hidden information; negative test for the leak path.
- [ ] Listener registered in `GameSession` (and `LogBackfill` if history needs it).
- [ ] View property + both overlay/main view models; render scene shows it ([hearthpulse-visual-check](../hearthpulse-visual-check/SKILL.md)).
- [ ] Migration: `AddedColumns` appended, insert/fill/query by name, migration test extended.
- [ ] Tests with synthetic lines in real order; full suite = baseline + new, only the 2 known failures.
- [ ] A replay over your kept logs for the new value (keep the output as `*-real-log-run.txt` evidence).
