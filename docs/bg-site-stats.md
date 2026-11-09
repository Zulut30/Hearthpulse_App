# Battlegrounds tabs: statistics from the site's open service

The main window's Battlegrounds tabs (Heroes, Trinkets, Cards, Comps) show the lists hearthpulse.net shows. The site
proxies an open service, `https://bg.hs-manacost.ru`, and the app reads that service directly (owner's decision,
2026-11-09). The in-game advisor and the overlay do not use it: they keep `BattlegroundsMeta`
([bg-meta-sources.md](bg-meta-sources.md)).

Answers were captured on **2026-11-09 23:06 UTC** with one plain `GET` per address, no sign-in, no cookie. Trimmed
copies are the test fixtures in `tests/Hearthpulse.Tests/Fixtures/BgSite/` (its `README.md` says what was cut).

Code: `src/Hearthpulse.Core/Battlegrounds/Site/` (`BgSiteClient`, `BgSiteStats`). Tests: `BgSiteStatsTests`.

## 1. Rules of the client

- Only `GET https://bg.hs-manacost.ru/...`. No `Authorization`, no cookie, no account token, nothing about the player
  in the request. Never hearthpulse.net (it answers 401 without sign-in).
- Four requests per refresh (section 2, "read"); the limit is 8.
- Cache `<data>/cache/api/bgsite-<part>.json`, 6 h, written through a temporary file. A failed request falls back to
  the old file, however old; the list's own `fetchedAt` goes to the UI so it can say how old the numbers are.
- Parts load independently: one address failing leaves its part `null` / empty and the others loaded.
- An answer over 8 MB, unreadable JSON, an unknown tier letter or an entry without a card id is dropped with a line in
  the log (`Diag`); nothing throws.
- **A list whose `source` names Firestone or zerotoheroes is not given to the app** (section 4), and is cut from the
  answer before it is cached, so that data is not kept on the player's disk either.
- The UI names the source "Hearthpulse data" (`HearthpulseData.Attribution`).

## 2. Addresses

Sizes are of the captured answers. Server headers on all of them: `Cache-Control: public, max-age=300,
stale-while-revalidate=900`, a weak `etag`, `access-control-allow-origin: *`.

| Address | Size | Read | `source` (verbatim) | Cache file | Tab |
|---|---|---|---|---|---|
| `/api/bg/tier-lists` | 950 KB | yes | per list, below | `bgsite-tier-lists.json` | Heroes, Trinkets, Cards |
| `/api/bg/heroes` | 125 KB | yes | `source_id`: `hsreplay_battlegrounds_heroes`, `site`: `hsreplay` | `bgsite-heroes.json` | Heroes (card id, top 4, 1st place, MMR slice) |
| `/api/bg/library/minion-stats` | 100 KB | yes | `latest_run.source`: `hsreplay_battlegrounds_minions` | `bgsite-minion-stats.json` | Cards (minions) |
| `/api/bg-comps` | 109 KB | yes | `HSReplay + Firestone через api.kolodahearthstone.com`; per comp `source`: `HSReplay` (33) or `Firestone` (27) | `bgsite-comps.json` | Comps |
| `/api/bg/library/spell-stats` | 24 KB | **no** | `source_id`: `firestone_battlegrounds_spells`, `site`: `firestone` | — | — (Firestone, section 4) |
| `/api/bg/library/meta` | 3.5 KB | no | none | — | — (card counts and filter names of the site's library; the tabs need none of it) |
| `/api/bg/library/cards` | 1.9 MB | no | not captured | — | — (names, tavern tier and art come from `CardDb`) |

Lists inside `/api/bg/tier-lists` (`lists.<name>`):

| List | `source` | `upstreamSource` | Entries | Slice fields | Used |
|---|---|---|---|---|---|
| `heroes` | `hsreplay` | `HSReplay через api.kolodahearthstone.com` | 116 (S 0, A 28, B 24, C 40, D 24) | none here; see `/api/bg/heroes` | yes |
| `minions` | `hsreplay` | `HSReplay через api.kolodahearthstone.com` | 255 (S 20, A 43, B 77, C 76, D 39) | none | yes |
| `trinkets` | `hsreplay` | `HSReplay trinkets + Blizzard card images` | 253 (S 18, A 68, B 44, C 56, D 67) | `mmr`: `TOP_1_PERCENT`, `timeRange`: `LAST_7_DAYS`, `gamesNote` | yes |
| `spells` | `firestone` | `Firestone через api.kolodahearthstone.com` | 72 | none | **no** (section 4) |
| `strategies` | `firestone` | `Firestone через api.kolodahearthstone.com + фреймы api.kolodahearthstone.com` | 27 | none | **no** (section 4) |

The answer's top level also has `generatedAt`, `tier` (null) and `strategySource`: `firestone`.

**Duos:** the service has no Duos slice. `/api/bg/tier-lists` has one `heroes` list, and `/api/bg/heroes` reports one
slice (`view.filters`: `mmr_percentile` `TOP_50_PERCENT`, `time_range` `CURRENT_BATTLEGROUNDS_PATCH`) with no mode
field. `BgSiteStats.HeroesDuos` is always `null`. The data API's `api/bg/heroes/duos` is the next step if Duos is
wanted (task card, "Решения").

**One MMR slice per list:** heroes and minions top 50%, current Battlegrounds patch; trinkets top 1%, last 7 days.
There is nothing for the tab's percent picker to choose from.

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

When this address fails, the heroes list is `null` (no card ids), the other lists stay.

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

## 4. What is not shown, and why

The earlier decision stands ([bg-meta-sources.md](bg-meta-sources.md)): there is no permission to use Firestone data,
and its datasets must not feed the app. The client therefore drops, by the `source` the service itself reports:

| Data | `source` | Result in the app |
|---|---|---|
| `lists.spells` of `/api/bg/tier-lists` | `firestone` | `BgSiteStats.Spells` = `null`: the Spells view has no tier list |
| `lists.strategies` of `/api/bg/tier-lists` | `firestone` | not read |
| `/api/bg/library/spell-stats` | `firestone_battlegrounds_spells` | not requested; `SpellStats` empty |
| 27 of 60 comps in `/api/bg-comps` | `Firestone` | dropped; the 33 `HSReplay` comps stay |

The rule in code: a list (or a comp) is refused when its `source` contains `firestone` or `zerotoheroes` in any case,
and a comp is also refused when it has no `source` of its own while the list's `source` names one of them. If the
server changes the origin of a list, the list appears or disappears without an app update.

**For the legal check (`hearthpulse-legal`, before merge):** everything that is shown is HSReplay data by the
service's own account. `/api/bg/heroes` titles its view "HSReplay premium Battlegrounds heroes tier list." and names
`hsreplay.net/api/v1/battlegrounds/heroes/` with an MMR percentile filter as its origin; the trinkets list is the
`TOP_1_PERCENT` slice. The task card records no risk acceptance for Battlegrounds heroes, trinkets, minions or comps
from HSReplay (Blocker L-B1 covers data of the same origin). The comps' `description` texts are shown as tips.

## 5. Records (`Hearthpulse.Core.Battlegrounds.Site`)

As in the task card's contract, with two members added at the end of `BgSiteTierEntry` (nullable, default `null`),
for trinkets only: `TrinketSize` (`"LARGE"` / `"SMALL"`) and `Cost` (int). `BgSiteComp` is as in the contract.

Per list:

| | Heroes | Minions | Trinkets | Spells | HeroesDuos |
|---|---|---|---|---|---|
| available | yes | yes | yes | `null` | `null` |
| `CardId` from | `/api/bg/heroes` by `dbfId` | `id` | `id` | | |
| `NameRu` / `NameEn` | `hero` / empty | `ruName` or `name` / `name` | `ruName` / `name` | | |
| `TavernTier` | 0 | 1..7 | 0 | | |
| `AveragePlacement` | yes | `null` | yes | | |
| `PickRate` | yes | yes (popularity) | yes | | |
| `Top4Rate`, `FirstRate` | yes | `null` | `null` | | |
| `Games` | `null` | yes | lower bound | | |
| `Mmr`, `TimeRange` | from `/api/bg/heroes` `view.filters` | `null` (see `BgSiteStats.Run`) | from the list | | |
| `GamesNote` | `null` | `null` | from the list | | |

`BgSiteStats.Updated` is the latest `FetchedAt` of the loaded lists and comps (minion stats: `latest_run.completed_at`).
`BgSiteClient.LoadAsync` returns `null` only when no part could be had from the network or the cache.
