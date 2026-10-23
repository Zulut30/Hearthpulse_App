# Games to the hearthpulse.net profile ("My statistics")

The app sends finished games to the player's profile on hearthpulse.net, where `/profile/` shows Battlegrounds,
Standard, Wild and Arena statistics built from them. Code: `src/Hearthpulse.Core/Accounts/ProfileEvents.cs` (mapping),
`ProfileUploadQueue.cs` (queue), `ProfileUploader.cs` (sending), `AccountClient.PostEventsAsync` (HTTP). Tests:
`tests/Hearthpulse.Tests/ProfileEventsContractTests.cs` (contract, fixture `Fixtures/ProfileUpload/`) and
`ProfileUploadTests.cs`.

Site contract (HeartPulse repo, branch `claude/profile-game-stats-e88af0`, commit c0f0ca0):
`docs/specs/player-game-stats.md`, `docs/specs/tracker-ingestion.md`, strict check
`server/modules/playerGames/schema/gameSchema.ts`.

## When games are sent

- Only while the player is signed in to a Hearthpulse account (device flow, `docs/accounts-api.md`) **and** the switch
  "Send games to my profile" is on (Settings → Hearthpulse account, both apps; `AppSettings.UploadGamesToProfile`,
  default on: signing in is the opt-in, and the switch with its caption sits right under the account name).
- A game that ends while signed out or with the switch off is not queued. "Send past games" queues every game of
  `history.db` that the site has not settled yet.
- Each game is sent once, after it ends. A Battlegrounds game with a rating at its start waits up to 130 s for the new
  rating (the session reads it for 120 s), then goes with or without it.

## Request

`POST {site}/api/v1/tracker/events/batch`, `Authorization: Bearer <access token>` from `AccountService`
(scope `tracker.write`), JSON `{"events": [...]}`. Each event: `eventId`, `type`, `schemaVersion: 2`, `occurredAt`
(UTC, `yyyy-MM-ddTHH:mm:ssZ`), `payload`.

- **Batches**: at most 50 events and 1 MiB of JSON per call (the site allows 5 MiB per request, 512 KiB per event).
- **eventId**: UUID version 5 (SHA-1, fixed namespace) of the game's start second and game type taken from the history
  key, without the BattleTag. The same game always gets the same id, on any install, so a repeated send or a backfill
  after a reinstall is only acknowledged by the site. `arena_run`: UUID v5 of `runId` + number of games in the run (a
  new score is a new record; the site shows the newest one).
- **Answers**:
  - 202 `{accepted, rejected}`: both lists are settled and leave the queue; every rejected event is logged to
    `hearthpulse.log` (`profile upload: <type> <eventId> rejected: <code> (<field>)`) and never retried.
  - 400/413/422 (bad envelope, too large): the batch is sent again one event at a time; an event refused alone is
    logged and dropped.
  - no connection, 401/403, 429, 5xx, 404, or a 2xx without the answer object (captive portal): the events stay and
    sending pauses 30 s, 60 s, 120 s ... up to 30 min. A new game, sign-in or the switch tries again at once.
- **Queue**: `profile-upload.json` in the data folder, written atomically. At most 5 000 waiting events (oldest dropped,
  logged) and 50 000 remembered settled ids. It belongs to one site account (`owner` = user id from `/me`); when
  another account signs in it starts empty, so one player's games never reach another profile.

## Mapping from `GameRecord` (history.db `games`)

Common to all games:

| Payload | From | Notes |
|---|---|---|
| `occurredAt` | `started_at` | UTC; `arena_run`: the run's last game end |
| `durationSeconds` | `ended_at - started_at` | clamped to 0-14 400 |
| `turns` | `turns` | left out outside 0-200 |
| `result` | `result` | `Win/Loss/Tie` → `win/loss/tie`; Constructed and Arena games with `Unknown` are not sent; not sent for Battlegrounds (the site derives it from the place) |

Not sent at all: `Abandoned` games, Brawl, Practice, Friendly, Mercenaries, Other, and Constructed formats other than
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
| `deckName` | `deck_name` | control characters removed, trimmed, at most 80 characters |
| `opponentArchetype` | `opponent_archetype` | only a clear call from the opponent's played cards (`ArchetypeGuess`) |
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
2. Sign in from Settings, keep "Send games to my profile" on, press "Send past games".
3. The status line under the switch counts down; `hearthpulse.log` names any rejected event; `/profile/` shows the games.
