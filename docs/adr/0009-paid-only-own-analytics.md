# Only our own analytics can be paid: card art and card data, and third-party numbers, are never behind a limit

A subscription or a daily free limit (plan 3.1, [usage limits](../usage-limits.md)) may cover only analytics Hearthpulse
makes from its own data: today the Battlegrounds stats pool, built from games our players share. Card art and card data
(HearthstoneJSON: names, text, images; copyright Blizzard Entertainment) are shown free to everyone, in every build and
every mode. Numbers from third parties (HSReplay, HSGuru, ArenaSmith and the like: the mulligan guide, hero stats,
Discover picks, the Arena draft) are never sold or limited: that would resell someone else's data
(`reference/reviews/LEGAL-HeartLens-card-choices.md`, L-M3).

## Consequences

- A new key for `UsageGate` (app) and `appUsage` (site) names its data source; it is accepted only when the numbers
  are ours. A licence that explicitly allows paid access changes this, and only for that source, by a new ADR.
- A limited panel still shows its cards with art and names; only our numbers are left out, and a line says why.
- On 2026-11-08 the owner confirmed that permission to use card data in the product has already been obtained.
  The former task to request that permission is closed. This confirmation does not change the rule that art and
  card data stay outside the limit, or extend permission to other third-party datasets such as HSReplay Premium.
- The main window's reference pages (meta, comps, trinkets) stay free; the limit covers in-game hints only.
