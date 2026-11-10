# Battlegrounds tabs: statistics from the site's open service

The main window's Battlegrounds tabs (Heroes, Trinkets, Cards, Comps) are to show the lists hearthpulse.net shows.
The site proxies an open service, `https://bg.hs-manacost.ru`, and the app reads that service directly (owner's
decision, 2026-11-09). This document is the contract of the Core client. **The tabs switch to it in package PC2 of
TASK-2026-11-09-light-ui-site-stats; until that is merged they still read `BattlegroundsMeta`**, and no host calls
the client. The in-game advisor and the overlay do not use it at all: they keep `BattlegroundsMeta`
([bg-meta-sources.md](bg-meta-sources.md)).

Answers were captured on **2026-11-09 23:06 UTC** with one plain `GET` per address, no sign-in, no cookie; five more
probes for Duos and MMR slices on 2026-11-09 23:35 UTC and six on 23:58 UTC (section 2). Trimmed copies are the test fixtures in
`tests/Hearthpulse.Tests/Fixtures/BgSite/` (its `README.md` says what was cut).

Code: `src/Hearthpulse.Core/Battlegrounds/Site/` (`BgSiteClient`, `BgSiteStats`). Tests: `BgSiteStatsTests`.

## 1. Rules of the client

- Only `GET https://bg.hs-manacost.ru/...`. No `Authorization`, no cookie, no account token, nothing about the player
  in the request; the only header the client adds is `Accept`. Never hearthpulse.net (it answers 401 without sign-in).
- The hosts pass the client `BgSiteClient.CreateHttp()`: its handler keeps and sends no cookies (`UseCookies = false`)
  and follows no redirects (`AllowAutoRedirect = false`), and it has no default headers. Whatever client is passed, an
  answer that reports another scheme, host or port than the service's (a followed redirect) is not read, and a 3xx
  answer is a failed load.
- Five requests per refresh (section 2, "read"), each one used; the limit is 8. The refresh reads every list in its
  default slice; another slice is read on demand, one request, cached under its own mode and slice.
- Cache `<data>/cache/api/bgsite-<part>.json`, 6 h, written through a temporary file. A failed request falls back to
  the old file, however old; the list's own `fetchedAt` goes to the UI so it can say how old the numbers are.
- **An answer without data is a failed load**, whatever its status: `{}`, a maintenance note, empty lists, the
  service's own `ok: false` or `view.blocked: true`, or an answer with nothing left after the source check. It is not
  written to the cache, the old file answers, and without an old file the part is missing. A cache file without data
  is asked for again. This holds for `/api/bg/heroes` like for any address: an answer of it that says nothing is a
  failed load of the base answer (section 4, rule 6, "failed load"), not a statement about the source.
- **A partly empty `/api/bg/tier-lists` is a failed load too**: when the answer comes without the rows of a list the
  old file has (heroes, minions, trinkets: the list is missing or its tiers are empty), the old file is kept whole and
  answers for this refresh. A list that is cut because the service now names another source for it is not "lost":
  that answer is taken, without the list. Without an old file a partial answer is taken as it is.
- **A slice must be the slice that was asked for.** Where the answer names it (`view.mode` and
  `view.filters.mmr_percentile` of a heroes slice; `list` and `mmr` of a trinket slice), a different mode, list or MMR
  slice is "no data": the service can ignore a parameter and send its default (it does for `minion-stats`), and that
  must not be cached and shown under another slice's name.
- Parts load independently: one address failing leaves its part `null` / empty and the others loaded. When nothing to
  show could be had, from the service or the cache, `LoadAsync` returns `null` (the tabs say "unavailable"), never an
  empty `BgSiteStats`. The answer of `/api/bg/heroes` alone is nothing to show: it only gives card ids to the tier
  list and vouches for the slices.
- An answer over 8 MB, unreadable JSON, an unknown tier letter or an entry without a card id is dropped with a line in
  the log (`Diag`). Nothing throws: any exception of the request or the body (a broken compressed answer, a disposed
  client) is a failed load like a network error. A cancelled load returns what the cache has and writes no failure.
- **Only data whose `source` is on the allow list is read** (section 4). Everything else is cut from the answer before
  it is cached, so it is not kept on the player's disk either.
- `LoadAsync` and the slice loads run off the caller's thread, also when every cache file is fresh.
- The source's name in the interface is PC2's: `HearthpulseData.Attribution` ("Hearthpulse data"), subject to the legal
  report's conditions on naming the origin.

## 2. Addresses

Sizes are of the captured answers. Server headers on all of them: `Cache-Control: public, max-age=300,
stale-while-revalidate=900`, a weak `etag`, `access-control-allow-origin: *`.

| Address | Size | Read | `source` (verbatim) | Cache file | Tab |
|---|---|---|---|---|---|
| `/api/bg/tier-lists` | 950 KB | yes | per list, below | `bgsite-tier-lists.json` | Heroes, Trinkets, Cards |
| `/api/bg/heroes` | 125 KB | yes | `source_id`: `hsreplay_battlegrounds_heroes`, `site`: `hsreplay` | `bgsite-heroes.json` | Heroes (card id, top 4, 1st place, MMR slice) |
| `/api/bg/heroes?mode=duos&mmr=TOP_50_PERCENT` | 128 KB | yes | **none**: the answer has no `source_id`, `site` or `url` | `bgsite-heroes-duos-top_50_percent.json` | Heroes, Duos (asked only after `/api/bg/heroes` named its source, section 4) |
| `/api/bg/library/minion-stats` | 100 KB | yes | `latest_run.source`: `hsreplay_battlegrounds_minions` | `bgsite-minion-stats.json` | Cards (minions) |
| `/api/bg-comps` | 109 KB | yes | `HSReplay + Firestone через api.kolodahearthstone.com`; per comp `source`: `HSReplay` (33) or `Firestone` (27) | `bgsite-comps.json` | Comps |
| `/api/bg/library/spell-stats` | 24 KB | **no** | `source_id`: `firestone_battlegrounds_spells`, `site`: `firestone` | — | — (Firestone, section 4) |
| `/api/bg/library/meta` | 3.5 KB | no | none | — | — (card counts and filter names of the site's library; the tabs need none of it) |
| `/api/bg/library/cards` | 1.9 MB | no | not captured: the address was never requested | — | — (names, tavern tier and art come from `CardDb`) |

Lists inside `/api/bg/tier-lists` (`lists.<name>`):

| List | `source` | `upstreamSource` | Entries | Slice fields | Used |
|---|---|---|---|---|---|
| `heroes` | `hsreplay` | `HSReplay через api.kolodahearthstone.com` | 116 (S 0, A 28, B 24, C 40, D 24) | none here; see `/api/bg/heroes` | yes |
| `minions` | `hsreplay` | `HSReplay через api.kolodahearthstone.com` | 255 (S 20, A 43, B 77, C 76, D 39) | none | yes |
| `trinkets` | `hsreplay` | `HSReplay trinkets + Blizzard card images` | 253 (S 18, A 68, B 44, C 56, D 67) | `mmr`: `TOP_1_PERCENT`, `timeRange`: `LAST_7_DAYS`, `gamesNote` | yes |
| `spells` | `firestone` | `Firestone через api.kolodahearthstone.com` | 72 | none | **no** (section 4) |
| `strategies` | `firestone` | `Firestone через api.kolodahearthstone.com + фреймы api.kolodahearthstone.com` | 27 | none | **no** (section 4) |

The answer's top level also has `generatedAt`, `tier` (null) and `strategySource`: `firestone`.

### Duos and MMR slices

The first capture asked every address without parameters and concluded "no Duos, one MMR slice". That was wrong: the
site asks the same service with query parameters. Probes on 2026-11-09 23:35 UTC, one `GET` each:

| Request | Answer | Shows |
|---|---|---|
| `/api/bg/heroes?mode=duos&mmr=TOP_50_PERCENT` | 200, 127 584 bytes. `view.mode`: `duos`, `view.filters.mmr_percentile`: `TOP_50_PERCENT`, 115 heroes, tiers A 32 / B 29 / C 27 / D 27, `placement_distribution` of **4** places, `fetched_at` 03:35 UTC | **A Duos slice exists.** |
| `/api/bg/heroes?mode=solo&mmr=TOP_20_PERCENT` | 200, 198 410 bytes. `view.mode`: `solo`, `mmr_percentile`: `TOP_20_PERCENT`, 116 heroes, tiers S 1 / A 31 / B 27 / C 34 / D 23; no hero has the average place of the default answer | **Heroes have MMR slices.** |
| `/api/bg/tier-lists?list=trinkets&mmr=TOP_50_PERCENT&timeRange=LAST_7_DAYS` | 200, 333 536 bytes. One flat list (the fields of `lists.trinkets` at the top level), `source`: `hsreplay`, `mmr`: `TOP_50_PERCENT`, 256 trinkets, S 13 / A 79 / B 39 / C 61 / D 64 | **Trinkets have MMR slices**, and `?list=` returns one list without the others. |
| `/api/bg/library/minion-stats?mmr=TOP_20_PERCENT` | 200, 99 818 bytes, byte for byte the answer without the parameter (`latest_run.mmr_percentile`: `TOP_50_PERCENT`) | **Minion stats have one slice**; the parameter is ignored. |
| `/api/bg/tier-lists?list=heroes&mode=duos&mmr=TOP_50_PERCENT` | 400 `{"error": "Unknown BG tier list"}` | There is no heroes tier list by `?list=heroes`; Duos comes from `/api/bg/heroes` only. |

Second round, 2026-11-09 23:58 UTC, one `GET` each, for the slice names not yet seen:

| Request | Answer | Shows |
|---|---|---|
| `/api/bg/heroes?mode=solo&mmr=TOP_5_PERCENT` | 200, 163 995 bytes; 116 heroes, 103 with a tier (S 2 / A 25 / B 28 / C 28 / D 20), 13 with `tier` null and an empty distribution | the slice exists |
| `/api/bg/heroes?mode=solo&mmr=TOP_1_PERCENT` | 200, 95 850 bytes; 85 heroes, 47 with a tier (S 1 / A 4 / B 17 / C 16 / D 9), 38 without | the slice exists, thinner |
| `/api/bg/heroes?mode=duos&mmr=TOP_20_PERCENT` | 200, 127 586 bytes; `mode` `duos`, 115 heroes (A 35 / B 23 / C 41 / D 16) | Duos has MMR slices too |
| `/api/bg/heroes?mode=duos` | 200, 127 584 bytes; `mmr_percentile` `TOP_50_PERCENT`, same `fetched_at` and numbers as `mode=duos&mmr=TOP_50_PERCENT` | the Duos default is top 50% |
| `/api/bg/tier-lists?list=trinkets&mmr=TOP_20_PERCENT&timeRange=LAST_7_DAYS` | 200, 333 439 bytes; `source` `hsreplay`, `mmr` `TOP_20_PERCENT`, 256 trinkets | the slice exists |
| `/api/bg/tier-lists?list=trinkets&mmr=TOP_5_PERCENT&timeRange=LAST_7_DAYS` | 200, 332 071 bytes; `source` `hsreplay`, `mmr` `TOP_5_PERCENT`, 255 trinkets | the slice exists |

**Slices the client supports** (`BgSiteClient.HeroesSoloSlices`, `HeroesDuosSlices`, `TrinketSlices`, each a
`BgSiteSlices(Mmr, Default)`):

| List | Slices (`mmr`) | Default of the service | In `BgSiteStats` (refresh) | Other slices |
|---|---|---|---|---|
| Heroes, solo | `TOP_50_PERCENT`, `TOP_20_PERCENT`, `TOP_5_PERCENT`, `TOP_1_PERCENT` (current patch) | `TOP_50_PERCENT` | `Heroes` | `LoadHeroesAsync(duos: false, mmr)` |
| Heroes, Duos | `TOP_50_PERCENT`, `TOP_20_PERCENT` (current patch) | `TOP_50_PERCENT` | `HeroesDuos` | `LoadHeroesAsync(duos: true, mmr)` |
| Trinkets | `TOP_50_PERCENT`, `TOP_20_PERCENT`, `TOP_5_PERCENT`, `TOP_1_PERCENT` (last 7 days) | `TOP_1_PERCENT` | `Trinkets` | `LoadTrinketsAsync(mmr)` |
| Minions (tier list and `MinionStats`) | one: `TOP_50_PERCENT`, current patch (`BgSiteStats.Run`) | the same | `Minions`, `MinionStats` | none: the `mmr` parameter is ignored |
| Comps | one, no slice named | | `Comps` | none |

- Every slice in the table was seen answering, except Duos `TOP_5_PERCENT` and `TOP_1_PERCENT`: not probed, so not
  listed (the site's page offers them; adding one is a value in `HeroesDuosSlices` after a probe). Trinkets
  `TOP_1_PERCENT` was seen as the default list of `/api/bg/tier-lists`, not by `?list=trinkets&mmr=TOP_1_PERCENT`.
- The default slice asked on demand is served from the refresh's own answers, with no other address: solo heroes
  `TOP_50_PERCENT` is the site's tier list (`lists.heroes` + `/api/bg/heroes`), trinkets `TOP_1_PERCENT` is
  `lists.trinkets`.
- **"No data for the slice" is not an error.** A slice name that is not in the table returns `null` without a request;
  a listed slice the service does not answer for (4xx, 5xx, no rows) returns `null`, and the refresh and the other
  lists are not affected.
- In the high solo slices the service has no tier, placement or distribution for heroes with too few games (13 of 116
  in the top 5%, 38 of 85 in the top 1%): they are left out of the list, so a slice can be much shorter than top 50%.
- The solo default list and the solo slices differ in shape: the default is the site's tier list (Russian hero
  names, card ids from a second answer), a slice is the heroes answer itself (English names, its own `tier`).

**The sliced heroes answer names no source.** `/api/bg/heroes` without parameters carries `source_id`:
`hsreplay_battlegrounds_heroes`, `site`, `url`, `backend`; with `mode` and / or `mmr` the answer has only `ok`,
`fetched_at` and `view{type, mode, heroes, filters}`: the words "hsreplay" and "firestone" do not occur in it at all.
How such a slice is accepted is rule 6 of section 4.

The minions tier list (`lists.minions`) names no slice of its own. `BgSiteStats.Run` is the slice of `minion-stats`,
a separate run of the server; that the tier list is the same slice is likely but not stated by the service.

## 3. Fields read

Every list of `/api/bg/tier-lists`: `source` string, `fetchedAt` ISO 8601 with offset, `availableTiers` string[]
(`["S","A","B","C","D"]`), `tiers` object of arrays by letter, `tierCounts` object of ints (equals the array lengths),
`count` int. Each entry repeats its letter in `tier`; the bucket's key is what the app trusts. Percent strings look
like `"78.10%"`; the app stores shares 0..1.

### `lists.heroes.tiers.<L>[]` → `BgSiteTierEntry`

| Field | Type | Example | Goes to |
|---|---|---|---|
| `dbfId` | int | `132608` | `DbfId` |
| `hero` | string, Russian name | `"Повелитель кошмаров Ксавий"` | `NameRu` |
| `avgPlacement` | string | `"4.24"` | `AveragePlacement` |
| `pickRate` | string, percent | `"78.10%"` | `PickRate` |
| `tier` | string | `"A"` | (the bucket key is used) |
| `bestComp` | string | `""` in all 116 | not read |
| `image` | URL | | not read |

There is no card id and no English name here. The card id comes from `/api/bg/heroes` by `dbfId`; a hero that
answer does not have is dropped (the tabs key everything by card id). `NameEn` is empty: the UI takes the name from
`CardDb`. `Games` is `null` (not published).

### `/api/bg/heroes` → card id, top 4, 1st place, slice

Top level: `source_id`, `ok` bool, `site`, `category`, `url` (`https://hsreplay.net/battlegrounds/heroes/`),
`fetched_at`, `backend` (`hsreplay_json_api`), `status` (the scraper's own report, not read), `view`.

`view`: `type` / `kind` `bg_heroes`, `title` (`HSReplay premium Battlegrounds heroes tier list.`), `blocked` bool,
`filters{mmr_percentile, time_range}` → `BgSiteTierList.Mmr`, `TimeRange` of the heroes list, `source{backend, status,
url, …}` (`url`: `https://hsreplay.net/api/v1/battlegrounds/heroes/?BattlegroundsMMRPercentile=TOP_50_PERCENT&BattlegroundsTimeRange=CURRENT_BATTLEGROUNDS_PATCH&format=json`),
`heroes[116]`:

| Field | Type | Example | Goes to |
|---|---|---|---|
| `dbfId` | int | `132608` | key to the tier list entry |
| `id` | string | `"BG36_HERO_105"` | `CardId` |
| `placement_distribution` | string[8], percent, places 1..8 | `["17.22%","13.50%",…]` | `FirstRate` = [0]; `Top4Rate` = [0..3] summed |
| `hero`, `tier`, `pick_rate`, `avg_placement` | strings | same values as the tier list (116 of 116 matched) | not read |
| `best_composition_id` int, `image` URL, `hero_power{dbf, card{…}}` | | | not read |

When this address fails, the heroes list is `null` (no card ids), the other lists stay. The answer is not used when
`ok` is false or `view.blocked` is true.

### `/api/bg/heroes?mode=<solo|duos>&mmr=<slice>` -> `BgSiteTierList` (Duos, `LoadHeroesAsync`)

Top level: `ok` bool (must be `true`), `fetched_at` -> `FetchedAt`, `view{type (must be `"bg_heroes"`), mode,
filters{mmr_percentile, time_range}, heroes[]}`; **no `source_id`** (section 4, rule 6). Each hero carries its own
tier, so the list is built from this answer alone:

| Field | Type | Example (Duos) | Goes to |
|---|---|---|---|
| `id` | string | `"TB_BaconShop_HERO_34"` | `CardId` |
| `dbfId` | int | `59397` | `DbfId` |
| `hero` | string, **English** | `"Patchwerk"` | `NameEn` and `NameRu` |
| `tier` | `"S"`..`"D"`, or null when the slice has too few games of the hero | `"A"` | `Tier`; a hero without one is left out |
| `avg_placement` | number | `2.178` | `AveragePlacement` |
| `pick_rate` (= `pick_rate_value`) | string percent / number | `"48.51%"` | `PickRate` |
| `placement_distribution` | string[8] solo, string[4] Duos | `["33.06%","30.50%","22.01%","14.44%"]` | `FirstRate` = [0]; `Top4Rate` = the top half: places 1-4 of 8, **1-2 of 4 in Duos** |
| `adjusted_avg_placement`, `anomaly_adjusted`, `best_composition`, `best_composition_id`, `key_minions_top3`, `image`, `hero_power` | | | not read |

Entries are ordered S to D, inside a tier as sent; `AvailableTiers` is all five letters; `Source` is the `source_id`
the base heroes answer named (`hsreplay_battlegrounds_heroes`); `Games` is `null`.

### `lists.minions.tiers.<L>[]` → `BgSiteTierEntry`

| Field | Type | Example | Goes to |
|---|---|---|---|
| `id` | string | `"BG36_341"` | `CardId` |
| `dbfId` | int | `132929` | `DbfId` |
| `name` | string, English | `"Veteran Brigand"` | `NameEn` |
| `ruName` (= `localizedName`) | string, absent in 9 of 255 | `"Опытный бандит"` | `NameRu` (the English name when absent) |
| `tavernTier` | int 1..7 | `6` | `TavernTier` |
| `popularity` | string, percent | `"6.08%"` | `PickRate` |
| `games` | int | `36014` | `Games` |
| `impact` (= `metricValue`), `combatWinrate` string percent | | `0.59`, `"59.74%"` | not read here: the tab's columns come from `MinionStats` |
| `races` string[], `ruText`, `image`, `image256` | | | not read |

`AveragePlacement`, `Top4Rate`, `FirstRate` are `null`. The tier list and `minion-stats` come from different runs of
the server (17:01 and 02:45 UTC in the capture), so the same minion's numbers differ slightly between them
(`games` 36014 here, `games_with_minion` 28615 there).

### `lists.trinkets.tiers.<L>[]` → `BgSiteTierEntry`

| Field | Type | Example | Goes to |
|---|---|---|---|
| `id` | string | `"BG30_MagicItem_545"` | `CardId` |
| `dbfId` | int | `112397` | `DbfId` |
| `name` | string, English | `"Fridge Magnet"` | `NameEn` |
| `ruName` (= `localizedName`) | string | `"Магнит на холодильник"` | `NameRu` |
| `avgPlacement` | number | `2` | `AveragePlacement` |
| `pickRate` | string, percent | `"5.5%"` | `PickRate` |
| `games` | int, **a lower bound, not a count** (`gamesIsMinimum`: true; 0 in 3 entries) | `0` | `Games` |
| `size` | `"LARGE"` / `"SMALL"` | | `TrinketSize` (added member) |
| `cost` | int | `0` | `Cost` (added member) |
| `placementDistribution[]{place, rate}`, `avgPlacementLabel`, `typeLabel`, `text`, `ruText`, `race`, `raceRu`, `image`, `imageFallback`, `upstreamImage` | | | not read |

The list's `gamesNote` (verbatim: `games is the minimum integer sample compatible with HSReplay's rounded placement
distribution`) goes to `BgSiteTierList.GamesNote`: the UI must not present `Games` of a trinket as a sample size.
`TavernTier` is 0.

`/api/bg/tier-lists?list=trinkets&mmr=<slice>&timeRange=LAST_7_DAYS` (`LoadTrinketsAsync`) is the same list object at
the top level of the answer, with the same entry fields and its own `source` (`hsreplay` in the three slices seen);
in the top 50% slice no trinket has `games` 0.

### `/api/bg/library/minion-stats` → `BgSiteCardStat`, `BgSiteRun`

`latest_run{id, source, mmr_percentile, time_range, started_at, completed_at, state, minions_total, minions_ok,
error}` → `BgSiteRun(MmrPercentile "TOP_50_PERCENT", TimeRange "CURRENT_BATTLEGROUNDS_PATCH", CompletedAt)`. Then
`total`, `limit`, `offset` ints and `minions[268]`:

| Field | Type | Example | Goes to |
|---|---|---|---|
| `card_id` | string | `"BG36_345"` | `CardId` (dictionary key) |
| `dbf_id` | int | `132915` | `DbfId` |
| `tavern_tier` | int | `1` | `TavernTier` |
| `impact` | number or null (13 null) | `-0.04` | `Impact` |
| `combat_winrate` | number, percent, or null (18 null) | `57.43` | `CombatWinRate` 0..1 |
| `popularity` | number, percent, or null | `7.97` | `Popularity` 0..1 |
| `games_with_minion`, `games_without_minion` | int or null | `66058`, `762593` | `GamesWith`, `GamesWithout` |
| `avg_placement_with`, `avg_placement_without` | number or null | `4.36`, `4.32` | `AvgPlacementWith`, `AvgPlacementWithout` |
| `name`, `name_ru`, `snapshot_id`, `run_id`, `fetched_at` | | | not read |

All 255 minions of the tier list are here; 13 more are here only. `IsSpell` is false. `SpellStats` is always empty
(section 4).

### `/api/bg-comps` → `BgSiteComp`

Top level: `source`, `fetchedAt`, `count` int, `comps[60]`:

| Field | Type | Example | Goes to |
|---|---|---|---|
| `source` | `"HSReplay"` / `"Firestone"` | | filter: only `HSReplay` comps are read |
| `key` | string | `"hsreplay-76"` | `Id` |
| `title` | string, Russian for HSReplay comps | `"Свинобразы - APM-усиление таверны"` | `NameRu` |
| `tier` | `"S"`…`"D"` | `"S"` | `Tier` |
| `difficulty` | Russian word: `Лёгкая` / `Легкая`, `Средняя`, `Сложная` | | `Difficulty`: `"easy"`, `"medium"`, `"hard"`, else `null` |
| `description` | string, Russian | `"Разыгрывайте заклинания, чтобы поглощать усиленную таверну."` | `Tip` |
| `avgPlacement` | string, **empty for all 33 HSReplay comps** | `""` | `AveragePlacement` (`null` when empty) |
| `coreCards[]{id, dbfId, name, count, role}` | | `"BG28_633"` | `CoreCardIds` |
| `additionalCards[]{…}` | same shape, **empty for all 33 HSReplay comps** | | not read |
| `sourceUrl`, `whenToCommit` (empty in all), `whenToCommitCards`, `enablerCards` (empty), `cards` (core + addon) | | | not read |

`NameEn` is empty (one title per comp), `Games` is `null`, `Tribes` is empty: the service gives no tribe key for these
comps (the tribe is only the first word of the Russian title); the UI can take tribes from the core cards through
`CardDb`. Order of `Comps` is the service's.

## 4. What is shown, what is not, and why

There is no permission to use Firestone data, and its datasets must not feed the app
([bg-meta-sources.md](bg-meta-sources.md)). The check is an **allow list** (legal report L-M2), not a list of
forbidden names: the server changes without an app update, so a renamed or new origin must not reach players unseen.

**The rule** (`BgSiteParser.Allowed`, `Usable`):

1. The `source` of a list, a comp or an answer must be one of `hsreplay`, `hsreplay_battlegrounds_heroes`,
   `hsreplay_battlegrounds_minions`. Case and spaces do not matter (`HSReplay`, ` hsreplay `). Which field is the
   source: a tier list's `source`; `/api/bg/heroes` - `source_id` (and `site`, when present, must be allowed too);
   `minion-stats` - `latest_run.source`; a comp - its own `source`.
2. **No `source`, an empty one or any other value is a refusal**: `hearthpulse`, `hsreplay_premium`, a mixed
   `HSReplay + Firestone ...`, a new site. A new origin is a new value in the code after a legal check.
3. Even with an allowed `source`, a list whose `upstreamSource`, or a comp whose `sourceUrl`, names `firestone` or
   `zerotoheroes` is refused.
4. **Comps are checked one by one, by each comp's own `source`** (coordinator's decision on review finding M2). The
   list's `source` is mixed (`HSReplay + Firestone через api.kolodahearthstone.com`): the 33 comps marked `HSReplay`
   stay, the 27 marked `Firestone` are cut. A comp without a `source` of its own takes the list's, which is not on
   the allow list, so it is cut.
5. The check is by source, never by list name: the spells list would be read the day the service marks it `hsreplay`.
6. **Heroes slices** (`/api/bg/heroes?mode=…&mmr=…`: Duos, and the solo MMR slices) - *coordinator's decisions,
   2026-11-10, pending confirmation by legal.* These answers name no source at all, so rule 2 alone would refuse them.
   A slice is read only when both hold:
   - (a) the **base answer** - the answer of the same address without parameters, `/api/bg/heroes` - names an allowed
     source: `source_id` `hsreplay_battlegrounds_heroes` (an allowed `site`, and a `url` that is not Firestone's). The
     base answer that counts is the one **fetched in this load, or its cache file while that file is in force (not
     older than the cache term, 6 h)**. The client gets the base answer first and asks for the slice only after it;
     the slice's `Source` is that `source_id`.
   - (b) the slice itself says `ok: true` and `view.type` is `"bg_heroes"` (and is not `view.blocked`), and it is the
     mode and MMR slice that was asked for. A slice that does name a source of its own must name an allowed one.

   Why this is not "the address is the source": the slices are other filters of the one dataset the base answer
   describes (same address, same `view.type`, the same heroes, the server's own statement of where that dataset comes
   from), and the statement is read from the base answer, never assumed. The window in which a change of the source
   on the server goes unseen is the cache term, the same 6 h every cached list has.

   What happens when the base answer is not a good one:

   | The base answer, `/api/bg/heroes` | Slices |
   |---|---|
   | fetched now, or its file within 6 h; names the allowed source | requested (or read from their files within 6 h) and read |
   | **refusal** - the service answered now and said something else: it names another source, or it has heroes and names none | not requested and not read, **even from the cache** |
   | **failed load** - no answer (network, time-out, 4xx / 5xx, another host), or an answer that says nothing (`{}`, a maintenance note, unreadable, `ok: false` or `blocked` without another source); the old file (older than 6 h) names the allowed source | **not requested**; the old file of a slice stands in, as the old heroes file does for the solo list |
   | failed load and no old file | not requested, not read |

What the rule does to the captured answers:

| Data | `source` | Result in the app |
|---|---|---|
| `lists.heroes`, `lists.minions`, `lists.trinkets`; `/api/bg/heroes`; `minion-stats`; 33 comps | `hsreplay`, `hsreplay_battlegrounds_heroes`, `hsreplay_battlegrounds_minions`, `HSReplay` | read |
| `lists.spells` of `/api/bg/tier-lists` | `firestone` | `BgSiteStats.Spells` = `null`: the Spells view has no tier list |
| `lists.strategies` of `/api/bg/tier-lists` | `firestone` | not read |
| `/api/bg/library/spell-stats` | `firestone_battlegrounds_spells` | not requested; `SpellStats` empty |
| 27 of 60 comps in `/api/bg-comps` | `Firestone` | cut |
| `/api/bg/heroes?mode=…&mmr=…` (Duos, MMR slices of heroes) | none of its own | read under rule 6, while `/api/bg/heroes` names `hsreplay_battlegrounds_heroes` |

What is refused is cut before the cache is written, and an answer with nothing left is not written at all. The whole
`/api/bg/tier-lists` and `/api/bg-comps` answers are still downloaded, Firestone rows included (legal report L-m1:
acceptable until the server offers an address without them; `?list=trinkets` is one, a `?list=` for heroes is not).

Everything that is shown is HSReplay data by the service's own account; `/api/bg/heroes` titles its view "HSReplay
premium Battlegrounds heroes tier list.", and the default trinkets list is the `TOP_1_PERCENT` slice. The conditions
for showing it are in the legal report (`LEGAL-HeartLens-bg-site-stats.md`: L-B1, the owner's decision).

## 5. Records and client (`Hearthpulse.Core.Battlegrounds.Site`)

Records as in the task card's contract, plus these members at the end (nullable, default `null`):

- `BgSiteTierEntry.TrinketSize` (`"LARGE"` / `"SMALL"`), `BgSiteTierEntry.Cost` (int): trinkets only.
- `BgSiteStats.CompsFetchedAt` (`DateTime?`, UTC): the `fetchedAt` of `/api/bg-comps`, for the Comps tab's "source,
  slice, date" line; `null` without comps.

`BgSiteClient(HttpClient http, string? baseUrl = null, string? cacheDirectory = null)`:

| Member | |
|---|---|
| `DefaultBaseUrl` | `https://bg.hs-manacost.ru/`. `baseUrl` replaces it only when it is an absolute `https://` address. |
| `static HttpClient CreateHttp()` | The client the hosts pass in: no cookies, no redirects, no default headers. |
| `static TimeSpan MaxAge` | 6 h: how long an answer is kept. |
| `Task<BgSiteStats?> LoadAsync(ct)` | The refresh: every list in its default slice, five requests, each used (the Duos one is not made when `/api/bg/heroes` did not name its source). Never throws; `null` when nothing could be had. |
| `static BgSiteSlices HeroesSoloSlices`, `HeroesDuosSlices`, `TrinketSlices`; `HeroSlices(bool duos)` | The slices of each list and its default (table in section 2). `BgSiteSlices(IReadOnlyList<string> Mmr, string Default)`, `Has(mmr)`. |
| `Task<BgSiteTierList?> LoadHeroesAsync(bool duos, string mmr, ct)` | One heroes slice on demand, cache `bgsite-heroes-<solo\|duos>-<mmr>.json`; one request, plus `/api/bg/heroes` when its file is not fresh. `null` = no data for the slice. |
| `Task<BgSiteTierList?> LoadTrinketsAsync(string mmr, ct)` | One trinket slice on demand (last 7 days), cache `bgsite-trinkets-<mmr>.json`. `null` = no data for the slice. |

The tabs show the default slices from `LoadAsync`; when the player picks another slice, the tab calls the slice method
and, on the refresh timer, calls it again for the slice still selected (it answers from its cache file for 6 h).
**Call a slice method after `LoadAsync` has completed, not beside it**: loads that run at the same time do not share
requests, so a slice asked for in the same tick would fetch `/api/bg/heroes` (125 KB) or `/api/bg/tier-lists`
(950 KB) a second time. In order, a refresh with a selected heroes slice and a selected trinket slice is 5 + 1 + 1
requests.

Per list:

| | Heroes | HeroesDuos | Minions | Trinkets | Spells |
|---|---|---|---|---|---|
| available today | yes | yes (rule 6) | yes | yes | `null` |
| `CardId` from | `/api/bg/heroes` by `dbfId` | `id` | `id` | `id` | |
| `NameRu` / `NameEn` | `hero` / empty | English / English | `ruName` or `name` / `name` | `ruName` / `name` | |
| `TavernTier` | 0 | 0 | 1..7 | 0 | |
| `AveragePlacement` | yes | yes (1..4) | `null` | yes | |
| `PickRate` | yes | yes | yes (popularity) | yes | |
| `Top4Rate`, `FirstRate` | yes | top 2 of 4, first | `null` | `null` | |
| `Games` | `null` | `null` | yes | lower bound | |
| `Mmr`, `TimeRange` | from `/api/bg/heroes` `view.filters` | from the answer's `view.filters` | `null` (see `BgSiteStats.Run`) | from the list | |
| `GamesNote` | `null` | `null` | `null` | from the list | |

`BgSiteStats.Updated` is the latest `FetchedAt` of the loaded lists and comps (minion stats: `latest_run.completed_at`).
A time is when the server fetched the data from its origin, not when the app downloaded the answer.
