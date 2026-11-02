# Hidden information: during a game only what the player sees; after it, only the match page shows the reveals

While a game runs, the overlay, the trackers and everything shown use only what the local player can see on screen:
no card ids from the opponent's hand, secrets or deck, no deck order, nothing from another player's tavern. After the
game, the match page ([match-replay.md](../match-replay.md)) may show everything the log revealed by the end of the
game; nothing else uses that post-game knowledge. This keeps the app within what Blizzard allows third-party tools,
and it is the line every feature is reviewed against (skill `hearthpulse-review`, agent `hearthpulse-legal`).

The one in-game exception (owner, 2026-10-29): Battlegrounds opponents' MMR from Blizzard's public leaderboard, off by
default, under the conditions in [bg-opponent-mmr.md](../bg-opponent-mmr.md) (legal report
`LEGAL-HeartLens-bg-opponent-mmr.md`). It is open data outside the client, not game state. Nothing else from outside
the client is covered by it.

## Consequences

- Filtering happens where data is recorded (replay events, live views), not where it is drawn, so a new view cannot
  leak by accident. Test: `ReplayRevealTests.The_live_game_gets_nothing_from_the_future`.
- A new exception needs the owner's decision and a legal check, and is added here.
