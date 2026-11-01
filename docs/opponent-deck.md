# Likely deck panel ("Вероятная колода")

An overlay panel (MovablePanel `opponent-deck`, ranked and casual Constructed) with the opponent's likely deck: the
most played meta list of the archetype its cards point to, the copies it already played marked. Setting "Likely deck"
in Settings → Overlay (Constructed group) and in the live bar's "⋯" menu (`AppSettings.ShowLikelyDeck`), off until
turned on. Design: `reference/tasks/TASK-2026-11-01-opponent-deck/design.md`.

- **Archetype:** the leader of the live archetype odds (`ArchetypeOdds.Build(...).Best`, the archetype the
  "Opponent's archetype" panel names), with its chance in the header. The two panels never name different archetypes.
- **List:** `LikelyDeck.ListOf` (`Core/Constructed/LikelyDeck.cs`): the archetype's most played meta list (by games);
  without lists, its core cards, one copy each, and the panel says so. The match page's "Predicted deck" uses the same
  list (`MatchPage.PredictedRows`).
- **Meta:** the meta the meta page loaded for the game's format, so its rank and period filters (plan 2.4,
  `AppSettings.MetaRank` / `MetaPeriod`) apply; the source line names them ("Hearthpulse data · Legend · 7 days").
- **Marks:** "2" untouched, "1/2" and "2/2" once played; one copy: no badge, "★" for a legendary. A row whose copies
  were all played is dimmed as a drawn-out card in the deck tracker. A card played more often than the list runs it
  counts that many copies. Cards played from the starting deck that the list does not hold: "Not in the list · N"
  under the list, up to 4 rows and "+N more".
- **Hidden information:** only public meta and cards the opponent played face up from its starting deck
  (`Entity.IsOriginalDeckCard`, `PlayedCard.FromDeck`). A card created during the game (generated in its hand and then
  played) marks nothing and is never "not in the list"; cards in its hand, Secrets before they are revealed, discarded
  or burned cards and its real deck size are never used.
- **Not shown:** Arena, Tavern Brawl, games against the AI, friendly games, Battlegrounds; without the meta of the
  game's format, archetypes of the class or a card played from the starting deck; between games.
- **Look:** card 214 wide, at most 384 high: header (title, chance with the odds' tooltip, archetype name), source line,
  the list scrolling (12 rows, 15 compact), "Not in the list" always in sight. Rows: tracker rows with cost gem, art,
  rarity strip and mark; the full card on hover. Default spot: right, top, `0,76,276,0` in 1920 × 1080 units, beside
  "Your deck", above End Turn.
- Render scene: `overlay-likely-deck.png` (`LikelyDeckView.Sample`).
