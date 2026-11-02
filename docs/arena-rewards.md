# Arena run rewards ("Награды")

My Stats → Arena: each run row has a rewards chip, and the period's tiles end with "Rewards". Design:
`reference/tasks/TASK-2026-11-02-arena-live/design.md`.

- **Source:** the player. No log the client writes names Arena rewards (none of the owner's logs up to 2026-10-30
  has a finished run's rewards), and reading them from game memory would be a new surface for the legal check. Once
  the logs of a finished run show a format (owner's checklist), the rewards can be filled in automatically; what the
  player entered is kept.
- **Storage:** `arena_rewards` in the history (`HistoryStore.ArenaRewards.cs`): gold, dust, packs, tickets, cards per
  run key (`ArenaRun.Key`: `deck:<id>` from Decks.log's deck id, else class and the first game's start). Local only:
  not uploaded, not in replays. All zero clears a run's rewards.
- **Chip:** under the run, only rewards above 0 with MDI glyphs (gold, dust, packs, tickets, cards) and a pencil, or
  "+ Add rewards". Runs still going take rewards too: a run the player gave up early never reaches its loss limit.
- **Editor:** opens in the row (one at a time; opening another closes it unsaved): five fields, empty is 0, digits
  only, 0..99 999, checked as typed ("Dust: whole numbers from 0 to 99,999 only"); Save (Enter), Cancel (Esc), Clear
  (when saved). The open editor survives a rebuild of the list (a new game, another period) and closes when its run
  leaves the period. A failed save keeps the editor open with an error.
- **Tile:** the period's gold, then the other rewards ("gold · 340 dust · 6 packs"); "—" while no run of the period
  has rewards. The Arena tiles are in rows of three (six tiles).
- Render scenes: `main-arena-deck.png` (two made-up runs, one with rewards: `Arena/ArenaRunsScene.cs`) and
  `main-arena-rewards.png` (its editor open).
