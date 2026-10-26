# api.kolodahearthstone.com - Hearthstone data API

Base URL `https://api.kolodahearthstone.com`. Checked live on 2026-10-26 (first pass 2026-10-23): `GET /openapi.json`
(title "Hearthstone Data API", 0.1.0, 144 paths) and `GET /api` (name "HS Battlegrounds RU API", version 1.16.0, with an
endpoint index and totals). All endpoints below returned 200 **without** credentials unless marked otherwise. A
Russian overview of what is there and what the app could use: `<workspace>/api-kolodahearthstone-2026-10-26.md`
(sibling of the repo; optional). The app's client is
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
(422, e.g. a missing required `card`, `deck`, `archetype`+`format`, `name`, `q`, `card_name`); `/api/v1/*` ->
`{"error": {"code": "not_found" | "invalid_parameter", "message": "<Russian text>"}}`.

Freshness: `GET /v1/sources` (alias `/sources`, `/v1/system/sources`) lists every source (110 on 2026-10-26: 73
HSReplay, 22 HSGuru, 8 Firestone, Vicious Syndicate, Metastats, Blizzard, HearthArena, hearthstone-decks) with
`enabled`, `freshness_state`, `stale`, `age_hours`, `stale_after_hours`, `last_success_at`, `next_run_at`,
`completeness`, `diagnostics`, `site`, `category`, `url`. `GET /v1/health` summarises (`serving_ok`, `freshness_ok`,
`degraded`, `stale_sources[]`, `operationally_disabled_sources[]`). Raw stored answers: `GET /datasets/{source_id}`.

**Provenance caveat.** HSReplay mulligan and card-choice answers carry `premium: true` / `premium_session` and are
collected through scrape.do on a daily credit budget (`stop_reason: scrape_do_budget`); per-opponent-class splits
exist "only in premium answers". Run `hearthpulse-legal` before showing such data to players, and expect partial
coverage (`no_data`, 404, `stale`).

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
| `GET /api/bg/heroes/{dbf_id}` | - | bare (~150 KB): `hero` (list item), `mode`, `filters` {mmr_percentile, time_range}, `source_url`, `as_of` {tavern_up, hero_power, combat_winrate, ...}, `tavern_up[]` {turn, tavern_tier, occurrences, pct_at_tier, num_games}, `tavern_up_by_turn[]` {turn, recommended_tavern_tier, pct_at_tier, num_games}, `hero_power[]` {turn, tavern_tier, gold, end_of_round_median_tavern_tier, times_invoked, invoked_rate, total_data_points}, `hero_power_by_turn[]`, `combat_winrate[]` {combat_round, data_points, combat_winrate}, `compositions[]` {composition_id, name, num_games, avg_placement, placement_distribution}, `best_composition` |
| `GET /api/bg/heroes/{dbf_id}/tavern-up`, `/hero-power`, `/best-composition` | - | parts of the above |
| `GET /api/bg/trinkets` | `trinket_tier`=all, `active_only`=true, `mmr`=TOP_1_PERCENT, `timeRange`=LAST_7_DAYS | bare: {type: "bg_trinkets", count, ..., `trinkets[]` {name, dbfId, id, trinket_id (e.g. `BG35_MagicItem_701`), cost, description, type/trinket_tier ("Lesser"/"Greater"), tier, pick_rate ("6.3%"), avg_placement (string), placement_distribution[], games, games_is_minimum, localized_name, localized_description, variant_key, source_id, source_url}} |
| `GET /api/db/bg/minions`, `/api/db/bg/minions/{dbf_id}`, `.../history` | `q`, `tavern_tier`, `limit`, `offset` | stored minion DB |
| `GET /api/bg/compositions/screenshot/latest[/image]` | - | bare {ok, source_id, url, captured_at, image_mime, ...}; source marked `missing` on 2026-10-26 |

Per-minion `stats` takes `minion` = dbf id or card id, `mmr` (TOP_50_PERCENT, TOP_20_PERCENT, ALL), `period`
(LAST_7_DAYS, CURRENT_BATTLEGROUNDS_PATCH), `composition` (board positions of one comp; 0 = all) and `snapshot_id`.
`/api/bg/trinkets` had 251 trinkets; `games` is a lower bound (`games_is_minimum`), `localized_name` /
`localized_description` are Russian.

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
| `GET /v1/hsguru/archetypes/history`, `/analysis` | `archetype`*, `format`* | per archetype |
| `GET /v1/hsguru/matchups` | `format`=standard, `rank`=all (all, top_legend, top_5k, legend, diamond_to_legend, diamond_4to1, bronze_plat), `period` (from `meta.available.periods`; default `meta.available.default_period`), `coin` (only any_player), `min_games`, `min_archetype_games`, `limit`<=200 (top N archetypes) | `data` {total_games, `archetypes[]` {archetype, class, games, winrate, popularity_pct}, `matchups[]` {archetype, opponent, winrate, games}}; `meta.available` {formats, ranks, periods, coins, current_period, default_period}. `/v1/hsguru/matchups/slices`: 140 stored slices with `state`, `total_games`, `archetypes`, `cells` |
| `GET /v1/hsguru/class-matchups` (app: `LoadHsguruClassMatchupsAsync`) | `archetype`+`format` or `deck_id`; `rank`, `period`, `coin` | `data` {games, winrate, `rows[]` {opponent_class (lower case), winrate, games, share_pct}} |
| `GET /v1/constructed/hsguru-deck` | `archetype`*, `format_name`=standard, `rank`=legend | `data[]` HSGuru builds {deck_code, win_rate, games, url, sample_period, sample_rank} |
| `GET /api/hsreplay/archetypes` | `hl`=en | bare {count (778), `archetypes[]` {id, name, class, url, standard, wild}} - the whole HSReplay archetype dictionary |
| `GET /api/db/cards/trends` | `card_name`*, `source_id`, `class_name`, `limit` | stored trend rows of one card |
| `GET /v1/meta-intelligence/*` | `format`, `rank`, `period`, `patch` | archetypes, cards, signals, snapshots, budget. **401 without a token** (header `x-api-key` or Bearer) |

## Mulligan

| Endpoint | Params (default) | Response |
|---|---|---|
| `GET /v1/hsguru/mulligan` (app: `LoadHsguruMulliganAsync`) | `archetype`+`format` or `deck_id`; `rank`=all (all, diamond_4to1, diamond_to_legend, legend, top_5k, top_legend), `period`=current, `coin`=any_player (on_coin, going_first), `opponent_class`=any (upper-case class), `min_mull_count`, `min_drawn_count` | `data` {kind "cards", subject_type, games, winrate, period, snapshot_id, active, stale, source_url, `rows[]` {position, card_id, dbf_id, card_name, mulligan_impact, mulligan_count, drawn_impact, drawn_count, not_drawn_impact, not_drawn_count, kept_impact, kept_count}} (~190 KB for a big archetype). `/slices` (which slices exist), `/history` (`kind`=cards\|matchups, `card`), `/coverage` |
| `GET /v1/hsreplay/mulligan` | `deck`* (HSReplay shortid), `format`, `rank`=legend (diamond_4to1, diamond, bronze_gold), `initiative`=all (first, coin), `period`=last_30_days (current_patch, current_expansion), `opponent_class`=any (premium only) | `data` {deck {archetype_id, archetype_name, player_class, deck_list [[dbf, count]...]}, state, no_data, games, base_winrate, scope, as_of, `sample` {deck_games, deck_winrate, archetype_games, archetype_winrate, class_games, class_winrate}, opponent_classes[], premium, `rows[]` {dbf_id, card_id, card_name, keep_percentage, opening_hand_winrate, winrate_when_drawn, winrate_when_played, avg_turns_in_hand, avg_turn_played_on, opening_hand_impact, drawn_impact, played_impact}} |
| `GET /v1/hsreplay/mulligan/decks` | `format`, `rank`, `limit`<=1000 | collected decks {deck_id, archetype_id, archetype_name, player_class, total_games, win_rate, pick_reason, slices_planned, slices_with_data}. Only ~18 decks (Standard 10 popular + 2 by win rate, Wild 5 + 1; `coverage.settings`) |
| `GET /v1/hsreplay/mulligan/slices`, `/history`, `/coverage` | `deck`* (+ slice params, `card`) | stored slices, snapshots, collection runs |

No endpoint returns card **pairs** (synergies) - `GuidePair` in Mulligan v2 still has no data.

## Card choices (Discover, Adapt, Dredge, Choose One, Excavate)

| Endpoint | Params | Response |
|---|---|---|
| `GET /v1/hsreplay/card-choices/cards` | `format`, `kind` (discover, adapt, dredge, choose_one, excavate) | `data[]` (533 cards, 382 discover) {card_id, dbf_id, name, name_ru, kind, card_class, set, formats[], has_hsreplay_stats, collected[]} |
| `GET /v1/hsreplay/card-choices` | `card`* (card id or dbf), `format`, `rank`=legend (bronze_gold; diamond_4to1/diamond when enabled) | `data` {card {...text}, stats {state, tracked, as_of, stale, options_total}, `options[]` {dbf_id, card_id, name, name_ru, card_class, cost, type, rarity, popularity, win_rate, in_pool, low_sample, undervalued}, `pool` {kind, resolved, class_dependent, total, cards[] {..., has_stats, popularity, win_rate}}, available[] {format, rank}, premium_session} |
| `GET /v1/hsreplay/card-choices/coverage` | - | per format/rank `targets`, `with_data`, `no_data`; last run (budget-limited) |

## Balance, patches

| Endpoint | Params | Response |
|---|---|---|
| `GET /v1/constructed/balance-radar` | `format`=standard\|wild | `data` (~290 KB) {runId, generatedAt, summary (ru), `nerfs[]`/`buffs[]` {ref, kind (class/card), id, dbfId, name, nameEn, class, confidence, headline, verdict, reasons[], suggestedChange, stats {source, rank, period, winrate, popularity, games, topArchetypes}, social {mentions {reddit, x}, sentiment, stance, topPosts[]}, precedents[]}, patch, model, history, accuracy, changesSincePrevious}. Texts are Russian |
| `GET /v1/constructed/balance-radar/history` | `card`, `direction` (nerf, buff, rework, neutral, mixed), `class`, `since`, `limit`<=100 | coverage since 2023, `contentDrops[]`, `patches[]` {version, date, title(Ru), officialUrl, wikiUrl, manacostUrl, `changes[]` {cardId, dbfId, fields[] {field, before, after}, textBefore/After, direction}, metaBefore[]} |
| `/balance-radar/runs[/{run_id}]`, `/backtest`, `/confidence-history`, `/patterns` | `format`, `status`, `side`, `runs` | model run history |
| `GET /api/patches`, `/api/patches/{version}` | `q`, `match_state`, `include_content`, `limit`<=500 | bare {total (321), `patches[]` {version, wiki_url, official_url, official_published_at, official_summary, title (ru), source_url (hs-manacost), sections[] with `include_content`}} |

## Leaderboards (Blizzard)

`mode` = standard, wild, battlegrounds, battlegroundsduo, arena, undergroundarena, twist; `region` = EU (default), US,
AP. Daily snapshots (`day`); `rating` is null for Standard/Wild (rank only) and set for Battlegrounds and Arena.

| Endpoint | Params | Response |
|---|---|---|
| `GET /v1/leaderboards` | `mode`, `region`, `season`, `day`, `q`, `limit`<=100, `offset` | `data` {board {region, mode, season_id, season_start, is_latest, final, rated}, snapshot {day, captured_at, total_size}, days[], total, `rows[]` {rank, name, rating, previous_rank}} |
| `GET /v1/leaderboards/player` | `name`*, `region`, `mode` | one player's places |
| `GET /v1/leaderboards/players` | `q`* (name prefix), `region`, `limit`<=50 | `data[]` {region, name, current[] {mode, season_id, rank, rating}} |
| `GET /v1/leaderboards/stats` | `mode`, `region`, `season`, `bucket` | thresholds {rank 100/1000/5000, rating}, histogram, daily `series[]` |
| `GET /v1/leaderboards/seasons`, `/coverage` | - | stored seasons per region/mode; collection runs |

## Streamers, Brawl, esports (HSGuru)

| Endpoint | Params | Response |
|---|---|---|
| `GET /v1/hsguru/streamers` | `q`, `format` (standard, wild, classic, twist), `player_class`, `language` (only `ru` is read by default), `live`, `days`=30, `sort` (active, followers, decks, rank), `decks`=3, `limit`<=100 | `data[]` (153) {slug, display_name, twitch_url, youtube_url, x_url, links[], avatar_url, followers, decks_total, last_played_at, `live` {title, viewers, started_at, stream_url}, `recent_decks[]`} |
| `GET /v1/hsguru/streamers/{streamer}`, `/{streamer}/decks` | slug, Twitch login or id | profile with VODs; decks |
| `GET /v1/hsguru/streaming-now` | `language`, `mode`, `known`, `limit`<=300 | `data[]` {platform, title, viewers, mode, started_at, stream_url, login, display_name, current_deck} |
| `GET /v1/hsguru/streamer-decks` | `format`, `player_class`, `archetype`, `max_rank`, `days`=7, `limit`<=200 | `data[]` (1864) {deck_id, deck_code, archetype, player_class, format, peak_rank, wins, losses, games, winrate, last_played_at, on_current_stream, vod, streamer {...}} |
| `GET /v1/constructed/deck-radar` | `status` (candidate, confirmed), `limit` | new exact decks of top-1000 legend streamers {fingerprint, event_type, deck_code, deck_name, streamer, format, first_seen_at}; `meta.beta`, was `stale` on 2026-10-26 |
| `GET /v1/hsguru/brawl/meta` | `rank`, `coin`, `opponent_class`, `min_games`=10 | `data` {total_games, `items[]` {archetype, winrate, popularity, games, turns, duration_minutes, climbing_speed}} |
| `GET /v1/hsguru/brawl/decks` | `rank` (all, legend, diamond_to_legend), `class`, `archetype` | `data` {`items[]` {title, class, deck_code, win_rate, games, url}} |
| `GET /v1/hsguru/esports/tournaments[/{id}]`, `/upcoming` | `kind` (tour_stop, community), `limit` | {tournament_id, name, kind, start_time, tags[], players, complete, winner {name, country}, top_archetypes[] {archetype, lineups, share}}; detail adds standings and lineups |
| `GET /v1/hsguru/esports/points[/seasons]` | `season`, `leaderboard` (STD, BG_LL), `region` (EU, AM, AP, CN) | {headers[], `items[]` {rank, player {name, battletag, region, country}, <month> points, total}} |
| `GET /v1/hsguru/esports/legacy/{table}` | `table` (mt-stats, qualifier-stats, mt-points), `selector` | Masters Tour archive |

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
| `GET /api/v1/meta` | library totals (2026-10-26: BG cards 1419 / 2653 records, heroes 123, timewarped 197, libraries 711, constructed 9950 = Standard 1322 + Wild 6501, in_pool 380), creature types, mechanics, tavern tiers [1..7], counts |

`/api/v1` library items also carry `tile_layout` (ready deck-tile layout: art URL, fonts, colours, cost/title
styles). `GET /api/v1/bartender-skins[/{card_id}]` and `GET /api/v1/card-history/card/{card_id}/legacy` exist too.

Other read endpoints: `/v1/search` (`q`*, `kind`, `limit`<=50, `mode`=hybrid; items {id, kind, title, subtitle,
image_url, metadata, url -> `/v1/materials/{id}`}), `/v1/arena/classes` (`limit`<=100; {class, win_rate, pick_rate,
pct_7_plus, num_drafts, class_ru}), `/v1/system/parsing-reliability`, `/system/technologies`.

## Practical notes

- Russian is the primary language of `/api/v1` text (`text_ru`, `name_ru`, error messages); English names are in
  `name.en` where present.
- Join to the app's card data by `card_id` (HearthstoneJSON id) or `dbf`/`dbfId` (`Card.DbfId`).
- Big unpaged responses: `/api/bg/heroes` (~3 MB), `/api/bg/trinkets` (~300 KB); avoid fetching them on every start (`CardDb`
  shows the existing pattern: a file in `AppPaths.Cache` refreshed after a max age).
- Re-check `openapi.json` before relying on a parameter; the API is versioned independently of the app.
