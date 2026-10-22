# Hearthpulse stats pool

A small Cloudflare Worker that collects the Battlegrounds results players choose to share and serves the totals
per hero: games, average placement, top 4 and first place rates, and pick rate. The free Cloudflare plan is enough.

What a shared game contains: a random game id, solo or duos, the rating rounded down to 500, the heroes offered,
the hero picked and the final placement. No player name, BattleTag, account or IP address is stored.

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

## API

- `POST /v1/games` with `{"v":1,"gameId":"<32 hex>","mode":"solo","bracket":6500,"offered":["BG_HERO_1",...],"picked":"BG_HERO_1","placement":3}`
- `GET /v1/heroes?mode=solo&days=30&minRating=0` returns `{"games":N,"heroes":[{"hero","games","avgPlacement","top","first","offered","picked"}]}`