# Endpoints Hearthpulse calls (checked in code on main 950e0ab, 2026-10-26)

Paths are relative to each base URL. Cache files live under the data folder (`AppPaths.Cache` = `<data>/cache`).

## Hearthpulse data API - `https://api.kolodahearthstone.com/`

Transport: `HearthpulseApi(HttpClient http, string? baseUrl = null)` - base URL from the ctor, else `HEARTHPULSE_API`,
else `DefaultBaseUrl`; `GetAsync(path, bearer?, ct)` only (no POST: nothing sends to this API); `Accept: application/json`;
20 s per call (on the client's `Time`, a test clock in tests); result `ApiResult { Data, Meta, Error, Ok }`.

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
| `v1/hsguru/mulligan?archetype=&format=&rank=&coin=&opponent_class=` (one slice sets the coin *or* the class; 404 = not collected) | `hsguru-mulligan-{format}-{hash}-{rank}-{coin}-{opponent}.json` | `LoadHsguruMulliganAsync` -> `MulliganGuideSource` slices. Ranks: the mulligan filter's (`AppSettings.MulliganRank`, default Legend) via `MulliganGuide.SourceRank`: legend, `diamond_4to1` (Diamond), `top_5k` (Top Legend: `top_legend` is not collected, 404), all; Legend and all ranks at Legend. Below/above Legend `LoadRankSliceAsync` reads the all-games slice as the guide's numbers (no HSReplay) |
| `v1/hsguru/class-matchups?archetype=&format=&rank=` (the mulligan rank's slice ranks, then all ranks; rows' `opponent_class` lower case -> upper case; 404 = not collected) | `hsguru-class-matchups-{format}-{hash}-{rank}.json` | `LoadHsguruClassMatchupsAsync` -> `MulliganGuideSource.WithClassMatchupsAsync`: class shift of the baseline when the HSReplay archetype has no matchups |
| `api/v1/constructed-cards/{cardId}/wiki` (`CardIdKey` `^[A-Za-z0-9_]{1,48}$`) | `card-wiki-{cardId}.json` | `LoadCardWikiAsync` -> `WikiPools` (Discover pools) |
| `api/v1/constructed-cards?format=wild&per_page=200&page=N` (<= 80 pages) | `cache/api/card-pool.json`, 1 day | `CardPoolCatalog` |
| `datasets/hsreplay_arena_cards_advanced` (envelope `data.structured.cards[]`, ~1.7 MB, list repeated in `hsreplay_extracted`) | `arena-cards.json`, trimmed to id/dbfId/score/win_rate/pick_rate/times_played (`ShrinkArenaCards`) | `HearthpulseData.LoadArenaMetaAsync` -> `ArenaMeta` (ArenaSmith `score`; one all-classes slice, Underground, last 4 days) |
| `v1/arena/classes?limit=20` | `arena-classes.json` | `ArenaMeta.Classes` (win rate, pick rate, 7+ share, drafts) |
| hs-manacost.ru WordPress REST `wp-json/wp/v2/posts?categories=1036…`, `wp-json/wp/v2/media/{id}` (the owner's site, not our API; `ManacostArticles`, own HttpClient call) | `manacost-brawl.json`, 6 h; cover `manacost-cover-{media}.jpg` | The Brawl page's article card (`docs/brawl.md`): the week's "Потасовка недели" post and deck collection; links and covers only on `hs-manacost.ru` over https |
| `v1/hsguru/brawl/meta` (all ranks) | `hsguru-brawl-meta.json` (its time = `BrawlMeta.FetchedAt`) | `HearthpulseData.LoadBrawlMetaAsync` -> `BrawlStats.ByClass` -> the Brawl page (`docs/brawl.md`): archetypes summed into classes, nameless / rateless / unclassed archetypes as other games |
| `v1/hsreplay/card-choices/cards` | `card-choices-cards.json`, trimmed to card_id/dbf_id/formats/has_hsreplay_stats | `LoadChoiceCardsAsync` -> `DiscoverChoiceSource` (which cards have numbers, by card id or dbfId) |
| `v1/hsreplay/card-choices?card=&format=&rank=legend` (`CardIdKey`; `card` takes a card id or a dbfId) | `card-choices-{format}-{rank}-{cardId}.json`, trimmed to card/format/rank/stats.state+as_of/options (card_id, dbf_id, popularity, win_rate, low_sample) | `LoadCardChoicesAsync` -> `DiscoverPicks.Build`: the card pool panel's pick hint (percent -> share; `stats.state` `not_collected` = no options, no hint) |

Not used: `/v1/meta-intelligence/*` (needs a token). **Not to be used**: `v1/hsreplay/mulligan` - HSReplay Premium data,
a Blocker (L-B1) in `../reference/reviews/LEGAL-HeartLens-card-choices.md`: HearthSim's terms forbid it without written
permission. `v1/hsreplay/card-choices` falls under the same Blocker, but **the owner accepted that risk on 2026-10-27**
("что берут на раскопках брать с api"): the Discover pick hint reads it, shown as "Hearthpulse data". The acceptance
covers card choices only, not the per-deck mulligan.
The two Arena endpoints above (ArenaSmith scores, class results) are HSReplay data too: the owner decided on
2026-10-27 to use them for the Arena draft helper and accepted the legal risk (`docs/arena-draft.md`, Legal status).
Available without a token but not called yet (fields in
[kolodahearthstone-api.md](../../hearthstone-context/reference/kolodahearthstone-api.md)): HSGuru
matchup matrix `v1/hsguru/matchups`, BG hero detail `api/bg/heroes/{dbf}` and trinkets
`api/bg/trinkets`, Blizzard leaderboards `v1/leaderboards*`, balance radar, patches, streamers, Brawl decks
`v1/hsguru/brawl/decks`, esports.
Premium-sourced HSReplay data needs a `hearthpulse-legal` check before it reaches players.
Docs: `docs/constructed-meta-api.md`, `docs/mulligan-v2.md`,
`docs/discover-pool.md`, `docs/arena-draft.md` (Arena; endpoints the server lacks: per-class scores, normal vs
Underground slice). Server map (owner's optional notes): `<repo>/../reference/server-151.md`.
Resolve the repository root and check this sibling file exists before consulting it. Endpoint examples here are
dated observations; check the current service contract before relying on changing fields or limits.

## hearthpulse.net - `https://hearthpulse.net/api/v1/`

`AccountClient(HttpClient http, string? baseUrl = null)`, `ClientId = "manacost-tracker"`, form-encoded bodies.

| Call | Notes |
|---|---|
| `POST oauth/device/code` | `client_id`, `scope`; defaults `expires_in` 600, `interval` 5 |
| `POST oauth/token` | `grant_type=urn:ietf:params:oauth:grant-type:device_code` or `refresh_token`; refresh rotates |
| `POST oauth/revoke` | refresh token only |
| `GET me` (Bearer) | `user{id,name,profileUrl}`, `subscription` -> `Entitlements`; e-mail is never read or stored |
| `POST tracker/events/batch` | finished games for the profile's "My statistics" (`ProfileUploader`, `docs/profile-upload.md`) |

Approval page shows the client name "Manacost Tracker"; `/connect?user_code=` pre-fills the code. Staging identity:
`test.hearthpulse.net` (set `HEARTHPULSE_SITE_API` to try it).

## HearthstoneJSON

- `https://api.hearthstonejson.com/v1/latest/{locale}/cards.json` -> `cache/cards.{locale}.json`, 2 days (`CardDb`).
- `https://api.hearthstonejson.com/v1/latest/enUS/cards.collectible.json` -> `cache/cards.enUS.collectible.json`,
  2 days (card pool fallback).
- Art (`CardArt`, `CardRender` in both UIs; addresses, cache files and downloads in `Core/Cards/CardImageFiles.cs`): `https://art.hearthstonejson.com/v1/tiles/{id}.png`, `v1/256x/{id}.jpg`,
  `v1/512x/{id}.jpg` (WPF), `v1/render/latest/{locale}/256x/{id}.png`, `v1/bgs/latest/{locale}/256x/{id}.png`.
- Generators read the enUS dump you download yourself: `tools/counters/gen_counter_cards.py`,
  `tools/cardpool/gen_pool_tables.py`, `tools/effects/*.py`.

## hsbg.cards

`https://hsbg.cards/api/v1/cards?limit={0}&offset={1}&pool=current`, 100 per page, <= 20 pages, cache
`cache/bgcards.hsbg.json` 12 h (`BgCardDb`).

## Hearthpulse archetype dictionary (blizzcore)

`GET https://api.blizzcore.ru/archetypes?limit=1000` -> `{"count", "success", "items": [{"id", "name_en", "name_ru",
"created_at", "updated_at"}]}`, sorted by `name_en`. Checked Oct 2026: without `limit` it answers 200 items (`count` is
the size of the answer, not the total - 253 existed); `offset`, `skip`, `page`, `per_page` are ignored, so ask for one
big page (`ArchetypeTranslations.Limit`; an answer that long is logged as possibly cut). Some `name_en` use
typographic apostrophes (`Mecha’thun Druid`) and a few are Russian - matching ignores case, spacing and apostrophe
style. Our own service (deckview on the owner's server), so no third-party attribution; the installer notice names it as
a Hearthpulse service. `ArchetypeTranslations` (12 h cache in `cache/api/blizzcore-archetypes.json`, stale copy
offline, 5 s timeout, 1 MB answer limit, failed fetches back off 1, 2, 4, 6 h via `blizzcore-archetypes.failed`, never
throws; `name_ru` cut to 40 characters, markup/control characters/no letters dropped);
labels fall back to the built-in `ArchetypeNames.RussianLabel`, then the English name. Tests:
`ArchetypeTranslationsTests` (fake handler).

## Battlegrounds meta - data API

`BattlegroundsMeta.LoadAsync(HearthpulseData)`, cache `cache/api/bg-heroes-<mmr>.json` and `bg-minions-<mmr>.json`, 6 h:

- `api/bg/heroes?mode=solo&mmr={TOP_50_PERCENT|TOP_20_PERCENT|TOP_5_PERCENT|TOP_1_PERCENT}` (bare answer; no `ALL`)
- `v1/battlegrounds/minions/stats?mmr={ALL|TOP_50_PERCENT|TOP_20_PERCENT}&period=CURRENT_BATTLEGROUNDS_PATCH&limit=500`

Comps, trinkets, tribe impact, turn results and MMR floors come only from the stats pool (`PoolClient.OverlayAsync`).
Firestone (`static.zerotoheroes.com`) is not used. Details: `docs/bg-meta-sources.md`.

## Battlegrounds tabs - the site's open service `https://bg.hs-manacost.ru/`

`BgSiteClient(HttpClient http, string? baseUrl = null, string? cacheDirectory = null).LoadAsync()` ->
`BgSiteStats` (`Core/Battlegrounds/Site`), for the main window's Battlegrounds tabs only (owner's decision,
2026-11-09: the lists hearthpulse.net shows; the advisor and overlay keep `BattlegroundsMeta`). Both hosts load it
beside the community stats (`LoadBgSiteAsync`) and hand the result to `MainViewModel.SetBgSiteStats`; the tabs name
the source "Hearthpulse data" with the list's slice and date. Pass it `BgSiteClient.CreateHttp()`: no cookies, no
redirects, no default headers. Plain `GET`, nothing about the player; 20 s and 8 MB per answer; never throws. Cache
`cache/api/bgsite-<part>.json`, 6 h; the old file answers when the server fails **or answers without data** (`{}`,
`ok: false`, `view.blocked`, empty lists). Fields, sizes, sources and probes: `docs/bg-site-stats.md`; tests
`BgSiteStatsTests` (fixtures `Fixtures/BgSite`).

| Call | Cache file | Gives |
|---|---|---|
| `api/bg/tier-lists` (950 KB) | `bgsite-tier-lists.json` | tier lists S-D: `heroes`, `minions`, `trinkets` (top 1%) |
| `api/bg/heroes` | `bgsite-heroes.json` | hero card ids by dbfId, top 4 / 1st place, the MMR slice (top 50%) |
| `api/bg/heroes?mode=duos&mmr=TOP_50_PERCENT` | `bgsite-heroes-duos-top_50_percent.json` | `HeroesDuos`; asked only after `api/bg/heroes` named the allowed source |
| `api/bg/library/minion-stats` | `bgsite-minion-stats.json` | minion impact, combat win rate, popularity, placement with / without (one slice; `?mmr=` is ignored) |
| `api/bg-comps` | `bgsite-comps.json` | comps (only those whose own `source` is `HSReplay`), `CompsFetchedAt` |
| on demand: `api/bg/heroes?mode=solo\|duos&mmr=<slice>` (`LoadHeroesAsync`) | `bgsite-heroes-<mode>-<mmr>.json` | a heroes slice: solo top 50 / 20 / 5 / 1 %, Duos top 50 / 20 % (`BgSiteClient.HeroesSoloSlices`, `HeroesDuosSlices`) |
| on demand: `api/bg/tier-lists?list=trinkets&mmr=<slice>&timeRange=LAST_7_DAYS` (`LoadTrinketsAsync`) | `bgsite-trinkets-<mmr>.json` | a trinket slice: top 50 / 20 / 5 / 1 %, default top 1 % (`BgSiteClient.TrinketSlices`) |

**Allow list of sources** (legal L-M2): only `hsreplay`, `hsreplay_battlegrounds_heroes`,
`hsreplay_battlegrounds_minions` (case and spaces ignored); no source or any other value is refused, and so is an
allowed source whose `upstreamSource` / `sourceUrl` names Firestone or zerotoheroes. Refused data is cut before the
answer is cached: `lists.spells`, `lists.strategies`, the Firestone comps (comps are checked one by one). Not
requested: `api/bg/library/spell-stats` (Firestone), `api/bg/library/meta`, `api/bg/library/cards`. A new origin is a
new value in `BgSiteParser.AllowedSources` after a `hearthpulse-legal` check. Heroes slices name no source: one is
read only when `api/bg/heroes` without parameters names the allowed source (fetched in this load, or its cache file
within 6 h) and the slice says `ok: true`, `view.type` `bg_heroes`, and the mode and slice asked for (coordinator's decision 2026-11-10, pending legal; `docs/bg-site-stats.md` §4,
rule 6). A slice that is not listed, or that the service does not answer for, is `null`, not an error.

## Hearthpulse stats pool - `PoolClient.Endpoint` (server/pool, off until deployed)

- `POST v1/games` - shared Battlegrounds games (`PoolClient.SubmitAsync`, WPF only).
- `POST v1/constructed` - Discover-style picks of shared Ranked/Casual games, consent version 4
  (`PoolClient.SubmitConstructedAsync`, `ConstructedPoolGame`; WPF only).
- `GET v1/stats`, `v1/meta` - Battlegrounds totals and base stats (`OverlayAsync`).
- `GET v1/choices?format=standard|wild` - pick totals of shared games; the server still serves it, but since
  2026-10-27 the app does not read it: the pick hint comes from the data API's `v1/hsreplay/card-choices` (above).
  Contract: `server/pool/README.md`.

## GitHub

`https://api.github.com/repos/Zulut30/Hearthpulse_App/releases/latest` - see `hearthpulse-release`.
