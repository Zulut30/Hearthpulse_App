# Constructed stats from the Hearthpulse API

Since this change every Constructed number in the app comes from our API (`api.kolodahearthstone.com`). Firestone is
not called at all (its loaders were removed); Battlegrounds moved to the API too, see `docs/bg-meta-sources.md`.
Checked against the live API on 2026-10-23.

## 1. Sources

| What the app shows | Endpoint | Notes |
|---|---|---|
| Tier list: win rate, popularity, games per archetype | `GET /v1/hsguru/meta?format=&rank=&period=&min_games=100` | Follows the page's format, rank and period filters. `coin` is `any_player` only, so the 1st / Coin columns show "—". |
| Class of each archetype | the HSGuru lists' `class` (`/v1/hsguru/archetypes`), else the name (`ArchetypeNames.ClassOf`: "DH", "DK", "-lock", class words) | Archetypes with no class from either are skipped. |
| Top decks tab, an archetype's best lists | `GET /v1/hsguru/archetypes?format=&min_games=50&limit=500` (up to 30 lists per archetype) | Always the **current expansion, all ranks**: the API publishes lists for no other rank or period. |
| Core cards (`ConstructedMeta.Match`: your decks → archetype, overlay fallback) | the same lists: cards in ≥ 60% of an archetype's lists, weighted by games | |
| Mulligan guide on the page, overlay fallback | `/api/db/archetypes` + `/{id}/decks` + `/{id}/mulligan` (HSReplay, Standard, Legend, EU) | Per HSGuru archetype, matched as described in section 2. |
| Mulligan for Wild and for archetypes HSReplay lacks | `GET /v1/hsguru/archetypes/analysis?archetype=&format=` (HSGuru, Legend, past week): per card `mulligan_count`, `kept_count`, `kept_impact`; `class_matchups` | Loaded for the 30 most played such archetypes per meta load. Keep rate = kept / in mulligan, win rate when kept = the archetype's (class results weighted by games) + `kept_impact`. HSGuru names cards by their original id (`CS1_130`), so a row goes to the list's card of the same name (`CORE_CS1_130`). |
| Russian archetype names | `ArchetypeNames.RussianLabel`, a port of the HeartPulse site's `fallbackTranslations.ts` and `archetypeLabel.ts` | Used when the card language is ruRU: the exact label, else the longest contained label with the other words in brackets, else the English name. Two archetypes sharing a label get the English name in brackets. |

Loading: the tier list, lists and core cards are shown first (`IProgress`), then the mulligan numbers follow; a newer
filter change cancels the older load. An empty slice (no archetype with 100 games) shows as "No meta stats", not as a
failure. HSGuru lists marked "Neutral" do not count as a class.

Filters: the API's own keys. Ranks: `all`, `diamond_to_legend`, `legend`, `top_5k`, `top_500`, `top_100`. The API also
has `diamond`, `diamond_4to1` and `top_legend`, left out so the filter row fits. Periods: `past_day`, `past_3_days`,
`past_week`, `past_30_days`, plus `patch` and `expansion`. The API names the current patch and expansion in every
answer (`patch_36.6.3`, `black_empire`), so a `past_day` slice is read first to resolve them. Keys saved by earlier
versions map onto the new ones, for example `past-7` → `past_week` and `top-2000-legend` → `top_5k`
(`ConstructedMeta.Key`). The page caption and the status line name the source "Hearthpulse data" (`HearthpulseData.Attribution`), in both
interface languages; the player never reads HSReplay or HSGuru.

## 2. HSGuru ↔ HSReplay archetypes

The two sites split the meta differently: HSGuru has "C'Thun Warlock", "Zoolock", "Void C'Thun DH"; HSReplay has
"Hand Warlock", "Void Demon Hunter". On 2026-10-23 only 27 of 173 HSGuru names matched an HSReplay name by
normalisation alone. `ApiConstructedMeta.Match` therefore works in two steps, always within one class:

1. **Name.** Both names are normalised: DH/DK spelled out, "-lock" → "warlock", Harold = Herald, no punctuation.
   The word sets must be equal.
2. **Cards.** The HSGuru archetype's most played list goes through the mulligan guide's matcher
   (`MulliganGuide.MatchArchetype`). It needs ≥ 50% of the cards in an HSReplay list, or ≥ 40% presence in the
   archetype's mulligan rows.

When neither step matches, the archetype gets HSGuru's own analysis instead (section 1), if it is among the 30 most
played such archetypes and HSGuru has one. Otherwise it has no mulligan data, and the page says so. Live result
(Standard, Legend, 7 days): 72 of 96 archetypes got HSReplay numbers and 20 got HSGuru's. Zoolock and Shredslock, which
HSReplay does not have, are among the 20. In Wild, the 30 most played archetypes got HSGuru numbers.

Mulligan deltas compare against the HSReplay sample's own baseline (`MetaArchetype.MulliganBaseline`), not against
HSGuru's win rate, which comes from other games. The page's mulligan list does the same (one line in
`MainViewModel.Constructed.cs`).

## 3. What is lost compared with Firestone

- **Win rate going first / on the coin.** HSGuru's coin filter is scraped as `any_player` only.
- **Decklists per rank and period.** The lists are current expansion, all ranks, whatever the filters say.
- **Bronze–Gold and Platinum brackets.** HSGuru's lowest bracket is "all".
- **HSReplay mulligan for Wild.** HSReplay data on the server is Standard only. Wild uses HSGuru's analysis, which
  has fewer games per card and no average turn, and counts kept cards rather than the opening hand.
- **Mulligan for archetypes HSReplay does not have** (section 2).

Gained:
- **HSGuru brackets:** Top 5K/500/100 and Diamond+.
- **Periods:** 1 day, 30 days, the current expansion.
- **Russian names**, matching the site.
- **Mulligan numbers from HSReplay.**

## 4. Battlegrounds

Done since: see `docs/bg-meta-sources.md` (what each number comes from, what stays empty until the stats pool runs,
and what the server could add).
