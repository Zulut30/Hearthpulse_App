# api.kolodahearthstone.com - Hearthstone data API

Base URL `https://api.kolodahearthstone.com`. Checked with curl on 2026-10-23: `GET /openapi.json` (title
"Hearthstone Data API", 0.1.0) and `GET /api` (name "HS Battlegrounds RU API", version 1.16.0, with an endpoint index
and totals). All endpoints below returned 200 **without** credentials. The app's client is
`src/Hearthpulse.Core/Api/HearthpulseApi.cs` (`DefaultBaseUrl`, `HEARTHPULSE_API` env override for a test server) with
typed, cached calls in `HearthpulseData.cs`; which endpoints the app actually calls is listed in the
`hearthpulse-data-sources` skill (`reference/endpoints.md`).

Two API families live on the host and they differ in envelope and paging:

| Family | Paths | Envelope | Paging |
|---|---|---|---|
| Data API (in `openapi.json`) | `/v1/*`, `/api/bg/*`, `/api/db/*`, `/api/patches` | mostly `{"data": ..., "meta": {...}}`; **some are bare** (see below) | `limit` (default 100, max 500 on lists) + `offset` (max 10000) |
| Card library (listed by `GET /api`, not in openapi.json) | `/api/v1/*` | `{"data": [...], "pagination": {page, per_page, total, total_pages, has_next, has_prev}}`; single items `{"data": {...}}` | `page` + `per_page` (default 50, max 200) |

`meta` on `/v1/*` lists: `source_id`, `fetched_at` (ISO UTC), `stale` (bool), `count`, `limit`, `offset` (some add
`beta`, `serving_cached_dataset`, `cached_after_failure`, `fresh_candidate_published`). Check `stale`.
Data provenance is in `source_id`, e.g. `hsreplay_battlegrounds_hero_details`, `hsreplay_battlegrounds_minions`,
`hsreplay_battlegrounds_minion_stats`, `hsreplay_archetypes`, `hsguru_meta_matrix`.

Errors seen: `/v1/*` -> `{"detail": {"code": "INVALID_TOKEN", "message": "..."}}` (401) or FastAPI validation list
(422); `/api/v1/*` -> `{"error": {"code": "not_found" | "invalid_parameter", "message": "<Russian text>"}}`.

## Auth

- `GET /v1/auth/token` with `Authorization: Bearer <token>` or `x-api-key: <token>` returns
  `{"data": {id, name, scopes[], expires_at, rate_limit_per_minute, monthly_quota, is_legacy}}`; without a token: 401
  `INVALID_TOKEN`. Public read endpoints did not require a token when checked.
- **Off-limits**: everything under `/admin/*` (token issuing, parser control, refresh, dataset upload/rollback),
  `/ops/*`, `/ui*`, `/demo/*`, `/firecrawl/*`, `POST /v1/` and `/v1/graphql`. Never call write/admin endpoints.
- Never put secrets in query strings; send tokens in headers only.

## Battlegrounds

| Endpoint | Params (default) | Response (verified fields) |
|---|---|---|
| `GET /v1/battlegrounds/heroes` (alias `/v1/bg/heroes`) | `mode`=solo (or duos), `q`, `limit`, `offset` | `data[]`: `hero` (name), `dbfId`, `id` (card id, e.g. `TB_BaconShop_HERO_37`), `tier` (S/A/...), `pick_rate` ("55.07%"), `pick_rate_value`, `avg_placement`, `placement_distribution` (8 strings; 4 in duos), `best_composition_id`, `best_composition` {composition_id, name, num_games, avg_placement, placement_distribution, confidence_interval, popularity(_value), popularity_first_place, popularity_top_4, is_recent, num_days, lineup, final_form_minions}, `key_minions_top3[]` {id, dbfId, name, cost, type, rarity, cardClass, techLevel, isBattlegroundsPoolMinion, isBattlegroundsPoolSpell}, `anomaly_adjusted`, `detail_available` |
| `GET /v1/battlegrounds/minions` (alias `/v1/bg/minions`) | `q`, `tavern_tier` (1-7), `limit`, `offset` | `data[]`: `dbf_id`, `card_id`, `name`, `name_ru`, `tavern_tier`, `popularity`, `combat_winrate`, `impact`, `games_with_minion`, `games_without_minion`, `avg_placement_with`, `avg_placement_without`, `fetched_at`, `snapshot_id`, `run_id` |
| `GET /v1/battlegrounds/minions/stats` | `mmr`=TOP_50_PERCENT, `period`=LAST_7_DAYS, `tavern_tier`, `sort`=popularity, `limit`, `offset` | `data[]`: `dbf_id`, `card_id`, `name`, `name_ru`, `tavern_tier`, `images` {card, card_golden, art, horizontal}, `normal`/`golden` {impact, combat_winrate, popularity, games_with_minion, games_without_minion, avg_placement_with, avg_placement_without, wins, losses} |
| `GET /v1/battlegrounds/minions/stats/slices` | - | `data[]`: `mmr_percentile`, `time_range`, `snapshot_id`, `fetched_at`, `list_as_of`, `has_positions`, `minions_total`. Slices seen: mmr `ALL`, `TOP_20_PERCENT`, `TOP_50_PERCENT` x `CURRENT_BATTLEGROUNDS_PATCH`, `LAST_7_DAYS` |
| `GET /v1/battlegrounds/minions/{minion}/stats`, `.../stats/history` | `mmr`, `period`, `composition`, `snapshot_id` / `limit` | one minion, normal and golden |
| `GET /api/bg/heroes` | `mode`=solo, `q`, `mmr`=TOP_50_PERCENT | **bare**: {type: "bg_heroes", mode, count, fetched_at, filters {mmr_percentile, time_range}, source {...}, `heroes[]` (same item fields as /v1 heroes plus `adjusted_avg_placement`)}. No paging (~3 MB) |
| `GET /api/bg/heroes/duos` | `q` | bare, same shape, duos (placement_distribution has 4 entries) |
| `GET /api/bg/heroes/{dbf_id}` | - | bare: `hero`, `mode`, `filters`, `source_url`, `as_of`, `tavern_up[]`, `tavern_up_by_turn[]`, `hero_power[]`, `hero_power_by_turn[]`, `combat_winrate[]`, `compositions[]`, `best_composition` |
| `GET /api/bg/heroes/{dbf_id}/tavern-up`, `/hero-power`, `/best-composition` | - | parts of the above |
| `GET /api/bg/trinkets` | `trinket_tier`=all, `active_only`=true, `mmr`=TOP_1_PERCENT, `timeRange`=LAST_7_DAYS | bare: {type: "bg_trinkets", count, ..., `trinkets[]` {name, dbfId, id, trinket_id (e.g. `BG35_MagicItem_701`), cost, description, type/trinket_tier ("Lesser"/"Greater"), tier, pick_rate ("6.3%"), avg_placement (string), placement_distribution[], games, games_is_minimum, localized_name, localized_description, variant_key, source_id, source_url}} |
| `GET /api/db/bg/minions`, `/api/db/bg/minions/{dbf_id}`, `.../history` | `q`, `tavern_tier`, `limit`, `offset` | stored minion DB |

## Constructed

| Endpoint | Params (default) | Response |
|---|---|---|
| `GET /api/db/archetypes` | `class_name`, `q`, `rank_range`=LEGEND, `game_type`=RANKED_STANDARD, `limit`, `offset` | **bare**: {`latest_run` {source "hsreplay_archetypes", game_type, rank_range, region, summary_time_range, ...}, `total`, `limit`, `offset`, `archetypes[]` {archetype_id, name, slug, player_class, class_name, url, total_games, win_rate, pct_of_class, pct_of_total, rank_range, game_type, region, as_of_popularity, snapshot_id, run_id, fetched_at}} |
| `GET /v1/constructed/archetypes` | same | `{"data": [...same items...], "meta"}` |
| `GET /api/db/archetypes/{archetype_id}` | `rank_range`, `game_type` | archetype detail |
| `GET /api/db/archetypes/{archetype_id}/mulligan` | `rank_range`, `game_type`, `display_only`, `limit` | bare {`snapshot` {...}, `mulligan[]` {dbf_id, card_id, card_name, card_name_en, cost, card_type, rarity, card_class, times_presented_in_initial_cards, times_kept, keep_percentage, times_in_opening_hand, opening_hand_winrate, times_card_drawn, winrate_when_drawn, times_card_played, avg_turn_played_on, avg_turns_in_hand, winrate_when_played, ...}} |
| `GET /api/db/archetypes/{archetype_id}/matchups`, `/decks` (`include_cards`), `/history` | `rank_range`, `game_type`, `min_games`, `limit` | per archetype |
| `GET /api/db/decks`, `/v1/constructed/decks` | `class_name`, `format_name`, `source_id`, `min_win_rate`, `q`, `limit`, `offset` | decks |
| `GET /v1/hsguru/meta` | `format`=standard\|wild, `rank`=all (diamond, diamond_4to1, diamond_to_legend, legend, top_5k, top_legend, top_500, top_100), `period`=past_day, `coin`=any_player, `min_games`=100 | `data` {format, rank, period, coin, min_games, source_url, `items[]` {archetype, winrate, popularity, games, turns, duration_minutes, climbing_speed}}; `meta.available_periods` (e.g. past_3_hours ... past_30_days, `patch_<version>`, current expansion slug), `current_period`, `current_patch_period` |
| `GET /v1/hsguru/archetypes` | `format`=all, `q`, `min_games`=50, `has_decks`, `sort`=games (winrate, popularity, name), `order`=desc, `limit`, `offset` | `data[]` {format, format_id, archetype, games, winrate, popularity_pct, avg_turns, avg_duration_minutes, climbing_speed_stars_per_hour, period, rank, source_url, archetype_url, decks_url, `decks[]` {source_id, title, archetype, class, format, deck_code, win_rate, score, games, url, updated_at, sample_period, sample_rank}, deck_count, has_decks}, plus `criteria`, `coverage` |
| `GET /v1/hsguru/archetypes/history`, `/analysis` | `archetype`, `format` | per archetype |
| `GET /v1/meta-intelligence/*` | `format`, `rank`, `period`, `patch` | archetypes, cards, signals, snapshots |

## Cards and Battlegrounds libraries (`/api/v1`, page/per_page)

| Endpoint | Notes / item fields |
|---|---|
| `GET /api/v1/cards` (`q`, `card_type=spell`, `include=wiki`, `page`, `per_page`) | BG card library (total 1419): `id`, `card_id`, `dbf`, `variant` {kind "base", base_card_id, base_dbf, premium_dbf}, `card_type` {slug, name_ru}, `name` {ru, en}, `tavern_tier`, `creature_type` {slug e.g. "undead"}, `attack`, `health`, `in_pool`, `duos_only`, `mechanics[]` {slug e.g. "DEATHRATTLE"}, `text_ru`, `tile_image_url`, `images`, `wiki_page`, `golden_variant` {card_id, dbf, name, attack, health, mechanics, text_ru, images...}. `tavern_tier` is not a filter here |
| `GET /api/v1/cards/{card_id}`, `/cards/by-dbf/{dbf}`, `/cards/{card_id}/wiki` | single card `{"data": {...}}` (e.g. `BG28_300` -> Harmless Bonehead, dbf 104551, premium_dbf 104554) |
| `GET /api/v1/card-catalog`, `/card-catalog/{card_id}`, `/card-catalog/dbf/{dbf}` | full HearthstoneJSON-based catalog (total ~36k): `card_id`, `dbf`, `collectible`, `formats`, `name` {ru,en}, `text`, `flavor`, `card_type`, `card_set`, `class`, `rarity`, `mana_cost`, `attack`, `health`, `durability`, `armor`, `mechanics[]`, `images`, `source`, `catalog_only`, `hearthstonejson` {ruRU, enUS} |
| `GET /api/v1/heroes`, `/heroes/{card_id}`, `/heroes/by-dbf/{dbf}` | BG heroes (123): `card_id`, `dbf`, `hero_id`, `name`, `health`, `armor`, `race`, `hero_power` {...}, `buddy` {...}, `wiki`, `images` |
| `GET /api/v1/timewarped-cards[/{card_id}\|/by-dbf/{dbf}]` | Timewarped Tavern cards: `category`, `card_type`, `tavern_tier`, `cost`, `attack`, `health`, `minion_type`, `race`, `golden`, ... |
| `GET /api/v1/trinkets`, `/anomalies`, `/quests`, `/dark-gifts`, `/darkmoon-prizes`, `/rewards`, `/libraries/{library}` | library items: `library` {slug}, `card_id`, `dbf`, `name`, `text`, `card_type` {slug, id} (e.g. `BATTLEGROUND_ANOMALY`), `mana_cost` (trinket cost), `in_pool`, `pool_status` ("available"), `group` {slug e.g. "lesser"}, `tier`, `images`, `full_art`, `artist`, `wiki_page`. Seen: trinket `BG30_MagicItem_433`, anomaly `BGDUO_Anomaly_005`, quest `BG24_Quest_114` |
| `GET /api/v1/constructed-cards?format=standard\|wild`, `/{card_id}`, `/by-dbf/{dbf}`, `/{card_id}/wiki` | Standard/Wild collectible cards |
| `GET /api/v1/hero-skins`, `/pets`, `/coins`, `/bartender-skins`, `/diamond-cards` | cosmetics |
| `GET /api/v1/card-history/{entity}/{card_id}` | stored versions of a card |
| `GET /api/v1/meta` | library totals, creature types, mechanics, tavern tiers [1..7], counts |

Other read endpoints: `/v1/search` (`q`, `kind`, `limit`<=50, `mode`=hybrid), `/api/patches` (`q`, `match_state`,
`include_content`, `limit`, `offset`), `/api/patches/{version}`, `/v1/health`, `/v1/sources`, `/v1/datasets`,
`/v1/arena/classes`, `/v1/constructed/deck-radar`, `/v1/constructed/hsguru-deck`, `/api/db/cards/trends`.

## Practical notes

- Russian is the primary language of `/api/v1` text (`text_ru`, `name_ru`, error messages); English names are in
  `name.en` where present.
- Join to the app's card data by `card_id` (HearthstoneJSON id) or `dbf`/`dbfId` (`Card.DbfId`).
- Big unpaged responses: `/api/bg/heroes` (~3 MB), `/api/bg/trinkets` (~300 KB); avoid fetching them on every start (`CardDb`
  shows the existing pattern: a file in `AppPaths.Cache` refreshed after a max age).
- Re-check `openapi.json` before relying on a parameter; the API is versioned independently of the app.
