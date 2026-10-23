# Battlegrounds stats: where each number comes from

Battlegrounds community stats no longer come from Firestone (`static.zerotoheroes.com`): there is no permission to use
its data. The owner's decision: **heroes and minions from our API (`api.kolodahearthstone.com`, HSReplay data), the
rest from the Hearthpulse stats pool once it runs**. The app makes no request to `zerotoheroes.com` any more.

Checked against the live API on 2026-10-23 (`openapi.json`, "Hearthstone Data API" 0.1.0). Field-level notes on the
API: `.claude/skills/hearthstone-context/reference/kolodahearthstone-api.md`.

## 1. What Firestone gave, and where it comes from now

| Firestone file (used by the advisor / pages) | Now | Gaps |
|---|---|---|
| Hero stats per MMR bracket 100/50/25/10/1: average place | **API** `GET /api/bg/heroes?mmr=TOP_50_PERCENT\|TOP_20_PERCENT\|TOP_5_PERCENT\|TOP_1_PERCENT` (current BG patch). Brackets in the app: top 50%, 20%, 5%, 1% | No "all ranks" slice for heroes (`mmr` rejects `ALL`): the widest hero bracket is top 50%. |
| Hero pick rate | API `pick_rate_value` (percent) | — |
| Hero place distribution, top 4, 1st | API `placement_distribution` (8 strings "22.00%") | — |
| Hero sample size (`dataPoints`) | — | **Not published.** Shown as "—"; the app treats 0 as "unknown, already filtered by the source". |
| Hero tribe impact (`tribeStats.impactAveragePosition`) | — | **Missing** in the API and in the pool. The hero pick ignores the lobby's tribes. |
| Hero combat win rate per turn (`combatWinrate`) | not loaded | API has it only per hero (`/api/bg/heroes/{dbf}` → `combat_winrate[]`, top 50% only): 116 requests of ~150 KB each per refresh. My Stats shows no "global" combat line until there is a bulk endpoint. |
| MMR floor of each percentile (`mmrPercentiles`) | pool (`/v1/meta` `percentiles`) when it has them | **Missing** in the API. Without floors the advisor cannot pick a bracket from the rating and uses the widest one (top 50%). The page lets the player pick a bracket by hand. |
| Card stats per bracket: average place of players who played the card | **API** `GET /v1/battlegrounds/minions/stats?mmr=ALL\|TOP_50_PERCENT\|TOP_20_PERCENT&period=CURRENT_BATTLEGROUNDS_PATCH&limit=500` → `normal.avg_placement_with`, `normal.games_with_minion`. Brackets: all ranks, top 50%, top 20% | Minions only (no tavern spells). Golden numbers are not used. |
| Card stats per turn played (`turnStats`) | pool (cards with turn results) | **Missing** in the API in bulk. `/v1/battlegrounds/minions/{id}/stats` has `rounds[]`/`purchases[]` per minion, mostly null per round, 270+ requests. Tavern and Discover picks get no community placement until the pool has it. |
| Comp stats (`comp-stats`): placement, per MMR, per hero | pool (`/v1/meta` comps) | **Missing**. The API has only each hero's compositions (`best_composition`, `/api/bg/heroes/{dbf}` → `compositions[]`, top 50%): no overall comp placement, no guides. Not used, per the owner's decision. |
| Comp guides (`comps-strategies`): cards core/addon, tips, when to commit, tribes, power level, difficulty | pool (`/v1/meta` guides) | **Missing** in the API. |
| Trinket stats: placement, pick rate, per MMR | pool (`/v1/stats`, `/v1/meta` trinkets) | The API has `/api/bg/trinkets?mmr=ALL\|TOP_50…TOP_1_PERCENT` (HSReplay placement, pick rate), but its `games` is "the minimum sample compatible with the rounded distribution", not a real count. **Not used**, per the owner's decision (section 3 lists it as the easy next step). |
| Tribe results of final boards | pool (`/v1/stats` tribes) | Shown instead of comps when the pool is on and there are no comps. |

Duos: the API has `/api/bg/heroes/duos` (top 50%), but the app's meta has no Duos dimension (Firestone's was solo too).

## 2. How the app loads it

- `BattlegroundsMeta.LoadAsync(HearthpulseData)` reads the four hero slices and the three minion slices through
  `HearthpulseData` (cache `cache/api/bg-heroes-<mmr>.json`, `bg-minions-<mmr>.json`, 6 h, stale cache when offline,
  404 cached as "no data"). `Source` = "Hearthpulse data" (`HearthpulseData.Attribution`). Nothing else is filled:
  comps, trinkets, percentiles and tribe impact stay empty.
- When the stats pool is set up (`PoolClient.Available`), `PoolClient.OverlayAsync` loads the pool's own meta
  (`/v1/meta` + `/v1/stats`) and lays it over the API meta: hero and card numbers blend by sample size **only when the
  API row has one** (API heroes have none, so their numbers stay HSReplay's; heroes the API lacks come from the pool
  once they reach `PoolClient.MinGames`); card turn results, comps, guides, trinkets, tribes and percentiles come from
  the pool when the API has none. `Source` then reads "… + Hearthpulse stats pool".
- Without the API (offline, no cache) the pool's meta is used alone; with neither, the pages show their empty states.

## 3. What the server could add (not done; the server is not changed from here)

All read-only, `{"data": ..., "meta": {source_id, fetched_at, stale}}` like `/v1/*`:

1. **MMR floors per percentile** - `GET /v1/battlegrounds/mmr-percentiles?mode=solo|duos`
   → `data: [{"mmr_percentile": "TOP_50_PERCENT", "min_rating": 6400}, ...]`. Lets the advisor pick the player's
   bracket from the rating again.
2. **Hero tribe impact** - add to each hero of `/api/bg/heroes` (or a new `/v1/battlegrounds/heroes/tribes?mmr=`):
   `tribe_impact: [{"race": "MURLOC", "impact_avg_placement": -0.12, "games": 1234}]` (negative = better with the tribe
   in the lobby).
3. **Hero sample size** - `num_games` on each hero row. The tier list could then hide thin samples and the pool could
   blend with the API by weight.
4. **Bulk per-turn card results** - `GET /v1/battlegrounds/minions/stats/turns?mmr=&period=` →
   `data: [{"card_id": "BG25_354", "turns": [{"turn": 5, "games": 4100, "avg_placement": 3.92}]}]`, tavern spells
   included. This is what ranks shop and Discover offers.
5. **Bulk hero combat win rate** - `GET /v1/battlegrounds/heroes/combat-winrate?mmr=` →
   `data: [{"id": "TB_BaconShop_HERO_37", "rounds": [{"combat_round": 3, "data_points": 6815, "combat_winrate": 52.47}]}]`.
6. **Compositions** - `GET /v1/battlegrounds/compositions?mmr=` → `data: [{"composition_id": 9, "name": "Demons",
   "name_ru": "Демоны", "num_games", "avg_placement", "placement_distribution", "tribes": ["DEMON"], "core_cards":
   [{"card_id", "at_least_one": 0.74}], "heroes": [{"id", "num_games", "avg_placement"}]}]`. The server already
   stores HSReplay comp datasets (`hsreplay_battlegrounds_comps`, `hsreplay_battlegrounds_compositions`) without a
   route; its `firestone_battlegrounds_comps` dataset must not feed the app. Guides (tips, when to commit) stay with the
   pool or editorial content.
7. **Trinkets with real sample sizes** - `games` as counted, not estimated; then `/api/bg/trinkets` can feed the
   trinket pick.
