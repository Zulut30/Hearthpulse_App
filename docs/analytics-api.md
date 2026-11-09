# Analytics API: facts from source v2 and published snapshots

Round 1 of the analytics plan (TASK-2026-11-09, F2) carries one metric end to end: `bg.hero.offer_pick_placement`,
rules `bg-hero-1` — which Battlegrounds heroes the player was offered, which one they picked, and where they placed.
Metric definitions live in the catalogue (`docs/analytics-metrics.json`, P1); this page is the server side: what is
stored, when it is counted and deleted, and the read route. Code: `server/pool/node/analytics.mjs`, migration
`server/pool/migrations/0009_analytics.sql`, client `src/Hearthpulse.Core/Pool/AnalyticsClient.cs`.

## Path of one game

1. **Intake** (`/v1/captures/chunks`, `/complete`): a source is all-v1 or all-v2 lines, equal to `manifest.sourceVersion`.
   v2 adds `start.format` (only for `ranked`/`casual`), `start.arena` (only for `arena`), `choice.choiceType`
   (`mulligan|general|other`, fixed for one `choiceId`) and the gap reason `combat_view`: required exactly once in
   every v2 BG/Duos game, `count: 1`, in the capture header (only `start`/`gap` records before it), nowhere else — the
   same rule as the decoder. A v2 field in a v1 line, an unknown value, a value in the wrong mode, mixed versions → 400.
2. **Worker result**: `v:1` (older worker) is accepted as before and gives no facts. `v:2` is cross-checked against the
   typed source; any mismatch makes the capture `failed` without facts:
   - `context` equals the `start` fields (v1 sources: `format`/`arena` null);
   - `eligibility.outcome = true` needs a game with `end.reason = normal`, no `late_attach|rotation|shutdown` gap and a
     result (`bg` place 1..8, `duos` 1..4, others W/L/T); for v1 sources only non-BG with `completeForAggregation`;
     `facts.outcome` equals `end.result`/`end.placement`;
   - `eligibility.offers = true` needs a v2 BG/Duos game with the same lifecycle, and the last record of the lowest
     `choiceId` being `choiceType: mulligan`, resolved, 2–4 options, one pick; `facts.offers` is exactly that record
     (`seq`, options, pick, `kind: bg_hero`);
   - `false` claims need empty facts (`outcome: null`, `offers: []`).
3. **Facts** (`source_fact_game`, `source_fact_offer`) are replaced in the same transaction as `derived`. Reprocess keeps
   the old set until the new result commits; a failed reparse — the worker's result or the re-check of the stored
   source (changed or lost chunk) — removes the capture's facts and withdraws the snapshot in the same transaction.
   Each fact row keeps the capture's `owner` (hash of the install id, already in `source_captures`) to count
   installations; it is deleted with the generation.
4. **Snapshot** (`analytics_snap`, key `bg-heroes-solo`) is built in the archive queue, in one transaction, by a timer at
   most once a minute when nothing is published yet, it is stale or withdrawn, or the day changed. New facts mark it
   `stale` (the old numbers stay correct to serve). Replaced or deleted facts **withdraw** it in the same transaction
   (`body = ''`): the route answers `rebuilding` until the next build.
5. **Deletion**: `/v1/captures/forget` (generation) and retention (`maintain()`) delete the facts explicitly with the
   receipts and withdraw the snapshot; a worker result arriving after deletion is discarded.

## The count (C4)

One unit is one BG solo game capture (one installation's observation). Window: `day` in `[today − 30, today]`. Groups:
`build` from `start`, newest first, `null` last; builds are never merged.

| Value | Definition |
|---|---|
| `captures`, `sources` | captures with `offers_ok` in the group; distinct installations (`owner`) among them |
| `offered(h)` | captures with `offers_ok` whose offer contains `h` (once per capture) |
| `picked(h)` | of those, the ones where `h` was picked; `pickRate = picked / offered` |
| `placement.n(h)` | captures with `offers_ok ∧ outcome_ok` where `h` was picked; `dist` = count of places 1..8 |
| `mean`, `low`, `high` | mean place ± 1.96·s/√n (s: sample standard deviation); interval null when n < 2 |
| `top4`, `first` | share of places ≤ 4 / = 1 among `placement.n`, Wilson interval z = 1.96 |
| `suppressed` | heroes offered to fewer than `ANALYTICS_MIN_SOURCES` (5) installations: not listed |
| `quality` | `small_sample` when any value is null (too few installations or games), else `ok` |

**Publication thresholds (privacy policy: every summary covers games of at least five installations).** Each number
needs `ANALYTICS_MIN_SOURCES` (5) distinct installations behind it, counted among the captures that number is made of:

- the row itself — installations the hero was offered to (else it is not listed, only counted in `suppressed`);
- `picked`, `pickRate` — installations that picked the hero; below that both are `null` (no count at all);
- `placement`, `top4`, `first` — installations that picked the hero and have a result; below that all three are `null`.

Above the installation threshold, a value with fewer than `ANALYTICS_MIN_CELL` (10) games in its denominator keeps
`num`/`den` (`n`) but `value`/`low`/`high` (`mean`) are `null`, and `dist` is `null`. A `null` field is the reason
"too small to publish" of C5; the row is then `small_sample`.

A capture with offers but no outcome counts in `offered`/`picked` only; one without offers counts nowhere. Heroes
rerolled away before the pick are not in `offered` (catalogue limitation). Numbers are rounded to 3 decimals.
`ANALYTICS_MIN_CELL` / `ANALYTICS_MIN_SOURCES` exist for tests only and are checked at start (an invalid value stops
the service); production keeps the defaults.

**Limits.** An installation is the hash of its install id: reinstalling the app (a new id) counts as a new source.
One installation can still contribute many captures to a group: a heavy player weighs more in `offered`/`picked` and
the places (no per-installation cap in `bg-hero-1`; candidate for `bg-hero-2`).

## `GET /stats/v2/analytics/bg/heroes?mode=solo`

`mode` defaults to `solo`; `duos` → `available: false, reason: "unsupported_mode"`; another value, another or a repeated
parameter → 400. Headers `content-type: application/json`, `cache-control: no-store`; nginx turns proxy caching off.
No capture key, generation or install id appears in the body.

```jsonc
{
  "v": 2, "metric": "bg.hero.offer_pick_placement", "rulesVersion": "bg-hero-1",
  "mode": "solo", "available": true, "reason": null,          // false + "not_published" | "rebuilding" | "unsupported_mode"
  "published": 1762646400000, "stale": false, "source": "source_capture",
  "period": { "fromDay": 20371, "toDay": 20401, "days": 30 }, // null when not available
  "groups": [
    { "build": 223456, "captures": 120, "sources": 14, "suppressed": 3,
      "heroes": [
        { "hero": "TB_BaconShop_HERO_xx", "offered": 40, "picked": 12,
          "pickRate":  { "num": 12, "den": 40, "value": 0.3,   "low": 0.181, "high": 0.454 },
          "placement": { "n": 12, "mean": 3.92, "low": 3.05, "high": 4.79, "dist": [2,1,2,2,1,2,1,1] },
          "top4":      { "num": 7,  "den": 12, "value": 0.583, "low": 0.320, "high": 0.807 },
          "first":     { "num": 2,  "den": 12, "value": 0.167, "low": 0.047, "high": 0.448 },
          "quality": "ok" } ] } ]
}
```

Rows are ordered by `offered` (desc), then hero id. Hidden values are `null` objects or fields, for example
`{"hero":"…","offered":6,"picked":null,"pickRate":null,"placement":null,"top4":null,"first":null,"quality":"small_sample"}`. `not_published`: nothing was ever published (or no BG facts yet);
`rebuilding`: published numbers were withdrawn after a deletion or replacement and the next build is pending.

## Rollout order

1. **Server first:** deploy the pool with this package (intake v1+v2, migration 0009, result v1+v2) and `nginx -t` the
   new location with its read zone before the reload.
2. **Then the worker v2** (`Hearthpulse.SourceWorker`, P1): a v1 worker reading a v2 source fails it, so the server
   must already accept v2 and the worker must be v2 before any v2 source arrives.
3. **Then clients writing source v2** and any default endpoint; captures already queued as v1 keep uploading as v1.

A client with source v2 against an older server gets 400 for every chunk; a v1 worker after a v2 client leaves the
captures `failed` until reprocess with the v2 worker.

## Client

`AnalyticsClient.LoadBgHeroesAsync(http, endpoint)` takes the pool base (as `PoolClient.Endpoint`, e.g.
`https://api.kolodahearthstone.com/stats`) and requests `{base}/v2/analytics/bg/heroes?mode=solo`. It returns the
parsed snapshot, an object with `Available = false` and `Reason`, or `null` on a network error, a non-success status,
invalid JSON or a non-https endpoint. Hidden numbers arrive as `null` (`Picked`, `PickRate`, `Placement`, `Top4`,
`First`, `Placement.Distribution`). No screen uses it yet (B1).

## Tests

`server/pool/test/analytics.test.mjs` (AC9–AC16: hand-checked set with an independent calculation, idempotent reprocess,
deletion and retention, cross-check failures, route answers, migration and a 10 000-capture rebuild),
`capture-validation.test.mjs` (v2 lines), `tests/Hearthpulse.Tests/AnalyticsClientTests.cs` (AC17).
