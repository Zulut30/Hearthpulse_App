# Statistics use per-metric eligibility, not one completeness flag

Status: accepted, 2026-11-09 (TASK-2026-11-09-analytics-round1, P1).

A source capture has one global flag, `summary.completeForAggregation`: a normal complete game with no gaps, no
unsupported records, no unresolved choices and a mode outcome. Every Battlegrounds game source carries a structural gap
(the Power state runs ahead of the displayed combat), so no BG source can ever pass it, and the place of a BG game,
the commonest number players ask for, could never be counted from source.

Weakening the global flag would let any partial game into every denominator. Instead the decoder (result v2,
`source-v2.0`) reports a separate fitness per metric family, each with its own checks and fixed reasons:

- `outcome` — the visible result or place: an `end` with `reason:"normal"`, no `late_attach`, `rotation` or `shutdown`
  gap, and the outcome of the mode. Gaps that lose observations but not the end of the game (`combat_view`,
  `unsupported`, `limit`, `invalid`, other `source_missing`) do not spoil it.
- `offers` — the Battlegrounds hero offer: the same lifecycle rules, plus the first choice typed `mulligan`, resolved,
  2–4 options and one pick.

The structural BG gap is named (`combat_view`, source v2) instead of being an anonymous `source_missing`, so the rules
can tell it from a real loss. Source v1 cannot: v1 Battlegrounds sources stay unfit (`source_v1`).

## Consequences

- `completeForAggregation` keeps its meaning and its consumers; it is not loosened. A BG v2 game is `partial` and still
  fit for `outcome` and `offers`.
- A new metric family adds its own flag, reasons and tests (the plan names context, actions, economy, mulligan, round
  and draft); it never reuses a looser flag because it is convenient.
- The decoder's word is not final: the server re-checks each fact against the source rows before storing it, and a
  mismatch fails the job (task contract C2). A rule change is a new parser version and a reprocess, not an edit of
  stored facts.
- Each metric in [the catalog](../analytics-metrics.md) names the fitness it requires and its rules version
  (`bg-hero-1` for the first one).
