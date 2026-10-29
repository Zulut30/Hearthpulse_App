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
  best one; a name on no row gives "<8000" (`BelowThreshold`, the table's floor).
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
  -> BgLeaderboardSource (cache/api/bg-leaderboard-<mode>-<region>.json, 1 day; stale up to 7 days offline)
  -> BgLeaderboard.Match(name)
GameSession.WithOpponentMmr -> BattlegroundsView.Opponents[i].PlayerName / .Mmr (live snapshot only)
```

- Power.log names only the local player and the Innkeeper in Battlegrounds, so the lobby names come from memory: the
  same read-only access as every other value (`OpenProcess` with `PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION`).
  The map key is the player id, the `PLAYER_ID` tag of the opponent's leaderboard hero. A `#1234` suffix is dropped,
  the client's `UNKNOWN HUMAN PLAYER` placeholder is no name.
- Names are read only while the setting is on, in a Battlegrounds game, until every opponent in the view has one.
  Turning the setting off forgets the names.
- The table is loaded the first time a game needs it (about 72 pages in EU, one after another on the thread pool),
  then once a day. A failed load is retried after 30 minutes; a 404 is remembered for a day.
- The local player's own rating is not involved and still comes from `NetCacheBaconRatingInfo`.

## Contract (`src/Hearthpulse.Core/Tracking/Views.cs`)

`BgOpponent.PlayerName` (null while off or not read) and `BgOpponent.Mmr` (null while off), `BgOpponentMmr(Status,
Rating)`:

| Status | Meaning | Show |
|---|---|---|
| `Found` | one row has the name; `Rating` set | the rating |
| `BelowThreshold` | no row has it | "<8000" |
| `Ambiguous` | several rows have it | "?" |
| `Loading` | the table is downloading, or the first memory scan is still due | a placeholder |
| `Unknown` | no region (China, no account in the log), no table (offline without a cached copy), no name read, memory not readable | nothing or "-" |
| `Off` | reserved; the session gives `Mmr = null` while the setting is off | - |

## Code and tests

- `src/Hearthpulse.Core/Battlegrounds/BgLeaderboard.cs` (table and matching),
  `BgLeaderboardSource.cs` (paging, cache, region/mode), `Memory/HearthstoneMemory.Lobby.cs` (names),
  `Tracking/GameSession.BgMmr.cs` (setting, names, view). Hosts: `SetBgLeaderboards`, `SetBgOpponentMmrEnabled` in
  `App.xaml.cs` and `UI/AppHost.cs`.
- `tests/Hearthpulse.Tests/BgOpponentMmrTests.cs`: matching, name spelling, region, paging without names, cache,
  offline, 404, statuses on the real Battlegrounds fixture, names absent from the history database and the log.

## Checking it on a live game

Not checked on a live client yet. With Hearthstone in a Battlegrounds game:

1. `dotnet run --project tools/Hearthpulse.Probe -- lobby` prints `pid=N name=...` for the lobby. Compare with the
   names on hovered leaderboard tiles. Nothing printed: `dotnet run --project tools/Hearthpulse.Probe -- class
   SharedPlayerInfo` and `class GameState` show the current field names; fix them in `HearthstoneMemory.Lobby.cs`.
2. Turn the setting on, play a game: opponents that are on the leaderboard get their rating; check two of them on
   https://hearthstone.blizzard.com/en-us/community/leaderboards (Battlegrounds, your region).
3. `cache/api/bg-leaderboard-battlegrounds-<region>.json` appears once a day; `hearthpulse.log` has one line with the
   row count and no names.

Not verified: the regions of KR/TW accounts (mapped to AP), the 8000 floor outside EU, the Duos table's mode name on a
live Duos game.
