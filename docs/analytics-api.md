# Analytics API: facts from source v2 and published snapshots

Round 1 of the analytics plan (TASK-2026-11-09, F2) carries one metric end to end: `bg.hero.offer_pick_placement`,
rules `bg-hero-1` — which Battlegrounds heroes the player was offered, which one they picked, and where they placed.
Metric definitions live in the catalogue (`docs/analytics-metrics.json`, P1); this page is the server side: what is
stored, when it is counted and deleted, and the read route. Code: `server/pool/node/analytics.mjs`, migration
`server/pool/migrations/0009_analytics.sql`, client `src/Hearthpulse.Core/Pool/AnalyticsClient.cs`.

## Path of one game

1. **Intake** (`/v1/captures/chunks`, `/complete`): a source is all-v1 or all-v2 lines, equal to `manifest.sourceVersion`.
   v2 adds `start.format` (only for `ranked`/`casual`), `start.arena` (only for `arena`), `choice.choiceType`
   (`mulligan|general|other`, fixed for one `choiceId`) and the gap reason `combat_view` (once, only in a BG/Duos game).
   A v2 field in a v1 line, an unknown value, a value in the wrong mode, mixed versions → 400.
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
   the old set until the new result commits; a failed reparse removes the capture's facts.
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
| `captures`, `sources` | captures with `offers_ok` in the group; distinct generations among them |
| `offered(h)` | captures with `offers_ok` whose offer contains `h` (once per capture) |
| `picked(h)` | of those, the ones where `h` was picked; `pickRate = picked / offered` |
| `placement.n(h)` | captures with `offers_ok ∧ outcome_ok` where `h` was picked; `dist` = count of places 1..8 |
| `mean`, `low`, `high` | mean place ± 1.96·s/√n (s: sample standard deviation); interval null when n < 2 |
| `top4`, `first` | share of places ≤ 4 / = 1 among `placement.n`, Wilson interval z = 1.96 |
| `suppressed` | heroes offered in fewer than `ANALYTICS_MIN_SOURCES` (5) distinct generations: not listed |
| `quality` | `small_sample` when any value is null because its denominator < `ANALYTICS_MIN_CELL` (10), else `ok` |

A capture with offers but no outcome counts in `offered`/`picked` only; one without offers counts nowhere. Heroes
rerolled away before the pick are not in `offered` (catalogue limitation). Numbers are rounded to 3 decimals.
`ANALYTICS_MIN_CELL` / `ANALYTICS_MIN_SOURCES` exist for tests only; production keeps the defaults.

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

Rows are ordered by `offered` (desc), then hero id. `not_published`: nothing was ever published (or no BG facts yet);
`rebuilding`: published numbers were withdrawn after a deletion or replacement and the next build is pending.

## Client

`AnalyticsClient.LoadBgHeroesAsync(http, endpoint)` takes the pool base (as `PoolClient.Endpoint`, e.g.
`https://api.kolodahearthstone.com/stats`) and requests `{base}/v2/analytics/bg/heroes?mode=solo`. It returns the
parsed snapshot, an object with `Available = false` and `Reason`, or `null` on a network error, a non-success status,
invalid JSON or a non-https endpoint. No screen uses it yet (B1).

## Tests

`server/pool/test/analytics.test.mjs` (AC9–AC16: hand-checked set with an independent calculation, idempotent reprocess,
deletion and retention, cross-check failures, route answers, migration and a 10 000-capture rebuild),
`capture-validation.test.mjs` (v2 lines), `tests/Hearthpulse.Tests/AnalyticsClientTests.cs` (AC17).
