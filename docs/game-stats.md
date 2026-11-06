# Game records for Hearthpulse's own statistics

Every finished game a player agrees to send goes to the stats pool as one compact, versioned **game record**: all
modes we track (Ranked, Casual, Arena, Brawl, Battlegrounds solo and duos), not only Battlegrounds. The records are
the raw material for Hearthpulse's own statistics later (ADR 0009: only our own analytics may be paid). Where they are
stored and why: [ADR 0011](adr/0011-game-records-in-the-stats-pool.md).

Code: `src/Hearthpulse.Core/GameStats/` (record, client, queue, sender), the server in
`server/pool/src/events.js`, the switch in Settings → Data of both apps (`MainViewModel.GameStats.cs`).

## What a record holds (version 1)

Built from the game's history row and its saved replay, after the game, off the log and UI threads.

| Key | What | Source |
|---|---|---|
| `id` | keyed hash of the game's history key (32 hex): a record sent twice is stored once | `GameStatsClient.RecordId` |
| `day`, `secs`, `turns` | day played (days since 1970, UTC), length, turns | history row |
| `mode`, `fmt` | `ranked` `casual` `arena` `brawl` `bg` `duos`; `standard` `wild` `classic` `twist` | history row |
| `rank` | league only (`bronze` … `diamond`, `legend`), Ranked only; never the legend number | history row |
| `cls`, `hero`, `deck` | your class, base hero, deck code (no deck name) | history row |
| `ocls`, `oarch` | opponent's class and archetype (the archetype the app named from cards it saw) | history row |
| `res`, `first` | `W` `L` `T`; whether you went first | history row |
| `mull` | `{o, k, r, n}`: offered, kept, replaced, received (the Coin left out) | replay |
| `plays` | `[turn, card, mana spent, created]`: every card you played; created = not in the deck list (0 without a deck) | replay frames |
| `oplays` | `[turn, card]`: cards the opponent played face up | replay frames |
| `choices` | `[turn, kind, source, [offered], [picked]]`, kind from the source's mechanics: `discover` `adapt` `chooseone` `dredge` `excavate` `choice` | replay |
| `draws` | `[turn, card, created]`: every card that came into your hand after the mulligan | replay |
| `exc` | `[turn, treasure]`: Excavate treasures that came into your hand | replay + `Excavate.Treasures` |
| `bg` | Battlegrounds: `placement`, `mmr` (rounded down to 500), `offered`/`picked` heroes, `anomaly`, `tribes`, `tier`, `triples`, `tierups`, `trinkets` `{o, p}`, `quests`, `board`, `combats`, `choices`, `plays`, `buys`, `sells` | the pool view of the game (`PoolGame`) + the BG replay's buy/sell steps |
| `app` | app version | |

Not sent: Friendly, Practice, Mercenaries and other modes; abandoned games and games without a result; games brought
over from another tracker. Lists are cut at the server's limits (400 plays, 120 choices, 300 draws, 6 mulligan cards).

**Only what the player saw** (ADR 0005). A Constructed replay is read with `MatchReplay.ParseSeen`, which leaves out the
post-game part (`N`/`K` lines: what the log revealed at the end about card backs), so an opponent card that was never
shown (a secret that did not trigger, a card left in hand) has no name in the record. No player name, BattleTag,
account id, deck name, legend number or exact rating. `GameStatsRealLogTests` checks this on the kept logs; the
server rebuilds every record from known fields only, so anything else is dropped.

## Consent and the switch

- One switch, Settings → Data: «Отправлять все мои игры для статистики Hearthpulse» / "Send all my games to Hearthpulse
  statistics", its tooltip lists every field, the line under it says what is never sent, the 180 days, that the
  statistics may be used in paid features, and that switching off deletes the sent games. Shown only once the pool is
  set up (`PoolClient.Available`). Stored as `AppSettings.GameStatsConsent` (the list version agreed to, 0 = no).
- The stats pool's first-start question (WPF) now covers game records too: `PoolClient.ConsentVersion` 5. Players who
  had agreed to version 4 are asked again with the longer text; until they answer, only what they agreed to is sent,
  and game records are not. "Share" turns on both; "No thanks" turns off both, as every earlier version bump did.
- Switching off: the queue is emptied and `POST /v1/events/forget` deletes every record of this install; the request
  waits in the queue (`game-stats/forget`) until the pool answers.

## Queue and sending

- `game-stats/` in the data folder, one JSON file per record; at most 300 records, 4 MB and 14 days (oldest dropped).
- Batches of up to 20, 12 s apart (the pool takes 6 requests a minute from one address). `2xx` removes the batch;
  `400`/`413`/`422` drop it for good; anything else (offline, `429`, `503` daily budget, `404` before the deploy) waits
  and retries after 30 s, doubling up to 30 min. Never on the UI or log thread; nothing waits for it.
- The install id is a keyed hash of the pool secret (`GameStatsClient.InstallId`), never shown and never linked to an
  account.

## Server (`server/pool`)

- `POST /v1/events` `{"v":1,"iid":"<32 hex>","games":[…]}`, 1-20 records, body up to 512 KB, one record up to 24 KB
  after validation; 8,000 records a day in all (`EVENTS_DAILY`, then `503`), plus the Worker's rate limiter.
- `POST /v1/events/forget` `{"iid":"…"}` → `{"ok":true,"deleted":n}`.
- `GET /v1/events/summary` → `{window, updated, games, days:[{day, mode, fmt, games}], discover:[{kind, card, offered,
  picked, rate}], mulligan:[{card, offered, kept, rate}]}` over the last 30 days, each card from 10 games or more;
  rebuilt by the 6-hourly cron job when new records came in, cached at the edge for 30 minutes.
- Table `e` (`WITHOUT ROWID`, indexes by day and install), migration `migrations/0007_events.sql`; kept 180 days.
- Tests: `node --test server/pool/test/events.test.mjs`.

## Checking a build

1. A copy of the data folder (`HEARTHPULSE_DATA`), `"PoolEndpoint": "https://<pool>"` and `"GameStatsConsent": 1` in
   its `settings.json`.
2. Play or finish a game; the log has `Game stats: sent 1 game(s)`.
3. `GET <pool>/v1/events/summary` (after the next cron run, or `npx wrangler dev --test-scheduled`) counts it.
