# Battlegrounds counters — specification

Numbers Battlegrounds cards care about, shown as chips on the BG overlay (panel `bg-counters`) and listed under
Settings > "Counters and mulligan" > Battlegrounds with the same Auto / Always / Off modes as the Constructed counters
(`AppSettings.CounterModes`, ids prefixed `bg-`). Code: `src/Hearthpulse.Core/Battlegrounds/BattlegroundsCounters.cs`.

The set follows the counter names of other trackers (names only). Everything below comes from card texts
(HearthstoneJSON enUS `cards.json`, Oct 2026), the GameTag enumeration of HearthSim's MIT-licensed `python-hearthstone`
(`hearthstone/enums.py`, `class GameTag`) and real Power.log lines. No other tracker's code was read.

## Sources checked

| Log | What it is | Used for |
|---|---|---|
| `D:\Hearthstone\Logs\Hearthstone_2026_10_23_02_01_43\Power.log` | current client (build 254477), one BG Solo game, 3 turns, no relevant cards | tag names the client prints today, player enchantments, button costs |
| `D:\Hearthstone\Logs\Hearthstone_2026_10_21_*` | Ranked only (no BG game kept) | — |
| `D:\BGSim\output\h10-source\139963.power.log` | full BG game from 2022 (build 139963, Naga/Spellcraft season), first place | how events look (zone moves of combat copies, deaths, summons, `NUM_SPELLS_PLAYED_THIS_GAME` on the player). Not evidence that a tag exists today. |

Facts from those logs that the counters rely on:

- Each player's tags are on the player entity (`Player EntityID=… PlayerID=…`, later named by BattleTag in TAG_CHANGE lines).
- Combat boards are copies: `SETASIDE -> PLAY` inside a TRIGGER block, with `COPIED_FROM_ENTITY_ID` set. Minions that die in
  combat go `PLAY -> GRAVEYARD` (copies and minions summoned during combat alike). Tokens summoned in combat are new entities
  without `COPIED_FROM_ENTITY_ID`.
- Player enchantments are ENCHANTMENT entities with `ATTACHED = <player entity id>` in zone PLAY
  (e.g. `Bacon_Free_Refresh_Player_Ench`, `Bacon_TagTransferPlayerE`). None of the counter enchantments below appeared yet.
- The client prints tags it has no name for as numbers (e.g. `tag=4741`, `tag=4901`). The counters never guess which number
  is which tag; a tag that only ever shows as a number keeps its counter unconfirmed.
- The reroll button `TB_BaconShop_8p_Reroll_Button` (local player, zone PLAY) changes `COST`. At the start of a turn it
  briefly reads 0 and is set back within the same trigger (`0 -> 1` in 2026, `0 -> 2` in 2022); after Refreshing Anomaly
  (`BGS_116`, 2022 log) it stays 0 until the free Refreshes are used. The overlay reads settled state, so only the
  lasting 0 shows.
- The lobby's Deity: `BACON_GLOBAL_OLD_GOD_DBID` on the game entity (130610 = `BGFYM_000` C'Thun in the 2026 game).

## When a counter shows

- **Auto**: confirmed counters show once a related card is seen in the tavern, hand, board, trinkets or hero power, or once
  their count is above zero. Unconfirmed counters show only when their tag or enchantment has actually arrived in the log
  (no default value is ever assumed).
- **Always**: always shown; an unconfirmed counter without data shows `—`.
- **Off**: never.

## Confirmed by the logs (events or tags seen in real Power.log lines)

| Id | Counter | What it counts | Source | Related cards |
|---|---|---|---|---|
| `bg-eternal-knight` | Eternal Knight | Friendly Eternal Knights that died this game; value is the bonus each Knight has (+4/+2 per death, golden +8/+4) | local player's minions with card id `BG25_008`/`BG25_008_G` moving `PLAY -> GRAVEYARD` (combat copies count; a Reborn Knight dying twice counts twice, as the text says "each … that died") | `BG25_008` Eternal Knight, `BG25_009` Eternal Summoner, `BG34_403` Eternal Tycoon, trinkets `BG30_MagicItem_301`, `BG36_MagicItem_216` Eternal Portrait |
| `bg-automaton` | Ancestral Automaton | Ancestral Automatons the player summoned this game; value is the bonus of a new one (+3/+2 per other, golden +6/+4) | local player's `BG_TTN_401`/`_G` entering PLAY for the first time without `COPIED_FROM_ENTITY_ID` (played from hand or summoned; combat copies do not count) | `BG_TTN_401`, trinket `BG30_MagicItem_303` Automaton Portrait |
| `bg-naga-spells` | Spells cast | Spells the player cast this game (Tavern spells, Spellcraft spells, Blood Gems played from hand…) | player tag `NUM_SPELLS_PLAYED_THIS_GAME` (seen on the BG player in the 2022 log). Detail: progress to the next "every 3 spells" step | `BG31_924` Thaumaturgist, `BG31_925` Showy Cyclist, `BG31_035` Groundbreaker, `BG35_921` Abyssal Bruiser, `BG28_633` Felboar, trinkets `BG32_MagicItem_808` Bloodbound Earrings, `BG32_MagicItem_930` Archaic Scroll, `BG32_MagicItem_932` Vash'jir Anemone, `BG32_MagicItem_934` Dazzling Dagger, `BG35_MagicItem_920` Bubble Crown, `BG36_MagicItem_305` Rune of Transmutation |
| `bg-free-refresh` | Free refresh | Whether the next Refresh is free (and how many, when the count tag arrives) | confirmed: reroll button `COST` = 0. Count: player tag `BACON_FREE_REFRESH_COUNT` (enum 4536, **not yet seen**) — shown only when it arrives | `BGS_116` Refreshing Anomaly, `BG28_827` Leaf Through the Pages, `BG36_330` Sly Infiltrator, `BGDUO_110` Feisty Freshwater, `TB_BaconShop_HP_063` Nozdormu's Clairvoyance, trinkets `BG32_MagicItem_888`, `BG32_MagicItem_901`, `BG35_MagicItem_930`, `BG36_MagicItem_200`, reward `BG33_Reward_004` |

## From the tag enumeration / card data, waiting for a live game

Not one of these tags or enchantments appeared in the available logs. Each definition in code carries the comment
"not yet confirmed in a real Power.log" and its source. Enchantment values are read from `TAG_SCRIPT_DATA_NUM_1` (Attack)
and `TAG_SCRIPT_DATA_NUM_2` (Health), the fields card texts call `SDN1`/`SDN2` (e.g. `BG26_135e` "Get SDN1 extra Gold next
turn"); several enchantments of one counter are summed (one per source card).

| Id | Counter | What it shows | Source (enum id / enchantment) | Related cards |
|---|---|---|---|---|
| `bg-blood-gem` | Blood Gem | Stats one Blood Gem gives: +(1 + attack bonus)/+(1 + health bonus) | player tags `BACON_BLOODGEMBUFFATKVALUE` (1844), `BACON_BLOODGEMBUFFHEALTHVALUE` (2827); the simulator reads the same tags (`Effects/attack/BloodGems.cs`). Enchantment `BG26_159pe` "Tracks the size of this player's Blood Gems" exists but is not read | `BG23_017` Sanguine Champion, `BG30_123` Fearless Foodie, `BG33_885` Sanguine Refiner, `BG29_708` Pokey Thornmantle, `BG32_433` Dreaming Thornweaver, `BG31_893` Gem Day, reward `BG27_Reward_815`, trinkets `BG30_MagicItem_410`, `BG30_MagicItem_988`, `BG35_MagicItem_431t`, `BG35_MagicItem_433` |
| `bg-blood-gem-barrage` | Blood Gem Barrage | Stats Blood Gem Barrage gives Tavern minions after each Refresh | enchantment `BG34_689e2` "Minions in the Tavern have increased stats from Blood Gem Barrage" | `BG34_689` Blood Gem Barrage, `BG34_682` Razorfen Flapper, `BG34_683` Briarback Drummer, `BG36_341` Veteran Brigand |
| `bg-elemental-tavern` | Tavern Elementals | Extra stats of Elementals in the Tavern | enchantments `BG_ShopBuff_Elemental`, `BG31_815pe`, `BG28_707e`, `BG30_MagicItem_544pe`, `BG21_020pe`, `BG26_162pe`, `BG32_814pe`, `BG32_843pe`, `BG34_855pe`, `BGS_104pe` ("Elementals in the Tavern have increased stats…") | `BG31_815` Dune Dweller, `BG28_707` Living Azerite, trinkets `BG30_MagicItem_544` Nomi Sticker |
| `bg-elemental-extra` | Elemental bonus | Extra stats your Elementals give | player tags `BACON_ELEMENTAL_BUFFATKVALUE` (4002), `BACON_ELEMENTAL_BUFFHEALTHVALUE` (4001) | trinkets `BG32_MagicItem_802` Fountain Pen, `BG36_MagicItem_380` Amplifying Essence, `BG32_MagicItem_830` Felemental Portrait |
| `bg-undead` | Undead Attack | Attack your Undead have this game | enchantment `BG25_011pe` "Undead Bonus Attack Player Enchant" | `BG25_011` Nerubian Deathswarmer, `BG28_604` Butchering, `BG34_690` Plaguerunner, `BG34_692` Forsaken Weaver, trinkets `BG30_MagicItem_547` Comfy Coffin, `BG32_MagicItem_276` Enigmatic Headstone, `BG30_MagicItem_989` Artisanal Urn |
| `bg-tavern-spells` | Tavern spells | Extra stats your Tavern spells give | player tags `TAVERN_SPELL_ATTACK_INCREASE` (3989), `TAVERN_SPELL_HEALTH_INCREASE` (3990); the simulator reads them (`Effects/attack/CrimsonVindicator.cs`). Enchantments `BG31_830pe`/`BG32_861pe` exist but are not read | `BG32_237` Intrepid Botanist, `BG32_341` Humon'gozz, `BG32_821` Felfire Conjurer, `BG32_835` Tranquil Meditative, `BG32_860` Shoalfin Mystic, `BG32_880` Friendly Geist, `BG33_830` Azsharan Cutlassier, `BG33_924` Blue Whelp, `BG35_341` Enchanted Sentinel, `BG36_104` Dark Puppeteer, `BG36_300` N'raqi Frostcaller, Rakanishu `TB_BaconShop_HP_085t`, reward `BG33_Reward_020`, several trinkets (`BG32_MagicItem_270/700/801`, `BG35_MagicItem_920`, `BG36_MagicItem_371/373/418`) |
| `bg-random-tavern` | Random Tavern minion | Stats a random minion in the Tavern gains | enchantment `BG34_854pe` "Random minion Tavern Minion Buff Player Ench" no card text in the current data names this buff (the minion's enchantment is `BG34_854e` "Eastern Winds"); the chip has no related cards and shows only when the enchantment arrives |
| `bg-volumizer` | Volumizers | Extra stats your Volumizers have | enchantment `BG34_170pe` "Volumizer Player Enchant" | `BG34_170` Auto Accelerator, `BG34_170t/t2/t3` Red/Blue/Green Volumizer, `BG34_171` Conveyor Construct, `BG36_366` Resourceful Robot, `BG36_367` Auto Reveille, trinket `BG36_MagicItem_423` Volumizer Portrait |
| `bg-whelp` | Whelps | Extra stats your Whelps have | enchantment `BG34_402pe` "Tracks the size of this player's Whelp Buffs" | `BG34_402` Burgeoning Whelp, `BG34_631` Whelp Watcher, Onyxia `BG22_HERO_305p` |
| `bg-beetles` | Beetles | Extra stats your Beetles have (on top of the 2/2 token) | enchantment `BG31_808pe` "Beetle Army Player Enchant: Gives Beetle stats" | `BG31_801` Forest Rover, `BG31_803` Buzzing Vermin, `BG31_808` Runed Progenitor, `BG31_809` Turquoise Skitterer, `BG36_209` Ravaging Scorpid, `BG28_603` Boon of Beetles, trinket `BG32_MagicItem_860` Beetle Band |
| `bg-deity` | Deity | Stats of your sleeping Deity | player tags `BACON_OLD_GOD_ATTACK` (4914), `BACON_OLD_GOD_HEALTH` (4915), by name only. The lobby Deity (`BACON_GLOBAL_OLD_GOD_DBID`, confirmed) names the chip | `BGFYM_000` C'Thun, `BGFYM_011` Y'Shaarj, `BG36_110` Joyous, `BG36_099` Brain Rotter, `BG36_106` Cutthroat K'Thir, `BG36_108` Vicious Mindslasher, `BG36_109` The Shadow of Doubt, `BG36_111` Sha of Fear, `BG36_113` Drifting Sacrifice, `BG36_318` Faceless Converter, `BG36_371` Energizing Chamber, `BGFYM_005` Harbinger Aph'lass, trinkets `BG36_MagicItem_404/416/417/602` |
| `bg-gold-next-turn` | Gold next turn | Extra Gold next turn minus Gold lost next turn | player tags `BACON_PLAYER_EXTRA_GOLD_NEXT_TURN` (4286), `BACON_PLAYER_OVERDRAWN_GOLD_NEXT_TURN` (4287). Enchantments `BG26_135e` (+SDN1) / `BG28_513e` (−SDN1) exist but are not read | `BG26_135` Southsea Busker, `BG28_800` Careful Investment, `BG28_513` Reckless Investment, Gallywix `TB_BaconShop_HP_008`, trinket `BG30_MagicItem_923` Elementium Chest, anomalies `BG27_Anomaly_575`, `BG31_Anomaly_116` |
| `bg-fodder` | Fodder | Fodders in your next Refresh | player tag `BACON_FODDERS_IN_REFRESH` (4664; the enum also calls 4664 `BACON_FODDER_TOOLTIP`). Enchantment `BG35_150pe` exists but is not read | `BG35_150` Laboratory Assistant, `BG35_155` Twisted Wrathguard, `BG36_730` Trapped Clapper, trinkets `BG35_MagicItem_151` Defiler Portrait, `BG36_MagicItem_830` Bloodfury Shield |

## Skipped

None of the 17 names is skipped outright, but two parts are left out:

- The extra +4/+2 per friendly Undead death from Greater Eternal Portrait (`BG36_MagicItem_216pe`, "Counts the number of
  Eternal Knights that died") is not added to `bg-eternal-knight`: how the enchantment stores it is unknown.
- "Gold in 2 turns" (`BG31_825e2`) is not part of `bg-gold-next-turn`.

## Replay of the real logs (Oct 2026)

Counters at the start of each combat, replayed by a scratch console over both BG logs (values other than `—`/0):

- 2026 game (3 turns): nothing to show (no related cards; one friendly minion died, one summoned).
- 2022 game (16 turns, Naga lobby): `bg-naga-spells` 1, 1, 3, 4, 6, 9, 13, 15, 17, 20, 23 from turn 6 on (the player
  tag's last value is 23); `bg-free-refresh` = Free at turn 13, after Refreshing Anomaly was played. No unconfirmed tag
  or enchantment appeared, as expected.

## Checking on a live game

When a game with these cards is logged, look for the tag names above on the player entity (by BattleTag) and for the
enchantments with `ATTACHED` = the player entity. If a tag shows only as a number, leave its counter unconfirmed. Move a
counter to the confirmed table once its values match what the cards show in the game.
