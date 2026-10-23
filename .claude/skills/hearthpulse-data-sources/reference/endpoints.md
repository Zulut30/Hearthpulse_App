# Endpoints Hearthpulse calls (checked in code on main 2e8d9be, 2026-10-23)

Paths are relative to each base URL. Cache files live under the data folder (`AppPaths.Cache` = `<data>/cache`).

## Hearthpulse data API - `https://api.kolodahearthstone.com/`

Transport: `HearthpulseApi(HttpClient http, string? baseUrl = null)` - base URL from the ctor, else `HEARTHPULSE_API`,
else `DefaultBaseUrl`; `GetAsync(path, bearer?, ct)`, `PostAsync(path, body, bearer?, ct)`; `Accept: application/json`;
20 s per call; result `ApiResult { Data, Meta, Error, Ok }`; POST bodies in snake_case, nulls omitted.

Typed calls: `HearthpulseData(HearthpulseApi api, string? cacheDirectory = null)`, cache `cache/api/`, `MaxAge` 6 h.

| Call | Cache file | Used by |
|---|---|---|
| `v1/battlegrounds/heroes?mode={mode}&limit=200` | `bg-heroes-{mode}.json` | `BattlegroundsMeta` fallback |
| `v1/battlegrounds/minions/stats?limit=500` | `bg-minions.json` | `BattlegroundsMeta` fallback |
| `api/db/archetypes?game_type=RANKED_STANDARD\|RANKED_WILD&rank_range=LEGEND\|DIAMOND_THROUGH_LEGEND\|ALL&limit=200` | per query | Constructed meta, mulligan |
| `api/db/archetypes/{id}/decks?limit=200` | per id | decks of an archetype |
| `api/db/archetypes/{id}/mulligan?display_only=false&limit=250` | per id | `MulliganGuideSource` (HSReplay, Standard Legend) |
| `api/db/archetypes/{id}/matchups?limit=500` | per id | matchups |
| `v1/hsguru/meta?format=&rank=&period=&min_games=100` | `hsguru-meta-{format}-{rank}-{period}.json` | tier list (period probed via `past_day` -> `current_period`) |
| `v1/hsguru/archetypes?format=&min_games=50&limit=500` | per format | archetype list |
| `v1/hsguru/archetypes/analysis?archetype=&format=` | `hsguru-analysis-{format}-{hash}.json` | Wild/HSGuru mulligan |
| `api/v1/constructed-cards?format=wild&per_page=200&page=N` (<= 80 pages) | `cache/api/card-pool.json`, 1 day | `CardPoolCatalog` |

Not used: `/v1/meta-intelligence/cards` (needs a token). Docs: `docs/constructed-meta-api.md`, `docs/mulligan-v2.md`,
`docs/discover-pool.md`. Server map (owner's notes): `C:\Users\zulut\Downloads\HeartLens-main\reference\server-151.md`.

## hearthpulse.net - `https://hearthpulse.net/api/v1/`

`AccountClient(HttpClient http, string? baseUrl = null)`, `ClientId = "manacost-tracker"`, form-encoded bodies.

| Call | Notes |
|---|---|
| `POST oauth/device/code` | `client_id`, `scope`; defaults `expires_in` 600, `interval` 5 |
| `POST oauth/token` | `grant_type=urn:ietf:params:oauth:grant-type:device_code` or `refresh_token`; refresh rotates |
| `POST oauth/revoke` | refresh token only |
| `GET me` (Bearer) | `user{id,name,profileUrl}`, `subscription` -> `Entitlements`; e-mail is never read or stored |
| `POST tracker/events/batch` | **not called by the app yet** - contract in shared notes `player-games-upload-contract.md` |

Approval page shows the client name "Manacost Tracker"; `/connect?user_code=` pre-fills the code. Staging identity:
`test.hearthpulse.net` (set `HEARTHPULSE_SITE_API` to try it).

## HearthstoneJSON

- `https://api.hearthstonejson.com/v1/latest/{locale}/cards.json` -> `cache/cards.{locale}.json`, 2 days (`CardDb`).
- `https://api.hearthstonejson.com/v1/latest/enUS/cards.collectible.json` -> `cache/cards.enUS.collectible.json`,
  2 days (card pool fallback).
- Art (`CardArt`, `CardRender` in both UIs): `https://art.hearthstonejson.com/v1/tiles/{id}.png`, `v1/256x/{id}.jpg`,
  `v1/512x/{id}.jpg` (WPF), `v1/render/latest/{locale}/256x/{id}.png`, `v1/bgs/latest/{locale}/256x/{id}.png`.
- Generators read the enUS dump you download yourself: `tools/counters/gen_counter_cards.py`,
  `tools/cardpool/gen_pool_tables.py`, `tools/effects/*.py`.

## hsbg.cards

`https://hsbg.cards/api/v1/cards?limit={0}&offset={1}&pool=current`, 100 per page, <= 20 pages, cache
`cache/bgcards.hsbg.json` 12 h (`BgCardDb`).

## blizzcore archetype names

`GET https://api.blizzcore.ru/archetypes?limit=1000` -> `{"count", "success", "items": [{"id", "name_en", "name_ru",
"created_at", "updated_at"}]}`, sorted by `name_en`. Checked Oct 2026: without `limit` it answers 200 items (`count` is
the size of the answer, not the total - 253 existed); `offset`, `skip`, `page`, `per_page` are ignored, so ask for one
big page (`ArchetypeTranslations.Limit`; an answer that long is logged as possibly cut). Some `name_en` use
typographic apostrophes (`Mecha’thun Druid`) and a few are Russian - matching ignores case, spacing and apostrophe
style. `ArchetypeTranslations` (12 h cache in `cache/api/blizzcore-archetypes.json`, stale copy offline, never throws);
labels fall back to the built-in `ArchetypeNames.RussianLabel`, then the English name. Tests:
`ArchetypeTranslationsTests` (fake handler).

## Battlegrounds meta - data API

`BattlegroundsMeta.LoadAsync(HearthpulseData)`, cache `cache/api/bg-heroes-<mmr>.json` and `bg-minions-<mmr>.json`, 6 h:

- `api/bg/heroes?mode=solo&mmr={TOP_50_PERCENT|TOP_20_PERCENT|TOP_5_PERCENT|TOP_1_PERCENT}` (bare answer; no `ALL`)
- `v1/battlegrounds/minions/stats?mmr={ALL|TOP_50_PERCENT|TOP_20_PERCENT}&period=CURRENT_BATTLEGROUNDS_PATCH&limit=500`

Comps, trinkets, tribe impact, turn results and MMR floors come only from the stats pool (`PoolClient.OverlayAsync`).
Firestone (`static.zerotoheroes.com`) is not used. Details: `docs/bg-meta-sources.md`.

## GitHub

`https://api.github.com/repos/Zulut30/IceCrowV2/releases/latest` - see `hearthpulse-release`.
