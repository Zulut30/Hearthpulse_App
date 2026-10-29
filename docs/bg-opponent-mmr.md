# Battlegrounds opponents' MMR

In a Battlegrounds game each opponent can show a rating next to their name: the rating of the row with that name on
Blizzard's public Battlegrounds leaderboard of the player's region. Firestone has the same idea ("View the MMR of your
opponents"); no code or data is taken from it. Setting `AppSettings.ShowBgOpponentMmr`, **off by default**.
Task card: `reference/tasks/TASK-2026-10-29-bg-lobby-panel/task.md` (P3 data, P4 UI).

## Why this is allowed (exception to the hidden-information rule)

The client does not show opponents' ratings, so this is an exception to "during a game only what the player sees"
(`docs/match-replay.md`). The owner approved it on 2026-10-29 ("давай сделаем как в firestone") after the legal report
`reference/reviews/LEGAL-HeartLens-bg-opponent-mmr.md` (L-M1). It holds only with these conditions:

- **L-M2, names never leave the computer.** The whole table of the region is downloaded and names are matched
  locally; nothing is ever asked by name (`v1/leaderboards/player`, `/players?q=` are not called). Names and ratings
  live only in the live view (`BgOpponent.PlayerName`, `BgOpponent.Mmr`): never in the history, SQLite, exports,
  uploads, the stats pool, `Diag` or any log. No dossier, no "met before".
- **L-m1, no guessing.** Leaderboard names have no `#1234`: a name on several rows gives "?" (`Ambiguous`), not the
  best one; a name on no row gives "<8000" (`BelowThreshold`, "<" + the table's `Floor`, its lowest rating: 8000 on
  all six tables, EU/US/AP solo and Duos, checked on 2026-10-29). Two players of one lobby with the same name (two
  opponents, or an opponent and the player), compared in any case, both get "?" whatever the table says.
- **L-m5, case (coordinator's decision, 2026-10-29; replaces "without regard to case" of the task card).** A rating
  only for one row spelled exactly like the name, case included; a row that matches only in another case gives "?",
  so does a name on several rows in any case. Latin and Cyrillic alike (ordinal comparison after trimming and
  Unicode NFC).
- **L-m6, only a whole table counts.** An empty table (a new season, a failing mirror), a page shorter than the total
  the first page reported, or a download stopped at the 300-page bound matches nobody: every opponent is `Unknown`
  rather than "<8000", and such a table is not cached.
- **L-m4.** No "average lobby MMR".
- **L-B2.** No Firestone data (`static.zerotoheroes.com`); the table comes from the Hearthpulse API.
- **L-m3.** The UI says where the number comes from ("from Blizzard's public leaderboard, 8000+"), and the feature is
  free.
- **L-m2.** The memory read is listed in `installer/windows/notice.txt`.

## Data flow

```
Power.log: local player's GameAccountId hi -> region (bits 32-39: 1 US, 2 EU, 3/4 AP; else none)
memory (setting on): GameState.s_instance.m_playerInfoMap (Map<int, SharedPlayerInfo>) -> player id -> m_name
  -> HearthstoneMemory.GetBattlegroundsLobbyNames -> MemoryPoller (WantLobbyNames) -> GameSession.SetLobbyNames
Hearthpulse API v1/leaderboards?mode=battlegrounds|battlegroundsduo&region=..&limit=100&offset=.. (&day= pinned)
  -> BgLeaderboardSource (cache/api/bg-leaderboard-<mode>-<region>.json, a day from the snapshot's captured_at;
     stale up to 7 days offline)
  -> BgLeaderboard.Match(name), then "?" for a name the lobby shares
GameSession.WithOpponentMmr -> BattlegroundsView.Opponents[i].PlayerName / .Mmr (live snapshot only)
```

- Power.log names only the local player and the Innkeeper in Battlegrounds, so the lobby names come from memory: the
  same read-only access as every other value (`OpenProcess` with `PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION`).
  The map key is the player id, the `PLAYER_ID` tag of the opponent's leaderboard hero. A `#1234` suffix is dropped,
  the client's `UNKNOWN HUMAN PLAYER` placeholder is no name.
- Names are read only while the setting is on, in a Battlegrounds game, until every opponent in the view has one.
  Turning the setting off forgets the names.
- The table is loaded the first time a game needs it (about 72 pages in EU, one after another on the thread pool).
  Its age counts from the server's snapshot (`snapshot.captured_at`, else the download time), not from the download:
  a day after the snapshot it is asked for again. When the first page shows the snapshot already held, that one
  request is all (the server has not renewed it yet).
- A table that is not fresh - a cached copy served while the server fails, a server that has not taken today's
  snapshot, a partial table - is asked for again every 30 minutes until the server gives a fresh whole one. A failed
  load is retried after 30 minutes; a 404 is remembered for a day.
- The local player's own rating is not involved and still comes from `NetCacheBaconRatingInfo`.

## Contract (`src/Hearthpulse.Core/Tracking/Views.cs`)

`BgOpponent.PlayerName` (null while off or not read) and `BgOpponent.Mmr` (null while off), `BgOpponentMmr(Status,
Rating)` with `Floor` (the table's lowest rating, set on every answer from a whole table; added to the card's contract
for the "<Floor" caption):

| Status | Meaning | Show |
|---|---|---|
| `Found` | one row has exactly this name, and no other player of the lobby has it; `Rating` set | the rating |
| `BelowThreshold` | no row has it, in any case | "<" + `Floor` (8000) |
| `Ambiguous` | several rows have it, the one row has it only in another case, or two players of the lobby share it | "?" |
| `Loading` | the table is downloading, or the first memory scan is still due | a placeholder |
| `Unknown` | no region (China, no account in the log), no whole table (offline without a cached copy, empty, cut short), no name read, memory not readable | nothing or "-" |
| `Off` | reserved; the session gives `Mmr = null` while the setting is off | - |

## Code and tests

- `src/Hearthpulse.Core/Battlegrounds/BgLeaderboard.cs` (table and matching),
  `BgLeaderboardSource.cs` (paging, cache, region/mode), `Memory/HearthstoneMemory.Lobby.cs` (names),
  `Tracking/GameSession.BgMmr.cs` (setting, names, view). Hosts: `SetBgLeaderboards`, `SetBgOpponentMmrEnabled` in
  `App.xaml.cs` and `UI/AppHost.cs`.
- `tests/Hearthpulse.Tests/BgOpponentMmrTests.cs`: matching (exact case, Latin and Cyrillic, several rows, floor,
  empty and partial tables), name spelling, region, paging without names, cache aged from the snapshot, one request
  for an unchanged snapshot, offline copy retried every 30 minutes, 404, cut-short and empty tables not cached;
  statuses on the real Battlegrounds fixture (names shared in the lobby or with the player give "?"), no request
  while the setting is off, names absent from the history database and the log.

## Checking it on a live game

Not checked on a live client yet. With Hearthstone in a Battlegrounds game:

1. **Names.** `dotnet run --project tools/Hearthpulse.Probe -- lobby` prints `pid=N name=...` for the lobby. Compare
   with the names on hovered leaderboard tiles. Nothing printed: `dotnet run --project tools/Hearthpulse.Probe --
   class SharedPlayerInfo` and `class GameState` show the current field names; fix them in `HearthstoneMemory.Lobby.cs`.
2. **The map key is the hero's `PLAYER_ID`** (the opponents are matched by it; a wrong key puts one player's rating
   next to another's hero). `dotnet run --project tools/Hearthpulse.Probe -- heroes <the session's Power.log>` prints
   `pid=N` with each leaderboard hero's card id. For at least two opponents, hover their tile: the hero shown there
   and the name shown there must have the same `pid` in the `heroes` and `lobby` outputs. Do not turn on the overlay
   column (P4) before this holds.
3. Turn the setting on, play a game: opponents that are on the leaderboard get their rating; check two of them on
   https://hearthstone.blizzard.com/en-us/community/leaderboards (Battlegrounds, your region).
4. `cache/api/bg-leaderboard-battlegrounds-<region>.json` appears once a day; `hearthpulse.log` has one line with the
   row count and no names.

Not verified: the regions of KR/TW accounts (mapped to AP), the Duos table's mode name on a live Duos game, whether the
client and the leaderboard keep the same case of a name (L-m5 assumes they do: otherwise more "?").
