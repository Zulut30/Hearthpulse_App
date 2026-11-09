# Analytics metrics catalog

Every number our statistics will show has one definition here before it is computed: unit, numerator, denominator,
the fields it needs, how missing data is treated, grouping, the small-sample rule and where it is shown. The machine
copy is [analytics-metrics.json](analytics-metrics.json) (catalog `version: 1`); `AnalyticsCatalogTests` keeps the two
in step and checks every field against the field matrix. A changed definition is a new metric `version`; a changed
computation of an implemented metric is a new `rulesVersion`.

## Rules for every metric

- **Unit.** One observation by one participating installation (a game capture, a draft pick, a card instance). Several
  clients' observations of the same lobby are not called unique matches: there is no safe way to join them.
- **Fitness.** A metric names the per-metric eligibility it needs ([ADR 0013](adr/0013-per-metric-eligibility.md));
  `completeForAggregation` is not loosened for it. An observation that is not fit is left out of both numerator and
  denominator.
- **Patch.** The group is `start.build`; `null` is its own "unknown" group. Builds are never mixed to reach a sample.
- **Small samples.** Shown honestly as missing (`null` with `small_sample`), never padded. Every published number
  covers games of at least 5 installations (`owner`, the hash of the install id), counted among the observations that
  number is made of; below that the number is `null`. Above it, a value with fewer than 10 in its denominator keeps
  its counts but its value, interval and distribution are `null`. Server rules: [analytics-api.md](analytics-api.md).
- **Uncertainty.** Shares carry a Wilson interval (z = 1.96); means carry mean ± 1.96·s/√n and are `null` below n = 2.
- **Hidden information.** Only what the local player saw: their own offers, picks, results and places, the public
  format, Arena kind and build. No names, BattleTag, account, game or deck ids.

## Areas and metrics

| Area | Metric | Status |
|---|---|---|
| Game results | `results.winrate`, `results.ties`, `results.duration` | defined |
| Game results | `results.by_class` | needs fields |
| Battlegrounds places | `bg.placement`, `bg.duos.team_placement` | defined |
| Heroes and trinkets | `bg.hero.offer_pick_placement` (`bg-hero-1`) | **implemented** |
| Heroes and trinkets | `bg.trinket.offer_pick` | needs fields |
| Shop and discovers | `bg.shop.actions`, `bg.discover.offer_pick` | defined |
| Shop and discovers | `bg.shop.offer_pick` | needs fields |
| Economy | `bg.economy.actions` | defined |
| Economy | `bg.economy.gold_left` | needs fields |
| Combats | `bg.combat.round_result`, `bg.combat.sim_vs_actual` | needs fields |
| Builds | `bg.build.final_board` | needs fields |
| Arena | `arena.draft.offer_pick` | defined |
| Arena | `arena.run.wins` | needs fields |
| Mulligan | `mulligan.keep` | needs fields |

Statuses: `implemented` — computed end to end; `defined` — every field exists (`have`/`derived`), no computation yet;
`needs_fields` — a field is `add` (a later F1 round) or `unavailable`, so the number does not exist yet.

## The implemented metric: `bg.hero.offer_pick_placement`, rules `bg-hero-1`

Unit: one Battlegrounds solo game capture. Window: `start.day` in [today − 30, today]. Group: `start.build`.

- `offered(h)` — captures fit for `offers` whose hero offer contains `h` (once per capture).
- `picked(h)` — of those, captures whose pick is `h`; `pickRate = picked / offered`.
- `placement.n(h)` — captures fit for `offers` and `outcome` whose pick is `h`; `mean` place; `dist[0..7]` counts places
  1..8; `top4 = #(place ≤ 4) / n`; `first = #(place = 1) / n`.
- Every number needs 5 distinct installations (`owner`) among its own captures: the hero row — installations it was
  offered to (else it is not listed, only counted in `suppressed`); `picked`/`pickRate` — installations that picked it;
  `placement`/`top4`/`first` — installations that picked it and have a place. Below that the number is `null`.
- Above that, a value with fewer than 10 games in its denominator keeps `num`/`den` (`n`), but `value/low/high`
  (`mean`) and `dist` are `null`; a row is `small_sample` when any of its values is `null`.
- Limit: the offer is the one at the moment of the pick. Heroes rerolled away are not in `offered(h)`, so their offer
  frequency is understated; that is a separate metric later.

Source: result v2 facts `offers[kind = bg_hero]` and `outcome.placement` (see the
[CLI contract](../tools/Hearthpulse.SourceWorker/README.md#source-v2-and-result-v2)); the server re-checks both
against the source rows. API: `GET /stats/v2/analytics/bg/heroes?mode=solo`.

## Field matrix

`fieldMatrix` in the JSON lists every field a metric names, with its status and source:
`have` (written by the current recorder or compact record), `derived` (computed reliably from `have` fields), `add`
(planned for a later F1 round, absent today), `unavailable` (not visible to the player or never uploaded — the metric
stays unavailable). Source v2 added `start.format`, `start.arena` and `choice.choiceType`; the remaining `add` fields
(game and run keys, class, deck key, coin, lobby tribes, mulligan phases, combat boundary and results) are the next
round's F1 work.
