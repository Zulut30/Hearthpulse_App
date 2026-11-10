# BgSite fixtures

Real answers of `https://bg.hs-manacost.ru`, captured 2026-11-09 23:06 UTC with one plain `GET` per address (no
sign-in), then cut down. Field notes: `docs/bg-site-stats.md`.

| File | Address | What was cut |
|---|---|---|
| `tier-lists.json` | `/api/bg/tier-lists` | Up to 3 entries per tier (4 in minions B: one without `ruName` added); `tierCounts` and `count` recomputed to match; `availableBackgrounds`, `exports`, `background` removed. The Firestone lists (`spells`, `strategies`) keep the service's `source` fields and 1 row per tier, and **those rows are invented** (ids `TEST_FS_…`, made-up names and numbers): they exist to test that the client refuses and cuts them. |
| `heroes.json` | `/api/bg/heroes` | The heroes of the trimmed tier list plus one more; `status` removed; `hero_power` cut to its `dbf`. |
| `minion-stats.json` | `/api/bg/library/minion-stats` | The minions of the trimmed tier list, two with null numbers, one that is in no tier; `total` is the original 268. |
| `comps.json` | `/api/bg-comps` | 3 HSReplay comps as sent, and 2 comps marked `Firestone` whose content is **invented** (keys `firestone-test-…`, cards `TEST_FS_…`); `count` recomputed. |
| `spell-stats.head.json` | `/api/bg/library/spell-stats` | Head only (`source_id`, `site`, `url`, view metadata), no rows: the app does not request this address; kept as the record of its origin. |
| `heroes-duos.json` | `/api/bg/heroes?mode=duos&mmr=TOP_50_PERCENT` (probe, 2026-11-09 23:35 UTC) | 2 heroes per tier; `hero_power` cut to its `dbf`, `key_minions_top3` emptied. As sent, it has no `source_id`: `heroes.json` vouches for it. |
| `heroes-solo-top20.json` | `/api/bg/heroes?mode=solo&mmr=TOP_20_PERCENT` (same probe round) | The same cut. |
| `heroes-solo-top1.json` | `/api/bg/heroes?mode=solo&mmr=TOP_1_PERCENT` (probe, 2026-11-09 23:58 UTC) | The same cut, plus 2 of the heroes the slice has no tier for. |
| `trinkets-top50.json` | `/api/bg/tier-lists?list=trinkets&mmr=TOP_50_PERCENT&timeRange=LAST_7_DAYS` (same probe round) | 2 trinkets per tier; `tierCounts` and `count` recomputed. |

Everything else is as the service sent it. No row of Firestone data is in these files.
