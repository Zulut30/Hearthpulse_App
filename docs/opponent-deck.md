# Likely deck panel ("Вероятная колода")

An overlay panel (MovablePanel `opponent-deck`, ranked and casual Constructed) with the opponent's likely deck: the
most played meta list of the archetype its cards point to, the copies it already played marked. Setting "Likely deck"
in Settings → Overlay (Constructed group) and in the sidebar overlay card's "⋯" menu (`AppSettings.ShowLikelyDeck`), off until
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
  counts that many copies. A reprint (`CORE_`, `VAN_`) marks the list's card (`SecretRules.BaseId`). The card id and
  "from its own starting deck" are taken when the card is played, so a card transformed on the board later (Hex,
  Polymorph) keeps its mark. Cards played from the starting deck that the list does not hold: "Not in the list · N"
  under the list, up to 4 rows and "+N more".
- **Hidden information:** only public meta and cards that left the opponent's own starting deck face up
  (`Entity.IsOriginalDeckCard` with its first controller, `PlayedCard.FromDeck`): played, and also discarded or burned
  ones, which both players see. Never: a card created during the game (generated in its hand and then played), a card
  taken from your deck, cards in its hand, a Secret played face down, a card set aside, a card shown and shuffled back
  (HIDE_ENTITY) until it is shown again, its real deck size.
- **Not shown:** Arena, Tavern Brawl, games against the AI, friendly games, Battlegrounds; without the meta of the
  game's format, archetypes of the class or a card played from the starting deck; between games.
- **Look:** card 214 wide, at most 384 high: header (title, chance with the odds' tooltip, archetype name), source line,
  the list scrolling, "Not in the list" always in sight. The list gets what is left: about 12 rows (15 compact)
  without the off-list section, 9 with two off-list rows. At a larger overlay scale the card is shorter
  (`OverlayViewModel.LikelyDeckMaxHeight`: it ends above End Turn, y 466 in 1080 screen units, down to 160) and the
  list scrolls sooner. Rows: tracker rows with cost gem, art,
  rarity strip and mark; the full card on hover. Default spot: right, top, `0,76,276,0` in 1920 × 1080 units, beside
  "Your deck", above End Turn.
- Render scene: `overlay-likely-deck.png` (`LikelyDeckView.Sample`).
