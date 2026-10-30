# Constructed counters — specification

The in-game counters (`src/Hearthpulse.Core/Tracking/Counters/Counters.cs`) cover the counters Hearthstone Deck
Tracker / HSReplay show in Constructed. This page lists the counters added to reach that set: what each one counts,
which cards care about it, and where the value comes from.

Sources of truth are card text (HearthstoneJSON enUS, `https://api.hearthstonejson.com/v1/latest/enUS/cards.json`) and
the tags of real Power.logs (client of October 2026). Counter names follow HDT's list only so players recognise them;
no HDT or Firestone code was read or used.

Value sources:

- **Player tag** — a tag the client keeps on the player entity (`TAG_CHANGE Entity=<BattleTag> tag=...`).
  "Seen" means the tag was found in the kept logs; "enum" means it is a client tag name known from the game's tag
  enumeration but was not hit in the kept logs (no such card was played).
- **Tracker** — counted by `CounterTracker` from Power.log events: cards played from hand (`BLOCK_START
  BlockType=PLAY`), minions/weapons entering and leaving play (`ZONE` changes), attacks (`BlockType=ATTACK`), triggers
  (`BlockType=TRIGGER`), choices (`ENTITY_CHOICES` / `EntitiesChosen`, local player only), hero damage (`DAMAGE` on the
  hero), quest progress (`QUEST_PROGRESS`).
- **Card table** — per-card amounts generated from card text by `tools/counters/gen_counter_cards.py` into
  `CounterCards.g.cs` (`Sources`: amount when played / when it dies; `BloodRunes`; `Protoss`).

"Relevant" cards (shown in Auto mode) are generated into `CounterCards.Related` from the text patterns in the
generator; the card ids below are the main ones.

## Added counters

| Id | Title | Counts | Cards (enUS ids) | Source | Goal / shown as |
|---|---|---|---|---|---|
| `abyssal-curse` | Abyssal Curse | Damage of the next Abyssal Curse this player gives: 1 + curses given (each Curse is worse than the last). | TSC_955 Sira'kess Cultist, TSC_956 Dragged Below, TSC_924 Abyssal Wave, TSC_959 Za'qul (curse token TSC_955t) | Card table: +1 per play of a card that says "Give your opponent an Abyssal Curse". Approximate: a doubled Battlecry (Brann) is not counted. | "Next: N" |
| `animal-companion` | Animal Companion | Extra Cost of your future Animal Companions (they become random Beasts that cost more). | MEND_303 Migrating Elekk (+1), MEND_300 Tame Pet (+1), MEND_307 Roam Free (+2); companion cards NEW1_031, OG_211, MEND_301, EDR_853, LOOT_217, DMF_123, TOY_353 | Card table ("Replace your future Animal Companions with random Beasts that cost (N) more"), on play. | "+N Cost" |
| `extra-companion` | Extra Animal Companion | Extra Animal Companions your cards summon this game. | MEND_304 Talya Earthstrider | Card table ("summon 1 more"), on play. | "+N" |
| `asteroid` | Asteroid damage | Damage your Asteroids deal: 2 + bonuses. | GDB_434 Bolide Behemoth (+1); Asteroid GDB_430 from GDB_435, GDB_445, GDB_901 | Card table ("Your Asteroids deal N more damage"), on play. | "N damage" |
| `astral-automaton` | Astral Automaton | Astral Automatons summoned this game; the next one has +N/+N. | TTN_401 | Tracker: minions entering play. | "+N/+N" |
| `aviana` | Lunar cycle | Turns left until the Full Moon (Aviana's three-turn lunar cycle). | EDR_895 Aviana, Elune's Chosen | Tracker: turn of the last play, counted in the player's own turns. Approximate (the game keeps the countdown on a hidden enchantment EDR_895t). | "N turns" / "Full Moon" |
| `board-damage` | Board damage | Damage the player's minions and hero can deal to the enemy hero from the board: for the player to move, what is left this turn; for the other, what the board deals on their next turn. | — (shown in Auto whenever it is above 0) | Board entities (`BoardDamage.Of`): ATK × attacks left (Windfury 2, Mega-Windfury 4) of minions in play; leaves out `FROZEN`, `CANT_ATTACK`, `DORMANT`, and this turn `EXHAUSTED` (summoned without Charge, out of attacks) and a Rush minion on the turn it entered play (`Entity.EnteredPlayTurn`, minions only). Hero: ATK (weapon and buffs) × swings left, weapon Windfury counts; next turn the larger of the hero's and the weapon's ATK. Tags checked in real logs of October 2026: summon sets `EXHAUSTED`, the controller's turn start clears it, `NUM_ATTACKS_THIS_TURN` clears at the next turn start, a Rush minion enters not exhausted, a hero buff ends with the turn. Visible board only. | "N"; tooltip "This turn / Next turn: minions N, hero N" and the attackers |
| `blood-runes` | Blood Runes | Blood Rune symbols on the cards you've played this game. | BE_287 G'huun | Card table `BloodRunes` from `runeCost.blood`; summed over plays. Counts rune symbols (a card with two Blood Runes counts 2). | "N" |
| `captured-archmage` | Captured Archmage | Your Captured Archmages that died this game (the next one casts Fireball after 4 others). | JAIL_974 | Tracker: friendly minion deaths. | "N/4" |
| `cards-drawn-turn` | Cards drawn this turn | Cards you've drawn this turn. | SW_037, SW_454, TOY_519, TTN_841, TTN_844, YOG_402, BT_196, DED_506 | Player tag `NUM_CARDS_DRAWN_THIS_TURN` (seen; reset at every turn change). | "N" |
| `cards-2-mana` | Cards played for 2 Mana | Cards you've played that cost exactly 2 when played. | JAIL_474 Jade Guardians, JAIL_470 Lotus Troublemaker | Tracker: plays with last known cost in hand = 2 (`TAG_LAST_KNOWN_COST_IN_HAND`). Checked against Jade Guardians' own count (`TAG_SCRIPT_DATA_NUM_1`) in a real log. | "N" |
| `cards-played-turn` | Cards played this turn | Cards you've played this turn. | EX1_613, AV_134, MAW_020, FIR_919, ICC_910, DMF_519, REV_750, REV_940, JAIL_500, JAIL_909, BE_721, UNG_063, YOD_017 | Player tag `NUM_CARDS_PLAYED_THIS_TURN` (seen). The tag of the player whose turn just ended resets one turn late, so it counts only on the player's own turn. | "N" |
| `chaotic-tendril` | Chaotic Tendril | Chaotic Tendrils played; the next one casts a spell that costs 1 + N. | YOG_514 (got from YOG_515, YOG_517, YOG_523, YOG_526) | Tracker: plays. | "N-Cost spell" |
| `colossus` | Protoss spells | Protoss spells you've cast this game (improve Colossus). | SC_758 Colossus | Tracker plays × card table `Protoss` (StarCraft mini-set cards of the Protoss classes Druid/Mage/Priest/Rogue) or the entity's `PROTOSS` tag (enum). | "N" |
| `cthun-old` | C'Thun | Stats of your old-style C'Thun: 6/6 plus buffs "wherever it is". | OG_280 C'Thun; buffers OG_281, OG_283, OG_284, OG_286, OG_162, OG_255, OG_339, OG_293, OG_282, OG_302, OG_303, OG_321, OG_334, WON_075; "if your C'Thun has at least 10 Attack" OG_096, OG_131, OG_188, OG_301, WON_010, WON_111 | Player tags `CTHUN_ATTACK_BUFF`, `CTHUN_HEALTH_BUFF` (enum). | "A/H" |
| `discovers` | Discovers | Cards you've Discovered this game (picks from a Discover). Your own only: the opponent's choices are not in the log. | GDB_237 Alien Encounters; this turn: GDB_843, TLC_365, TLC_462 | Tracker: local `ENTITY_CHOICES` whose source has the Discover mechanic (card data or the entity's `DISCOVER` / `USE_DISCOVER_VISUALS` tag), counted when the pick is sent. | "N" (+ this turn in details) |
| `golakka` | Dive the Golakka Depths | Times the repeatable quest was completed: Murlocs you summon gain +N/+N. | TLC_426 | Tracker: `QUEST_PROGRESS` reaching `QUEST_PROGRESS_TOTAL` on the quest. Progress itself stays in the Quests counter. | "+N/+N" |
| `elwynn-boar` | Elwynn Boar | Your Elwynn Boars that died this game. | SW_075 | Tracker: friendly minion deaths. | "N/10" |
| `excavate` | Excavate | Times you've Excavated this game ("If you've Excavated twice"). | WW_001, WW_002, WW_009, WW_322, WW_334, WW_352, WW_378, WW_380, WW_412, WW_417, WW_426, DEEP_009, DEEP_018, DEEP_033; cares: DEEP_007, WW_349, WW_356, WW_413, WW_375, WW_324 | Card table ("Excavate a treasure": on play; after "Deathrattle:" on death). Highest tier in details from player tag `MAX_EXCAVATE_TIER` (seen). Approximate: conditional ones (Smokestack "if it dies") count on play. | "N" |
| `expensive-spells` | Spells (5+) cast | Spells you've cast this game that cost 5 or more. | LOOT_535 Dragoncaller Alanna | Tracker: spell plays with last known cost ≥ 5. | "N" |
| `galakrond` | Galakrond invokes | Times you've Invoked Galakrond. | DRG_600, DRG_610, DRG_620, DRG_650, DRG_660 and Invoke cards DRG_021, DRG_030, DRG_050, DRG_202, DRG_218 (twice), DRG_242, DRG_246–DRG_250, DRG_303 | Player tag `INVOKE_COUNTER` (enum), else card table ("Invoke Galakrond", "twice" = 2) over plays. | "N" |
| `hero-attacks` | Hero attacks | Times your hero attacked this game. | JAIL_200 Infest the Scullery | Tracker: `ATTACK` blocks whose attacker is a hero. | "N" |
| `leyline-cost` | Leyline cost reduction | How much less your Leylines cost this game. | MEND_501 Ley Walker (1); Leylines MEND_500, MEND_502, MEND_504 | Card table ("Your Leylines cost (N) less"), on play. The Arcanomicon's chosen upgrade is not counted (its pick is not identifiable for the opponent). | "-N" |
| `leyline-effect` | Leyline effect increase | How much the effects of your Leylines are increased. | MEND_506 Mystic Runesaber (1) | Card table ("Increase the effects of your Leylines by N"), on play. | "+N" |
| `leyline-trigger` | Leyline extra triggers | Additional times your Leylines trigger. | MEND_503 Surge Needle | Card table ("trigger an additional time"), on play. | "+N" |
| `libram` | Libram cost reduction | How much less your Librams cost this game. | BT_020 (1), BT_026 (2), GDB_721 (1 + 1 on death), GDB_726 (1 + 1 on death); Librams BT_011, BT_024, BT_025, GDB_137–GDB_139, YOP_011 | Card table ("Reduce the Cost of your Librams by (N)": on play; "Battlecry and Deathrattle" also on death; weapons' death = destroyed). | "-N" |
| `minions-died-turn` | Minions died this turn | Minions that died this turn, both sides. | BRM_001, BRM_003, BRM_009, BRM_025, ICC_904, ETC_410, END_004, EX1_190, FP1_013, BT_427, BE_564, BE_565, BE_877, MEND_805, GIL_518, GIL_905, REV_372, JAIL_445, EDR_491, TRL_566 | Player tags `NUM_FRIENDLY_MINIONS_THAT_DIED_THIS_TURN` of both players (seen); yours in details. | "N" |
| `paladin-cards` | Paladin cards played | Paladin cards you've played this game. | TID_077 Lightray | Tracker plays × card class PALADIN. | "N" |
| `pogo-hopper` | Pogo-Hopper | Pogo-Hoppers played; the next one gains +2N/+2N. | BOT_283 | Tracker: plays. | "+2N/+2N" |
| `protoss-discount` | Protoss minion discount | How much less your Protoss minions cost this game. | SC_754 Artanis (2), SC_764 Sentry (1, Deathrattle), SC_753 Photon Cannon (1 if it kills a minion) | Card table (on play / on death); Photon Cannon counts when a minion died inside its play. | "-N" |
| `self-damage` | Damage taken on your turns | Damage your hero has taken on your own turns. | TTN_462 Imprisoned Horror | Tracker: increases of the hero's `DAMAGE` while the player is the current player (armor-absorbed damage is not damage taken). | "N" |
| `silver-hand` | Silver Hand Recruit buff | Attack/Health your Silver Hand Recruits get for the rest of the game. | MEND_800 (+1 Attack), MEND_801 (+1 Health), MEND_803 (+1/+1), CS3_029 Pursuit of Justice | Player tags `SILVER_HAND_RECRUIT_HP_BUFF` (seen, MEND_801), `SILVER_HAND_RECRUIT_ATK_BUFF` (enum). | "+A/+H" |
| `spells-on-friendly` | Spells on friendly characters | Spells you've cast on your own characters. | SCH_139 Devout Pupil, BT_334 Lady Liadrin | Tracker plays: spells whose `CARD_TARGET` is a friendly character (seen on targeted plays). | "N" |
| `spells-on-characters` | Spells on characters | Spells you've cast on any character. | VAC_558 Sea Shanty | Same, any target. | "N" |
| `dragons-summoned` | Dragons summoned | Dragons you've summoned this game. | WW_825 Fye, the Setting Sun | Tracker: minions entering play × race DRAGON. | "N" |
| `ceaseless-expanse` | The Ceaseless Expanse | Cost of The Ceaseless Expanse: 100 less one per card drawn, played or destroyed (both players). | GDB_142 | Tracker: draws + plays + minions died + weapons/locations destroyed of both players. Approximate: cards destroyed in hand or deck are not seen. | "Cost N" |
| `timethief-rafaam` | Timethief Rafaam | Different Rafaams you've played (Timethief needs the other 9). | TIME_005 and TIME_005t1–TIME_005t9 | Tracker: distinct plays of TIME_005t1–t9; total Rafaam plays (Giant Rafaam) in details. | "N/9" |
| `tyrande` | Tyrande | Spells left that cast twice. | EDR_464 Tyrande | Tracker: 3 minus spells played after the last Tyrande. | "N left" |
| `void-soul` | Void Soul | Void Souls played; the next one summons a demon that costs 1 + N. | JAIL_732 (from JAIL_730, JAIL_733, JAIL_891) | Tracker: plays. Checked against the card's own number (`TAG_SCRIPT_DATA_NUM_1`) in a real log. | "N-Cost demon" |
| `wheel-of-death` | Wheel of Death | Turns until Wheel of DEATH!!! destroys the enemy hero. | TOY_529 | Tracker: 5 own turns after the last play. Approximate (the game keeps the countdown on enchantment TOY_529e1). | "N turns" |
| `ysondre` | Ysondre | Times your Ysondre has died this game (Dragons summoned by the next Deathrattle). | EDR_465 | Tracker: friendly minion deaths. | "N" |
| `incindius` | Eruption damage | Damage of your Eruptions: 1 + Incindius upgrades. | VAC_321 Incindius (Eruption VAC_321t) | Tracker: Incindius' end-of-turn `TRIGGER` blocks. | "N damage" |

Notes:

- "Your" counts are kept for both players; what the opponent did is seen once their cards are revealed (plays always
  are). Counters that need hidden information (Discovers) are shown for you only.
- Fye, the Setting Sun (WW_825) counts Dragons *summoned*; it moved from the `dragons` (played) counter to
  `dragons-summoned`.
- "This turn" tags reset when the turn changes; `NUM_CARDS_PLAYED_THIS_TURN` of the player whose turn ended is reset
  one turn later, so it is read only on the player's own turn.

## Skipped

| HDT counter | Why |
|---|---|
| Anachronos turns (RLK_919) | "Send all other minions 2 turns into the future": whether that means the caster's turns or game turns is not stated, the countdown lives on a hidden enchantment (RLK_919e) and no game in the logs played it. The minions' return cannot be predicted reliably. |
| Ashalon, Ridge Guardian (TLC_229t14) | Shows which Adaptations Ashalon chose. The choice is in the log for the local player only and the Adapt option names would need their own table; the reward itself is already a revealed card. |
| Deathwing discount (CATA_497 Ultraxion) | "Reduce Deathwing's Cost by ({1})": the amount is a text placeholder that grows with Heralds, not stated in the card text; the discounted Deathwing (CATA_190h) shows its own cost in hand. |
| Kil'jaeden (GDB_145) | "Each turn, they gain an additional +2/+2": when the portal grows (each turn or each own turn) is not stated, and the value lives on hidden enchantments (GDB_145e/e2); not in the logs. |
| Leylines from The Arcanomicon (MEND_505) | The chosen upgrade (MEND_505t/t2/t3) is only visible for the local player's own pick; Ley Walker, Runesaber and Surge Needle are counted. |
