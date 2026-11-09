# BgSite fixtures

Real answers of `https://bg.hs-manacost.ru`, captured 2026-11-09 23:06 UTC with one plain `GET` per address (no
sign-in), then cut down. Field notes: `docs/bg-site-stats.md`.

| File | Address | What was cut |
|---|---|---|
| `tier-lists.json` | `/api/bg/tier-lists` | Up to 3 entries per tier (4 in minions B: one without `ruName` added); `tierCounts` and `count` recomputed to match; `availableBackgrounds`, `exports`, `background` removed. The Firestone lists (`spells`, `strategies`) keep 1 entry per tier with ids and numbers only: they exist to test that the client refuses them. |
| `heroes.json` | `/api/bg/heroes` | The heroes of the trimmed tier list plus one more; `status` removed; `hero_power` cut to its `dbf`. |
| `minion-stats.json` | `/api/bg/library/minion-stats` | The minions of the trimmed tier list, two with null numbers, one that is in no tier; `total` is the original 268. |
| `comps.json` | `/api/bg-comps` | 3 HSReplay comps and 2 Firestone comps (their `description` emptied); `count` recomputed. |
| `spell-stats.head.json` | `/api/bg/library/spell-stats` | Head only (`source_id`, `site`, `url`, view metadata), no rows: the app does not request this address; kept as the record of its origin. |

Everything else is as the service sent it.
