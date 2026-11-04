# Mercenaries is not tracked: the mode is frozen, so it stays out of stats, replays and the overlay

Hearthstone's Mercenaries mode still runs, but Blizzard stopped its development: patch 25.4 (February 2023) was the
last content update, with bug and balance fixes only when needed
([Shacknews](https://www.shacknews.com/article/134066/hearthstone-ending-mercenaries-updates)); nothing new has come
since, and nothing in the mode has been removed either (checked 2026-11-02, plan stage 4). Hearthpulse keeps it out of
everything but the History row: a finished Mercenaries game is written to History with `mode = 'Mercenaries'`
(`GameRecords.ModeOf`), but it is not recorded as a replay (`ReplayRecorder`), History's Constructed queries, stats and
deck records skip it, the HDT / Firestone imports skip it, the profile upload never sends it, and the overlay's ladder
widgets ignore the `LETTUCE_*` scenes. This stays so: no Mercenaries panels, counters, stats or replays.

## Consequences

- A Mercenaries game in the log is parsed only far enough to know it is one; nothing new is built for the mode.
- Its History row is kept (the game happened) but left out of every stat, as today.
- Reopen with a new ADR only if Blizzard brings new Mercenaries content back, or removes the mode (then the remaining
  code that recognises it can go).
