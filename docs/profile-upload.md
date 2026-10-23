# Games to the hearthpulse.net profile ("My statistics")

The app sends finished games to the player's profile on hearthpulse.net, where `/profile/` shows Battlegrounds,
Standard, Wild and Arena statistics built from them. Code: `src/Hearthpulse.Core/Accounts/ProfileEvents.cs` (mapping),
`ProfileEventSchema.cs` (the site's check, ported), `ProfileUploadQueue.cs` (queue), `ProfileUploader.cs` (sending),
`AccountClient.PostEventsAsync` (HTTP), `HistoryStore.PageForUpload` (backfill reads). Tests:
`tests/Hearthpulse.Tests/ProfileEventsContractTests.cs` (contract, fixture `Fixtures/ProfileUpload/`) and
`ProfileUploadTests.cs`.

Site contract (HeartPulse repo, branch `claude/profile-game-stats-e88af0`, commit 0ac2bcf):
`docs/specs/player-game-stats.md`, `docs/specs/tracker-ingestion.md`, strict check
`server/modules/playerGames/schema/gameSchema.ts`.

## When games are sent

- **Opt-in.** `AppSettings.UploadGamesToProfile` is `null` until the player answers; only `true` sends or queues
  anything. Once signed in (and once for players who signed in before this feature) both apps show a card on top of the
  main window, "Send your games to your hearthpulse.net profile?", with the list below, a link to the site's privacy
  policy and the buttons **Turn on** / **Not now** (`MainViewModel.AskUpload`, `AnswerUpload`; in WPF it waits while
  the stats pool question is up). "Not now" stores `false` and the card does not come back.
- The switch "Send games to my profile" (Settings → Hearthpulse account, both apps) changes the answer later. Under it:
  the same list (`MainViewModel.UploadWhat`), links "Privacy policy" (`{site}/privacy/`) and "See, download or delete
  your data on the site" (`{profile}#account-tracker-data`, the account page's «Данные приложения»: export and
  deletion of everything the tracker stored), the "Send past games" button and the queue status.
- Only while signed in and switched on: a game that ends otherwise is not queued, and switching off drops what was
  waiting (and a backfill under way).
- "Send past games" (only when switched on) starts a backfill: the history is read 1 000 rows at a time
  (`PageForUpload`, by row id, with only the `opponent_archetype` card column) whenever fewer than 1 000 events wait,
  so the queue never holds the whole history. Its cursor (the last row queued) is saved with the queue and survives a
  restart; Arena runs are built from all Arena games on the first page. Games the site already settled are skipped.
- Each game is sent once, after it ends. The site keeps the first copy of an id forever, so:
  - every event passes `ProfileEventSchema` (the site's checks: fields, ranges, free text, `occurredAt` from 2014 to a
    day ahead, 32 KiB) before it is queued; one that fails is logged (`not sent, the site would not read its <field>`)
    and not sent, so a fixed mapping can still send it later under the same id;
  - a Battlegrounds game with a rating at its start and none after waits until 130 s after its end (`ended_at`, so the
    wait survives a restart; the session reads the rating for 120 s), then goes with or without it.

## Request

`POST {site}/api/v1/tracker/events/batch`, `Authorization: Bearer <access token>` from `AccountService`
(scope `tracker.write`), JSON `{"events": [...]}`. Each event: `eventId`, `type`, `schemaVersion: 2`, `occurredAt`
(UTC, `yyyy-MM-ddTHH:mm:ssZ`), `payload`.

- **Batches**: at most 50 events and 1 MiB of JSON per call (the site allows 5 MiB per request, 32 KiB per game event).
- **eventId**: UUID version 5 (SHA-1, fixed namespace) of the game's start second and game type taken from the history
  key, without the BattleTag. The same game always gets the same id, on any install, so a repeated send or a backfill
  after a reinstall is only acknowledged by the site. `arena_run`: UUID v5 of `runId` + number of games in the run (a
  new score is a new record; the site shows the newest one).
- **Answers**:
  - 202 `{accepted, rejected, warnings?}`: both lists are settled and leave the queue; every rejected event
    (`INVALID_EVENT`, `EVENT_LIMIT_EXCEEDED`, `QUOTA_EXCEEDED` past 100 000 events per account) is logged to
    `hearthpulse.log` (`profile upload: <type> <eventId> rejected: <code> (<field>)`) and never retried. An event in
    `warnings` (`INVALID_STATISTICS`: stored, but the statistics skip it) is logged as `stored without statistics` -
    a mapping bug to fix here; resending the same id changes nothing on the site. The status line counts both since
    the app started ("2 games sent without statistics, 1 game refused by the site; see hearthpulse.log").
  - 400/413/422 (bad envelope, too large): the batch is sent again one event at a time; an event refused alone is
    logged and dropped.
  - no connection, 401/403, 429, 5xx, 404, or a 2xx without the answer object (captive portal): the events stay and
    sending pauses 30 s, 60 s, 120 s ... up to 30 min. A new game, sign-in or the switch tries again at once.
- **Queue**: `profile-upload.json` in the data folder (waiting events, backfill cursor, owner), indexed by event id in
  memory and written atomically in batches: after a game is queued, after a backfill page, at the end of each send
  pass, and at once for a change of account or the switch turned off. Settled ids are appended to
  `profile-upload.json.settled` as they come (at most 50 000 kept, rewritten past that). At most 5 000 waiting events
  (oldest dropped, logged; the backfill never gets near it). An entry replaced while it was being sent stays and is
  logged (the site keeps the copy it got first). Everything belongs to one site account (`owner` = user id from `/me`);
  when another account signs in it starts empty, so one player's games never reach another profile.

## Mapping from `GameRecord` (history.db `games`)

Common to all games:

| Payload | From | Notes |
|---|---|---|
| `occurredAt` | `started_at` | UTC; `arena_run`: the run's last game end |
| `durationSeconds` | `ended_at - started_at` | clamped to 0-14 400 |
| `turns` | `turns` | left out outside 0-200 |
| `result` | `result` | `Win/Loss/Tie` → `win/loss/tie`; Constructed and Arena games with `Unknown` are not sent; not sent for Battlegrounds (the site derives it from the place) |

Not sent at all: games dated before 2014-01-01 (a broken clock; the site ignores them), `Abandoned` games, Brawl, Practice, Friendly, Mercenaries, Other, and Constructed formats other than
`FT_STANDARD`/`FT_WILD` (Classic, Twist).

### `battlegrounds_match` (modes Battlegrounds, Duos)

| Payload | From | Notes |
|---|---|---|
| `heroCardId` | `hero` | skin → base hero (`CardDb.BaseHeroId`); game skipped without a valid id |
| `placement` | `placement` | 1-8 (Duos 1-4), game skipped otherwise |
| `duos` | `mode == "Duos"` | |
| `ratingBefore`, `ratingAfter` | `rating_before`, `rating_after` | null outside 0-30 000 |
| `anomalyCardId` | `anomaly` | |
| `races` | `races` (`"BEAST,DEMON,..."`) | upper case, only the site's ten tribes (ABERRATION is left out until the site adds it), at most 6 |
| `tavernTier` | `tavern_tier` | left out outside 1-7 |
| `finalBoard.minions` | `final_board` `[{id, atk, hp, gold}]` | `{cardId, attack, health, golden}`, first 7, stats clamped to 0-100 000 |
| `combats` | `combat_results` `[{turn, result, opponent, taken, dealt}]` | `{turn, result, damageDealt, damageTaken, opponentHeroCardId}`, turns 1-60, damage clamped to 0-200, at most 60 |
| `heroOptions` | `offered_heroes` (comma list) | base heroes, the pick always included; rerolls append to the list, so more than 4 keep the last three plus the pick |

Not sent (not stored): `offers` (Discover, trinket and quest choices).

### `constructed_match` (modes Ranked, Casual)

| Payload | From | Notes |
|---|---|---|
| `format` | `format` | `FT_STANDARD` → `standard`, `FT_WILD` → `wild` |
| `ranked` | `mode == "Ranked"` | |
| `playerClass`, `opponentClass` | `player_class`, `opponent_class` | one of the 11 classes, game skipped otherwise |
| `heroCardId`, `opponentHeroCardId` | `hero`, `opponent_hero` | the opponent's hero is on the board for everyone |
| `deckCode` | `deck_code` | only if it matches `^[A-Za-z0-9+/=_-]{4,256}$` |
| `deckName` | `deck_name` | control and bidi characters removed, trimmed, at most 80 characters; left out when it holds something shaped like a BattleTag (`#` + 3-6 digits), which the site refuses |
| `opponentArchetype` | `opponent_archetype` | only a clear call from the opponent's played cards (`ArchetypeGuess`); same text rules as `deckName` |
| `wentFirst` | `went_first` | `false` = the coin, null when unknown |
| `rankBefore` | `rank_star_level`, `rank_legend` | Ranked only: `{starLevel}` (Legend: 51 + `legendRank`) |

Not sent: `archetype` (the own deck's archetype is not stored), `rankBefore.stars` (stars within a level are not read),
`rankAfter` (the rank is read only during a game), `playerMulligan` and `opponentDeck` (v1 lists, not used by the
statistics).

### `arena_match` and `arena_run` (mode Arena)

Runs are split as My Stats splits them (`ArenaStats.Runs`: by Decks.log deck id, else same class until the win or loss
limit). When an Arena game ends, its `arena_match` and the run's new `arena_run` go together.

| Payload | From | Notes |
|---|---|---|
| `runId` | first game of the run | `arena-<start UTC yyyyMMddTHHmmssZ>`, stable while games are added |
| `gameNumber` | place in the run | 1-20 |
| `winsBefore`, `lossesBefore` | earlier games of the run | |
| `underground` | `game_type` contains `UNDERGROUND` | |
| `playerClass`, `opponentClass`, `wentFirst`, `deckCode` | as in Constructed | |
| `arena_run.wins`, `losses`, `complete` | the run's games | `complete` = 12 wins or the loss limit |

The site accepts at most 3 losses; Underground Arena runs with more (the app assumes 5, unverified) are capped at 3 in
`losses` and `lossesBefore`.

## Privacy

Never in a payload: the player's and the opponent's BattleTags (`player_name`, `opponent_name`), not even hashed into
the event id; log lines; anything the player did not see (the opponent's hand, deck or secrets). The opponent's
archetype is only the history's guess from cards the opponent played face up. Tokens go only in the `Authorization`
header and are never logged.

## Checking by hand

1. Run the site locally (HeartPulse, see its `docs/specs/player-game-stats.md` "Development data") and point the app
   at it: `HEARTHPULSE_SITE_API=http://localhost:<port>/api/v1/`, `HEARTHPULSE_DATA` = a copy of the data folder.
2. Sign in from Settings, answer **Turn on** on the card (or turn the switch on), press "Send past games".
3. The status line under the switch counts down ("... More past games follow." while the backfill reads); `hearthpulse.log`
   names any rejected or statistics-less event; `/profile/` shows the games.
