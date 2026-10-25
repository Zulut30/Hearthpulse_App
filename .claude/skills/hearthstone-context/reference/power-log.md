# Hearthstone logs and how Hearthpulse reads them

## Where logs come from

- The client writes logs only for sections enabled in **`log.config`**. Hearthpulse writes it itself
  (`LogConfig.EnsureAll` in `src/Hearthpulse.Core/Hearthstone/HearthstoneInstall.cs`). Required sections:
  `[Power]` (Verbose=True), `[LoadingScreen]`, `[Decks]`, `[Arena]`, `[FullScreenFX]`, `[Achievements]`, each with
  `LogLevel=1`, `FilePrinting=True`, `ConsolePrinting=False`, `ScreenPrinting=False`.
  Location: Windows `%LOCALAPPDATA%\Blizzard\Hearthstone\log.config`; macOS
  `~/Library/Preferences/Blizzard/Hearthstone/log.config`; Linux/Wine inside the prefix
  (`HearthstoneInstall.LogConfigPathFor`). The client reads it at start, so changes need a client restart.
- **`client.config`** in the install folder: the client stops writing a log for the rest of its session once it passes
  `Log.FileSizeLimit.Int` KB (default 10000 = ~10 MB, around BG turn 8). `ClientConfig` sets
  `[Log] FileSizeLimit.Int=-1` (no limit). `LogWatcher` reports a Power.log stuck at the cap.
- Files live in `<install>/Logs/Hearthstone_yyyy_MM_dd_HH_mm_ss/` (one folder per client session;
  `HearthstoneInstall.LatestLogSession`). Hearthpulse tails `Power.log`, `Decks.log`, `LoadingScreen.log`
  (`LogWatcher`); `LogBackfill` imports finished games from all kept `Power.log`/`Power_old.log`.

## Power.log line shape

```
D 16:44:59.2148622 GameState.DebugPrintPower() - <body>
```

`PowerParser.LineRegex`: `^[DWEI] <time> GameState.(DebugPrint\w+)() - <body>`. Only `GameState.*` lines are parsed;
the body is trimmed, indentation only expresses nesting. Methods handled:

| Method | Content |
|---|---|
| `DebugPrintPower` | the game tree (below) |
| `DebugPrintGame` | `GameType=GT_...`, `FormatType=FT_...`, `ScenarioID=`, `BuildNumber=`, `PlayerID=n, PlayerName=...` |
| `DebugPrintEntityChoices` | `id=.. Player=.. TaskList=.. ChoiceType=MULLIGAN|GENERAL ...` then `Entities[i]=...` (Discover, hero pick, trinkets, mulligan) |
| `DebugPrintEntitiesChosen` | the pick that was made |

`PowerTaskList.*` lines (the client's on-screen replay) are not parsed except one signal:
`Entity=GameEntity tag=BOARD_VISUAL_STATE value=2` = combat is on screen (Battlegrounds).

## DebugPrintPower bodies (as in `tests/Hearthpulse.Tests/ParserTests.cs`)

```
CREATE_GAME
    GameEntity EntityID=1
        tag=CARDTYPE value=GAME
    Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115198130930503 lo=11]
        tag=CONTROLLER value=1
FULL_ENTITY - Creating ID=4 CardID=CORE_CS2_029
    tag=ZONE value=HAND
    tag=CONTROLLER value=1
    tag=CARDTYPE value=SPELL
FULL_ENTITY - Creating ID=5 CardID=            <- hidden card: no CardID yet
TAG_CHANGE Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=5 zone=HAND zonePos=1 cardId= player=2] tag=ZONE value=DECK
SHOW_ENTITY - Updating Entity=[... id=9 ...] CardID=EX1_001      <- card revealed
BLOCK_START BlockType=ATTACK Entity=[...] EffectCardId=...
BLOCK_END
```

- `CREATE_GAME` starts a game; `GameEntity` is the game itself (turn, step, lobby-wide BG tags); each `Player`
  entity has a `PlayerID` (1/2 in 1v1; in Battlegrounds the opponents are represented differently, see below).
- `FULL_ENTITY` creates an entity followed by indented `tag=X value=Y` lines; `SHOW_ENTITY`/`CHANGE_ENTITY` reveal or
  transform it (new `CardID`); `HIDE_ENTITY` hides it again; `TAG_CHANGE` changes one tag.
- `Entity=` references are a bare id, a name (`GameEntity`, a player name) or a bracket form
  `[entityName=.. id=.. zone=.. zonePos=.. cardId=.. player=..]`.
- `BLOCK_START`/`BLOCK_END` group what one action caused: `ATTACK`, `TRIGGER`, `POWER`, `PLAY`, `DEATHS`... The
  tracker uses them, e.g. an ATTACK inside a TRIGGER block of a hero power/minion/quest reward is a
  start-of-combat attack.
- Tag values are names (`ZONE value=PLAY`) or numbers. `GameTag` (`src/Hearthpulse.Core/Power/GameTag.cs`) holds the
  tag names used and maps `ZONE` and `PLAYSTATE` names to numbers.

## Zones (`GameTag.Zones`)

`PLAY` (board, hero, hero power), `DECK`, `HAND`, `GRAVEYARD`, `REMOVEDFROMGAME`, `SETASIDE` (created-but-unplaced
cards, e.g. Discover options), `SECRET`, `INVALID`. `Entity` exposes `Zone`, `ZonePosition`, `Controller`,
`InPlay`/`InHand`/`InDeck`, and tracks `CreatedTurn`, `EnteredHandTurn`, `Mulliganed`, `IsOriginalDeckCard`.

## Tags the app relies on (constants in `GameTag`)

Common: `ZONE`, `ZONE_POSITION`, `CONTROLLER`, `CARDTYPE`, `TURN`, `STEP`, `STATE` (`COMPLETE` ends the game),
`PLAYSTATE` (`WON`/`LOST`/`TIED`/`CONCEDED`...), `MULLIGAN_STATE`, `HERO_ENTITY`, `FIRST_PLAYER`, `CREATOR`,
`CREATOR_DBID`, `DISPLAYED_CREATOR`, `ATK`, `HEALTH`, `DAMAGE` (current health = HEALTH - DAMAGE), `ARMOR`, `COST`,
`PREMIUM` (golden), keywords `TAUNT`, `DIVINE_SHIELD`, `POISONOUS`, `VENOMOUS`, `REBORN`, `WINDFURY`,
`MEGA_WINDFURY`, `STEALTH`, `SECRET`, `CLASS`, `CARDRACE`, `TECH_LEVEL`, `ATTACHED` (enchantment target),
`TAG_SCRIPT_DATA_NUM_1/2` (per-card counters).

Battlegrounds: `PLAYER_ID`, `PLAYER_TECH_LEVEL`, `PLAYER_LEADERBOARD_PLACE`, `PLAYER_TRIPLES`,
`NEXT_OPPONENT_PLAYER_ID`, `BACON_CURRENT_COMBAT_PLAYER_ID`, `BOARD_VISUAL_STATE`, `BACON_DUMMY_PLAYER`,
`BACON_GLOBAL_ANOMALY_DBID`, `BACON_GLOBAL_OLD_GOD_DBID` (Deity), `BACON_MAX_PLAYER_TECH_LEVEL`,
`BACON_DUO_TEAM_ID`, `BACON_COMBAT_DAMAGE_CAP` + `BACON_COMBAT_DAMAGE_CAP_ENABLED` (game entity; the combat damage cap).
`BACON_CURRENT_COMBAT_PLAYER_ID` is missing in 2022 logs (build 139963), so the tracker records no combats there.

## Game types and formats

`GameRecords.ModeOf` maps `GameType`: contains `BATTLEGROUNDS` (+`DUO` -> Duos), `MERCENARIES`, `GT_RANKED`,
`GT_CASUAL`, contains `ARENA`, `GT_TAVERNBRAWL`/`GT_FSG_BRAWL*` -> Brawl, `GT_VS_AI` -> Practice, `GT_VS_FRIEND` ->
Friendly, else Other; `GT_TUTORIAL` is never recorded. `FormatType`: `FT_STANDARD`, `FT_WILD`, `FT_CLASSIC`,
`FT_TWIST` (ranks are read per format from memory).

## Battlegrounds in the log (as the tracker reads it)

- Lobby heroes are hero entities carrying `PLAYER_ID`, health/armor, `PLAYER_TECH_LEVEL`, `PLAYER_LEADERBOARD_PLACE`,
  `PLAYER_TRIPLES`. Hero pick is a choice before the local hero is set (`IsHeroSelection`).
- Outside combat the opposing side of the board holds Bob's tavern offer (shop minions and tavern spells); during
  combat it holds the current opponent's board (`BattlegroundsTracker` keeps each opponent's last seen board).
- Choices: trinket offers arrive as a `GENERAL` choice of trinket cards (`BATTLEGROUND_TRINKET` or `_MagicItem_` ids);
  other picks (Discover from hero powers, spells, minions, triple reward, Dark Gift) are fresh `SETASIDE` cards.
- Other card types seen: `HERO_POWER`, `BATTLEGROUND_SPELL`, `BATTLEGROUND_TRINKET`, `BATTLEGROUND_QUEST_REWARD`,
  `ENCHANTMENT`.

## Other logs

- `Decks.log`: deck chosen for the game (`### <name>`, `# Deck ID: <id>`, deck code line) -> `GameSession`.
- `LoadingScreen.log`: scene changes (menu, `GAMEPLAY`...) -> `GameSession` scene.
