# Mercenaries is not tracked: the mode is frozen, so it stays out of History, replays and the overlay

Hearthstone's Mercenaries mode still runs, but Blizzard stopped its development: patch 25.4 (February 2023) was the
last content update, with bug and balance fixes only when needed; nothing new has come since, and nothing in the mode
has been removed either (checked 2026-11-02, plan stage 4). Hearthpulse already leaves it out: `GameState.IsMercenaries`
games are not recorded as replays (`ReplayRecorder`), History's Constructed queries and deck records skip
`mode = 'Mercenaries'`, the HDT / Firestone imports skip them, the profile upload never sends them, and the overlay's
ladder widgets ignore the `LETTUCE_*` scenes. This stays so: no Mercenaries panels, counters, stats or replays.

## Consequences

- A Mercenaries game in the log is parsed only far enough to know it is one; nothing new is built for the mode.
- A History row that comes in as "Mercenaries" (an old import or a log replay) is kept but left out of Constructed
  stats, as today.
- Reopen with a new ADR only if Blizzard brings new Mercenaries content back, or removes the mode (then the remaining
  code that recognises it can go).
