# Secrets helper

While the opponent has Secrets in play (Constructed and Arena), the overlay panel `secrets` lists every Secret of
their class and format and crosses out the ones the game has ruled out. Setting: `AppSettings.ShowSecrets`
(Settings > Overlay, "Secrets helper"). Backlog item C1 (`reference/index/not-implemented-2026-10.md`).

Panel: x 1180-1390 on the 1920x1080 surface (right of the opponent's Hero Power, left of `opponent-archetype`), from
y 140, above the board. Folded it shows the first 4 possible Secrets and "+N more"; unfolded, the whole pool (crossed
out by hand, then ruled out). Only the rows and "+N more" take clicks; everything else passes through to the game.

## Where the code is

| Part | File |
|---|---|
| Trigger table: card id -> triggers and conditions | `src/Hearthpulse.Core/Secrets/SecretRules.cs` |
| Pool: Secrets of a class in a format, reprints folded | `src/Hearthpulse.Core/Secrets/SecretPool.cs` |
| Tracker (`IPowerListener`), events, view | `src/Hearthpulse.Core/Secrets/SecretTracker.cs`, `SecretsView.cs` |
| Session wiring, manual cross-out, quiet commit | `src/Hearthpulse.Core/Tracking/GameSession.Secrets.cs` |
| Render scene | `src/Hearthpulse.Core/Secrets/SecretsScene.cs`, `DevRender.Secrets.cs`, `OffscreenRenderer.Secrets.cs` |
| Overlay | `Overlay/SecretsPanel.xaml` / `.axaml`, `OverlayViewModel.Secrets.cs` (both UIs) |
| Strings | `Localization/Strings/{en,ru}/secrets.json` |
| Tests | `tests/Hearthpulse.Tests/SecretsTests.cs`, `SecretGame.cs` (synthetic logs), `SecretsRealLogTests.cs` |
| Fixture of every collectible Secret | `tests/.../Fixtures/Secrets/secrets.enUS.json`, made by `tools/secrets/secrets_fixture.py` |

## Data

- **Pool**: collectible spells with the `SECRET` mechanic from `CardDb` (HearthstoneJSON), by `cardClass` and `set`.
  Standard: sets in `CardPoolTables.StandardSets`; Wild and Twist: every set but `VANILLA`; Classic: `VANILLA` only;
  Arena: the Arena pool rotates and is not in the card data, so every Secret of the class (as Wild). Printings of one
  card (`EX1_287`, `CORE_EX1_287`, `VAN_EX1_287`, a reprint under a new id with the same name) are one row; the key is
  the id without the reprint prefix.
- **Rules**: written by hand from the English card text, one entry per Secret. A Secret with a `null` rule fires on
  something the player cannot check (Hidden Cache needs a minion in their hand, Ice Block needs fatal damage), so it
  is never ruled out. `SecretsTests.Every_collectible_secret_has_a_rule_entry` fails when HearthstoneJSON gains a
  Secret the table does not know: regenerate the fixture after a patch and add the new rows.
- No code, tables or strings of HDT or Firestone are used; only the idea of the feature.

## What the player sees (and nothing else)

- A Secret is an opponent entity in zone `SECRET` with `SECRET=1` (objectives, auras, quests and sigils sit in the
  same zone without it). `SECRET=1` may arrive after the zone, so Secrets are picked up on every event and view.
- Its class is the `CLASS` tag (the icon colour); without one, the opponent's hero class.
- Mana spent on it: the opponent's `RESOURCES_USED` increase plus `TEMP_RESOURCES` decrease inside its `PLAY` block
  (on the kept logs this equals the card cost in 64 of 68 opponent plays, the rest were discounted, never more). Pool
  Secrets cheaper than that are dropped, unless that would drop all of them (a cost increase such as Loatheb).
- Known Secret: only when its hand slot showed the card (it was not a card drawn from their deck - a card back keeps
  whatever id the log carries). Then it is listed as known, and no other active Secret can be the same card.
- Copies: in Ranked/Casual/Friendly (not Arena), a Secret from their starting deck cannot be a card of which two
  copies from their starting deck (one for a Legendary) were already seen face up. Created Secrets are not limited.
- Created Secrets: the visible card that made it is named (`DISPLAYED_CREATOR`/`CREATOR` of a face-up card only; a
  creator in their deck, hand or Secret zone is not). `CREATOR_DBID` and the hidden card's `COST`/card id are never read.

## Events

Each event records the Secrets active at that moment and the board around it (their minions, their hand size, your
minions). The tracker judges events in windows: a window closes at the next top-level `PLAY`/`ATTACK` block of the
local player, at every `STEP` change, or when the log has been quiet for 1 s with no block open
(`GameSession.TickSecrets`; one action's lines are not one timestamp - a Discover inside a play waits for input). If
any active Secret fired (a `TRIGGER` block from it), was revealed or left play in the window, the whole window is
dropped: one Secret firing can stop another (a countered spell, a redirected attack). Otherwise each event is kept on
its Secrets with the conditions that held before and after it (`SecretNeeds`), and a pool Secret is ruled out when
one of its triggers was seen with all its conditions.

| Trigger | Seen as |
|---|---|
| minion played / on fourth / spell / spell on a minion / weapon / hero power | top-level `PLAY` of a local card (a minion must still be in play at the block end; `CARD_TARGET` for spells) |
| third card, second draw | local `NUM_CARDS_PLAYED_THIS_TURN` = 3, `NUM_CARDS_DRAWN_THIS_TURN` = 2 |
| card played the turn it entered hand | the played card's `EnteredHandTurn` is the current turn |
| spent all Mana, turn ended with no Mana | `RESOURCES + TEMP_RESOURCES - RESOURCES_USED - OVERLOAD_LOCKED` = 0, judged only without temporary crystals and Overload |
| attacks | `ATTACK` block of a local character, defender = first `PROPOSED_DEFENDER` on the game entity |
| 3+ damage by an attacking minion | defender `DAMAGE` increase during the `ATTACK` block (armor loss not counted), attacker still alive at its end |
| their hero damaged | their hero's `DAMAGE` increases on your turn |
| their minion died (the turn after it was played) | their minion `PLAY` -> `GRAVEYARD` on your turn (played turn from their `PLAY` block) |
| turn ended (after plays / no Mana / they took no damage) | `STEP=MAIN_END` on your turn; only Secrets active since `MAIN_READY` |
| their turn started (with two minions) | `STEP=MAIN_START_TRIGGERS` on their turn |

Turn: the local player entity's `CURRENT_PLAYER` = 1. Events of their own turn never rule anything out, except the
start of their turn. Elusive (`CANT_BE_TARGETED_BY_SPELLS`), Dormant or untouchable minions never rule out Secrets
that act on that minion.

## Checked logs

Kept logs on this machine (`D:\Hearthstone\Logs`, sessions 2026-10-23 16:08 to 2026-10-25 17:24, 8 Standard ranked
games that reached your first turn) contain **no opponent Secret** - only objectives (`BE_085`, `BE_726`) and auras
(`END_011`, `TIME_700`) in the Secret zone, which are correctly not listed. The line shapes of `ATTACK`, `PLAY`,
`STEP`, `CURRENT_PLAYER`, `RESOURCES_USED`, `TEMP_RESOURCES`, `NUM_CARDS_*` and `PROPOSED_DEFENDER` were taken from
them. The way a card back of theirs enters the Secret zone is in `Hearthstone_2026_10_23_16_08_05/Power.log` line
4756: an aura (`TIME_700`) played from hand gets `TAG_CHANGE` `SECRET=0`, `CLASS=PALADIN`, `COST=5` inside its `PLAY`
block while still a card back, and the opponent pays `RESOURCES_USED` 0 -> 4 plus `TEMP_RESOURCES` 1 -> 0 (5 Mana);
an aura is then shown face up (`SHOW_ENTITY ... ZONE=SECRET`), a Secret stays a card back with `SECRET=1`. The COST
tag of the card back is hidden information and is not read. `SecretsRealLogTests` replays every kept game with a
card-back Mage Secret added for the opponent at your first turn: the real events rule out Counterspell in 7 of 8
games and Ice Barrier in 6. A real Secret game is still to be checked when one is in the kept logs.

## Known limits

- Arena shows every Secret of the class: the current Arena pool is not in the card data or the Hearthpulse API.
- Only attacks count for "deals 3 or more damage" (Reckoning); damage from spells or Battlecries does not rule it out.
- A window with any Secret firing is dropped whole, so two events in one action (an attack and the deaths it causes)
  tell nothing when one Secret fired.
- History import (`LogBackfill`) does not run the tracker: the helper is live only.
