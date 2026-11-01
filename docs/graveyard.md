# Graveyard panel ("Кладбище")

An overlay panel (MovablePanel `graveyard`, Constructed and Arena) with the minions that died this game: the
opponent's above the player's, as on the board. Setting "Graveyard" in Settings → Overlay, off until turned on
(`AppSettings.ShowGraveyard`).

- **Data:** `GraveyardView.Of` (`Core/Tracking/GameSession.Graveyard.cs`) from the counter log (`CounterTracker`): a
  minion that went from `PLAY` to `GRAVEYARD`. Deaths on the board are public for both players. Not counted: a card
  discarded or burned from the hand or the deck, a minion returned to the hand or the deck, a transformation, spells
  and Secrets. A minion that dies again after a resurrection counts again.
- **Look:** like the opponent panel (card 214 wide, `Ornament` sections "Opponent's · N" and "Yours · N", tracker rows
  with cost gem, name over the art strip, rarity strip, "×N" for repeats; compact rows on the panel's compact switch).
  At most 10 rows a side, then "+N more"; a side with no deaths says "None died yet". The panel shows from the first
  death and hides between games. The full card shows on hover over a row (the rows take the pointer).
- **Default spot:** right, bottom, `0,0,276,72` in 1920 × 1080 units: left of the deck list, clear of the hand,
  the hero, the board and End Turn up to about 7 + 7 rows; with more the player can move it.
- Render scene: `overlay-graveyard.png` (`GraveyardView.Sample`).
