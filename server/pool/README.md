# Hearthpulse stats pool

A small Cloudflare Worker that collects the Battlegrounds games players choose to share and serves the totals:
heroes, final boards, trinkets, tribes, tavern tier timing, combat odds, picks (Discover, triple rewards, trinkets)
and the cards played. It also collects the Discover-style picks of shared Ranked and Casual Constructed games and
serves what players take from each card (the pick hint on the app's card pool panel). It is built to stay inside
Cloudflare's free plan.

It can also serve **base Battlegrounds statistics** (heroes, cards, trinkets and comp guides) from D1 at `/v1/meta`.
Hearthpulse ships that table empty: upstream filled it once from a third-party stats service, and that data is not
part of this repository. Fill it only from a source you are allowed to redistribute.

What a shared game contains: a game id (an HMAC of the game made with a key that never leaves the player's PC), the day it was played, solo or duos, the rating rounded down to 500,
the heroes offered and picked, the placement, and (if the player agreed to it) the game details listed in the app.
A shared Constructed game (consent version 4) holds only its id, day, format (standard or wild), whether it was won,
and its picks: the source card, the cards it offered and the one taken. No deck, class, rank or opponent.
No player name, BattleTag, account or IP address is stored, and nothing about other players.

## Free plan budget

The free plan allows 100,000 requests a day, 100,000 rows written and 5 million rows read per day.

- A game is one row (plus one index entry). The app sends a game when it ends, and earlier games in batches of 20,
  so a batch of 20 games is one request and one `INSERT` statement.
- The totals are not worked out on each read. A cron job (every 6 hours) rebuilds them only when new games came in,
  and stores them as one JSON row in `snap`. A read returns that row and is cached at the edge for 30 minutes.
- Each rebuild reads the last 30 days of games about 12 times. At 10,000 games in 30 days that is about 1 million
  rows read a day for both modes, well under the limit.

## Deploy

You need Node.js and a Cloudflare account.

```
cd server/pool
npx wrangler login
npx wrangler d1 create hearthpulse-pool
```

Copy the `database_id` it prints into `wrangler.toml`, then:

```
npx wrangler d1 execute hearthpulse-pool --remote --file=schema.sql
npx wrangler deploy
```

`deploy` prints the address, for example `https://hearthpulse-pool.<you>.workers.dev`. Put it in
`PoolClient.DefaultEndpoint` (src/Hearthpulse.Core/Pool/PoolClient.cs) and release the app.

To move an existing pool to a new version, run the new file in `migrations/` with
`npx wrangler d1 execute hearthpulse-pool --remote --file=migrations/<file>.sql`, then `npx wrangler deploy`.

## Base stats

`migrations/0004_meta.sql` creates the `meta` table, empty. The body is one compact JSON document (heroes per MMR
bracket, cards per bracket with their turn results, trinkets per MMR, comps with their MMR and hero placement, comp
guides) split into numbered rows small enough for a D1 statement. Until rows are added, `/v1/meta` answers 503 and
the app keeps its own base stats, with the shared games blended in. Create the table with:

```
npx wrangler d1 execute hearthpulse-pool --remote --file=migrations/0004_meta.sql --yes
```

## API

- `POST /v1/games` with `{"v":3,"games":[...]}`, 1 to 20 games. Each game:
  `{"id":"<32 hex>","day":20749,"mode":"solo","bracket":6500,"offered":["BG_HERO_1",...],"picked":"BG_HERO_1",
  "placement":3,"src":"live"}`, plus the optional details `turns`, `tier`, `triples`, `anomaly`, `tribes`, `comp`,
  `board` (`[{"c","g","a","h"}]`), `trinkets` (`{"o":[...],"p":[...]}`), `tierups` (`[[turn,tier]]`),
  `combats` (`[[turn,"W|T|L",taken,dealt,winChance|null]]`), `choices` (`[[turn,kind,source,[offered],[picked]]]`)
  and `plays` (`[[turn,card]]`). A game sent twice is stored once. Hearthpulse 0.8 and 0.9 still send one game
  (`"v":1` or `"v":2`) and are accepted.
- `GET /v1/stats?mode=solo` returns everything: `games`, `detailedGames`, `heroes`, `cards`, `trinkets`, `comps`,
  `tierUps`, `combatOdds`, `choices` and `plays`, over the last 30 days.
- `GET /v1/heroes?mode=solo` returns only the hero totals (used by Hearthpulse 0.8).
- `POST /v1/constructed` with `{"v":1,"games":[...]}`, 1 to 20 games. Each game:
  `{"id":"<32 hex>","day":20752,"format":"standard","won":true,"src":"live","picks":[[turn,"SOURCE",["A","B","C"],["B"]],...]}`
  (up to 60 picks, 2 to 8 offered, 1 to 4 picked among the offered). A bad pick is dropped; a game without a valid
  pick is rejected. Needs `migrations/0005_constructed.sql` on an existing pool.
- `GET /v1/choices?format=standard` (or `wild`) returns `{format, days, updated, games, sources:[{source, choices,
  options:[[card, offered, picked, wonWhenPicked], ...]}]}` over the last 30 days; options offered fewer than 3
  times are left out. Rebuilt by the same cron job into `snap` rows `choices-standard` / `choices-wild`.
- `GET /v1/meta` returns the base stats (see above) when the table has rows, cached at the edge for 30 minutes.

For a local test, run `npx wrangler dev --test-scheduled` and open `/__scheduled` to run the cron job.

## Validation tests

From the repository root, with Node.js 24 or later:

```
node --test server/pool/test/validation.test.mjs
```

The built-in runner needs no npm packages. Tests exercise the validators and `POST /v1/games` with an in-memory
database stub, and `POST /v1/constructed` with the pick totals on Node's built-in SQLite (`node:sqlite`, the real
`schema.sql`), without contacting Cloudflare. Invalid required game fields reject that game; invalid optional
details are omitted while the valid game summary is stored. Card IDs must be strings, and trinket lists are checked
before they are merged (up to four picks and twelve offers, with at most twelve distinct cards in their union).
