---
name: hearthpulse-dev
description: Use when implementing or fixing Hearthpulse functionality and you need the map - projects, Core folders (logs, Power parser, trackers, counters, card pool, mulligan, My Stats, accounts, API, history DB, updates), data flow and threads, data folder, coding conventions, Battlegrounds combat effects (clean-room), and how to build (restore needs an explicit NuGet source). Start here for any code task; it points to the recipe skills (tracking, counters, overlay panel, main page, data sources, testing, release, review).
---

# Working on Hearthpulse functionality

Hearthstone deck tracker + in-game overlay, .NET 10. Solution: `Hearthpulse.slnx`. For visuals see the
`hearthpulse-design` skill; for game concepts see `hearthstone-context`.

Recipes: [hearthpulse-tracking](../hearthpulse-tracking/SKILL.md), [hearthpulse-counters](../hearthpulse-counters/SKILL.md),
[hearthpulse-overlay-panel](../hearthpulse-overlay-panel/SKILL.md), [hearthpulse-main-page](../hearthpulse-main-page/SKILL.md),
[hearthpulse-data-sources](../hearthpulse-data-sources/SKILL.md), [hearthpulse-testing](../hearthpulse-testing/SKILL.md),
[hearthpulse-visual-check](../hearthpulse-visual-check/SKILL.md), [hearthpulse-release](../hearthpulse-release/SKILL.md),
[hearthpulse-review](../hearthpulse-review/SKILL.md), [hearthpulse-parallel-work](../hearthpulse-parallel-work/SKILL.md).

## Project map

| Project | Target | Purpose |
|---|---|---|
| `src/Hearthpulse.Core` | net10.0 | All logic, UI-free. Only package: `Microsoft.Data.Sqlite`. |
| `src/Hearthpulse.App` | net10.0-windows, WPF, AssemblyName `Hearthpulse` | The Windows app the installer ships. |
| `src/Hearthpulse.UI` | net10.0, Avalonia 12.1.4 | Cross-platform views/view models ("preview"). |
| `src/Hearthpulse.Desktop` | net10.0, AssemblyName `Hearthpulse` | Avalonia desktop host: windows, per-OS overlay platform. |
| `tests/Hearthpulse.Tests` | net10.0, xunit | Tests; references Core only. |
| `tools/Hearthpulse.Probe` | net10.0 | Dev probe: `heroes <Power.log>`, `mem`, `class <Name>` (read live client memory). |
| `tools/effects/*.py` | Python | Generators for `CombatRelevance.g.cs` and effect pools (`*Pool.g.cs`). |
| `tools/counters/gen_counter_cards.py` | Python | Generates `Tracking/Counters/CounterCards.g.cs` from enUS HearthstoneJSON. |
| `tools/cardpool/*.py` | Python | `gen_pool_tables.py` -> `CardPool/CardPoolTables.g.cs`; `pool_fixture.py` -> test fixture `card-pool.json.gz`. |

Core folders:

- `Logs/LogWatcher.cs` - polls the newest `Logs/Hearthstone_*` session folder; tails `LoadingScreen.log`,
  `Decks.log`, `Power.log` (`LogTail`), resyncs, detects the 10 MB log cap.
- `Power/` - `PowerParser` (GameState.DebugPrint* lines -> entities/tags/blocks/choices, `IPowerListener`),
  `GameState`, `Entity`, `GameTag` (tag name constants).
- `Tracking/` - `GameSession` (live pipeline, publishes immutable `LiveSnapshot` via `SnapshotChanged`, at most every
  100 ms), `BattlegroundsTracker`, `ConstructedTracker`, `MemoryPoller` (memory reads on their own thread),
  `GameRecords` (finished game -> history row, `ModeOf(gameType)`), `Views.cs` (records the UI binds:
  `BattlegroundsView`, `ConstructedView`, `LiveSnapshot`...).
- `Memory/` - read-only access to the Hearthstone process: `ProcessMemory`, `MonoRuntime`, `MonoOffsets`,
  `HearthstoneMemory` (BG rating solo/duos, ranks, lobby tribes, BG button costs). Windows only
  (`HearthstoneMemory.IsSupported`). Never write to game memory.
- `Hearthstone/HearthstoneInstall.cs` - install/log folder discovery per OS; `LogConfig.EnsureAll` writes the needed
  `log.config` sections; `ClientConfig` sets `Log.FileSizeLimit.Int=-1` in `client.config`.
- `Cards/` - `CardDb` (HearthstoneJSON `https://api.hearthstonejson.com/v1/latest/{locale}/cards.json`, cached in
  `AppPaths.Cache`, plus hsbg.cards via `BgCardSource`), `Card`, `DeckString` (deck codes), `Races`.
- `Battlegrounds/` - `BattlegroundsMeta` (community stats: heroes/minions from the Hearthpulse API, the rest from the
  stats pool when set up; `docs/bg-meta-sources.md`),
  `BattlegroundsAdvisor` (hero/trinket/shop/Discover/comp advice), `CombatSimulator` + `Simulation/`
  (`Fight`, `Side`, `Unit`, `Traits`) + `Effects/` (per-card combat effects), `BattlegroundsCounters` +
  `BattlegroundsCounterTracker` (BG counter chips, spec `docs/counters-battlegrounds.md`).
- `Constructed/ConstructedMeta.cs` - ranked Constructed stats (archetypes, decks, mulligan), loaded from the Hearthpulse
  API by `ApiConstructedMeta` (HSGuru meta/decks + HSReplay mulligan, names via `ArchetypeNames`; docs/constructed-meta-api.md).
- `Data/HistoryStore.cs` (SQLite `history.db`), `Data/LogBackfill.cs` (imports games from kept Power logs). Columns added
  after the first schema go in `HistoryStore.AddedColumns` (ALTER TABLE on open, NULL in old rows): `combat_results`
  (BG combats JSON), `offered_heroes`, `anomaly`, `rank_star_level`, `rank_legend`, `went_first`, `deck_id`, and the deck
  page's card columns `mulligan`, `cards_drawn`, `cards_played`, `opponent_cards`, `opponent_archetype` (JSON via
  `Data/GameCards.cs`; lists read them as NULL, `HistoryStore.WithCards` reads them for one deck's games).
  `Tracking/GameFactsTracker.cs` keeps the BG hero offer for the row; `GameFactsTracker.Cards.cs` the Constructed
  mulligan, draws, plays and the opponent's played deck cards (only what it showed).
  Deck families (merged deck versions): `HistoryStore.Families.cs` (tables `deck_families`, `deck_family_members`,
  `deck_family_dismissed`); a version is `DeckRecords.DeckKey` (card list of the code, else "name|CLASS"),
  `HistoryStore.FillDecks` carries family keys to the card list when it gives a game its code.
- `Stats/` - My Stats aggregates, pure functions over `GameRecord`s: `StatsFilter` (period, format), `BattlegroundsStats`
  (summary, rating timeline, per-hero stats vs `MetaHero` placement shares / combat win rate), `ConstructedStats`
  (rank timeline, classes, matchups, length, archetypes), `ArenaStats` (runs by deck id, else class + loss limit),
  `DeckFamilies` (versions, families, merge suggestions, card diff; `WithFamily` for the overlay deck record),
  `DeckDetails` (deck page: summary, win-rate line, matchups by class/told archetype, per-card mulligan and play stats),
  `ArchetypeGuess` (opponent archetype from its played cards, only a clear call).
- `Replay/` - match replay (`docs/match-replay.md`): `ReplayRecorder` (IPowerListener writing only what the player saw:
  card backs, new ids across hidden zones), `ReplayStore` (`<data>/replays/*.hpr` gzip, 300 MB / 3000 files, history
  column `replay`), `MatchReplay` (frames/turns/actions), `MatchPage` + `MatchAnalysis` (what the match page of both
  apps shows; opened from History). Battlegrounds rounds come from `BattlegroundsTracker.Rounds.cs`.
- `Updates/UpdateService.cs` - GitHub releases of `Zulut30/Hearthpulse_App` (`RepoOwner`/`RepoName` constants),
  download + staged install.
- `AppSettings.cs` (settings.json), `AppPaths.cs` (+ `Diag` log), `ThemePalette.cs`.
- `Api/HearthpulseApi.cs` (client for `api.kolodahearthstone.com`, `HEARTHPULSE_API` env override; `{"data"}` and
  bare answers, `{"detail"}` errors) and `Api/HearthpulseData.cs` (every Constructed number: HSGuru meta, lists and
  per-archetype analysis, HSReplay archetypes/mulligan/matchups; plus the Battlegrounds heroes and minions per MMR slice
  read by `BattlegroundsMeta.LoadAsync`). Answers are cached in `cache/api` (6 h, written
  atomically; a 404 is cached as `{"data":null}`).
- `Accounts/` - sign-in with a hearthpulse.net account via the OAuth device flow (`AccountClient`, `AccountService`,
  `TokenStore` = DPAPI on Windows, `Entitlements` = paid access, read from `/me`; no feature is gated yet). Contract: `docs/accounts-api.md`
  (`HEARTHPULSE_SITE_API` for a test site). UI: Settings, "Hearthpulse account" in both apps.
- `Tracking/Counters/` - in-game counters. `CounterTracker` (IPowerListener) records plays/summons/deaths/draws;
  `Counters.cs` defines every counter (player tags seen in real logs: `CTHUN_ABILITY1..4_USED`,
  `HERALD_COLOSSAL_AMOUNT`, `CORPSES`, `NUM_SPELLS_PLAYED_THIS_GAME`, `QUEST_PROGRESS`/`_TOTAL`...). Which cards make a
  counter relevant comes from `CounterCards.g.cs`, generated by `tools/counters/gen_counter_cards.py` from enUS
  HearthstoneJSON - regenerate after a patch. The overlay applies each counter's mode (`AppSettings.CounterModes`).
- `CardPool/` - Discover/random-card pool ("what can this card give"): `CardPoolCatalog` (all cards from the API
  `api/v1/constructed-cards`, HearthstoneJSON fallback, `cache/api/card-pool.json`), `CardPoolEngine`, `PoolTextParser`,
  `CardPoolTables.g.cs`, `CardPoolTracker` (IPowerListener; `GameSession.CardPool.cs`). Spec: `docs/discover-pool.md`.
  Overlay panels `card-pool` and `card-pool-browser`.
- `Tracking/ConstructedTracker.cs` also builds the deck panel and the opponent's hand (`HandSlot`, only what the
  player can see); `Tracking/OpponentHandFan.cs` places the hand marks.
- `Tracking/Mulligan.cs`, `MulliganGuide.cs`, `MulliganGuideSource.cs`, `MulliganReplay.cs` - mulligan guide (docs/mulligan-v2.md): API archetype
  for the deck, keep/mulligan calls, keep impact; `ConstructedMeta` is the fallback. Shown while the local `MULLIGAN_STATE` is
  INPUT/DEALING; hosts call `GameSession.SetMulliganGuide` and `SetConstructedMeta`.

App/UI folders: `Main/` (main window + `MainViewModel` and its partials `.Account`, `.Counters`, `.BgCounters`,
`.MyStats`, WPF also `.Constructed`, `.Theme`; `Main/Stats/` My Stats views), `Overlay/` (overlay window,
`OverlayViewModel` + partials `.Live`, `.Mulligan`, `.Deck`, `.Hand`, `.CardPool`, `.BgCounters` flatten snapshots,
`MovablePanel` drag/resize panels, extracted panels `MulliganPanel`, `OpponentHandPanel`, `CardPoolPanel`,
`CardPoolBrowser`), `Infrastructure/` (converters, `CardArt`, `Observable`, WPF
`ThemeEngine`, `Native`, `Autostart`), `Controls/`. WPF composition root: `App.xaml.cs` (+ `DevRender.cs`); Avalonia:
`Hearthpulse.UI/AppHost.cs`, `Hearthpulse.Desktop/DesktopApp.cs`, `Desktop/Platform/*OverlayPlatform.cs`.

## Data flow

```
Hearthstone writes Logs/Hearthstone_<date>/Power.log, Decks.log, LoadingScreen.log
  -> LogWatcher (poll + tail) -> GameSession.OnLines
       Power.log -> PowerParser.Feed -> GameState/Entity -> IPowerListener:
                    ConstructedTracker (+ CounterTracker), BattlegroundsTracker (+ BattlegroundsCounterTracker),
                    GameFactsTracker, CardPoolTracker, GameSession  (registered in GameSession.OnSessionStartedCore)
       Decks.log -> deck being played; LoadingScreen.log -> scene
  + MemoryPoller -> HearthstoneMemory (rating, ranks, tribes, button costs)
  + CardDb (names/stats), BattlegroundsMeta / ConstructedMeta (community stats), BattlegroundsAdvisor (Enrich)
  -> LiveSnapshot (SnapshotChanged) -> host dispatches to UI thread
  -> MainViewModel / OverlayViewModel (WPF and Avalonia each have their own)
  finished game -> GameRecords.Build -> HistoryStore (SQLite)
```

Combat odds: `BattlegroundsTracker` captures both boards at the first regular attack and runs `CombatSimulator`
(up to 4000 fights, stops after 200 ms, at least 500) on the log thread.

## Data folder (`AppPaths`)

- `%APPDATA%\Hearthpulse` (Windows), `~/Library/Application Support/Hearthpulse` (macOS),
  `~/.local/share/Hearthpulse` (Linux). `HEARTHPULSE_DATA` overrides it (use a scratch folder in experiments).
- A legacy `Hearthlens` folder is copied (never moved) to `Hearthpulse` on first start, without its logs and
  `updates/`, so an old install keeps its own data. Always set `HEARTHPULSE_DATA` to a scratch copy when running the
  app or `--render-ui` during development.
- Contents: `settings.json`, `history.db`, `account.dat` (signed-in session; never copy it into scratch data),
  `updates/` (staged update), `hearthpulse.log` (`Diag.Info/Debug/Error`, rotated at 4 MB to
  `.old.log`), `cache/` (card JSON, meta), `cache/images/`.

## Conventions

- Match the surrounding style: file-scoped namespaces, `sealed` records/classes, `/// <summary>` on public members,
  explanatory comments about *why*, `CultureInfo.InvariantCulture` for parsing/formatting machine data.
- Logic belongs in Core; keep view models thin. A user-facing feature goes into **both** UIs (WPF `Hearthpulse.App`
  and Avalonia `Hearthpulse.UI`) - see the checklists in `hearthpulse-main-page` and `hearthpulse-overlay-panel` (accepted gaps: `hearthpulse-design`).
- New settings: property with a default and a `<summary>` on `AppSettings` (JSON via System.Text.Json, enums as
  strings, saved atomically via temp file). Bind through the view models.
- Runtime diagnostics go through `Diag` (`hearthpulse.log`); `Console` is only used by the `--render-ui` dev path.
- No hidden information: the simulator only uses what the player can see (no opponent hand/secrets; see Effects
  README).

## Battlegrounds combat effects (clean-room rule)

Read `src/Hearthpulse.Core/Battlegrounds/Effects/README.md` before touching effects. Essentials:

- **Clean-room**: implement from card text/golden text (HearthstoneJSON/hsdata) and general BG rules. Do not read, run
  or copy another simulator's code - this includes the HDT/Firestone reference index (see "External code and data").
- One class per card (or per group with identical behaviour) in its own file under `Effects/attack|deaths|ongoing/`,
  marked `[CardEffect("<normal card id>")]`; registration is by reflection (`EffectRegistry`), no list to edit.
- Golden: same class, branch with `self.Golden` / `self.Pick(normal, golden)` following the golden card's own text.
- Effects are shared across threads: no mutable fields; per-minion state in `Unit.Counter`/`Counter2`/`AvengeCount`,
  per-player state in `Side.Counter(key)`.
- Override only the hooks you need (`Deathrattle`, `OnAttack`, `AfterFriendlyDeath`, `AuraFor`...; Avenge derives
  from `AvengeEffect`). Use `Tokens.*` for summoned token stats.
- Tests: `tests/Hearthpulse.Tests/Sim/` (`SimTestKit.Fight(Side(...), Side(...), oursFirst, picks)`) and
  `tests/Hearthpulse.Tests/Effects/{attack,deaths,ongoing}/`. `RelevanceTests.Built_in_effects_register_cleanly`
  catches duplicate registrations.
- Registering an effect removes the "N minion effects not simulated" flag for that card. After a patch regenerate
  `CombatRelevance.g.cs` with `tools/effects/gen_combat_relevance.py`.

## Build and test

This machine has **no NuGet source configured** (`dotnet nuget list source` -> none), so restore explicitly:

```
dotnet restore Hearthpulse.slnx --source https://api.nuget.org/v3/index.json
dotnet build Hearthpulse.slnx --no-restore
dotnet test tests/Hearthpulse.Tests/Hearthpulse.Tests.csproj --no-restore
```

- `dotnet test` does not accept `--source`; restore first.
- If another session is building the same tree, isolate outputs: add `--artifacts-path <dir>` to both
  `dotnet restore` and `dotnet test`. Keep `<dir>` **short** (e.g. `%TEMP%\hpa`) - under a long path `e_sqlite3.dll`
  fails to load and every `HistoryStore` test fails.
- `Hearthpulse.App` targets net10.0-windows; off Windows publish needs `-p:EnableWindowsTargeting=true` (as
  `installer/windows/build.sh` does). The build must stay at 0 warnings.
- Baseline (claude/quality-hardening-431c90 on main 433e8bf, Oct 2026): 780 tests - 779 pass, 1 skipped (`[UnixFact]` on
  Windows; the `[RealLogsFact]` ones skip too where `D:\Hearthstone\Logs` is missing), **no known failures**. Anything failing
  is yours. Details: [hearthpulse-testing](../hearthpulse-testing/SKILL.md).
- UI checks: [hearthpulse-visual-check](../hearthpulse-visual-check/SKILL.md) (`--render-ui` on a copy of the data folder).

## Release

Tags `vX.Y.Z` -> `.github/workflows/release.yml` -> GitHub release in `Zulut30/Hearthpulse_App`, which `UpdateService`
offers to users. Versions, installers, updater and the pre-release checklist: [hearthpulse-release](../hearthpulse-release/SKILL.md).
Tagging/pushing/publishing only with explicit permission.

## External code and data

What may be borrowed from HDT, Firestone, HdtArenaHelper, bgtracker, tavern-ledger and upstream HeartLens, and the
rules for every network source: [hearthpulse-data-sources](../hearthpulse-data-sources/SKILL.md). The reference
index `C:\Users\zulut\Downloads\HeartLens-main\reference\index\` (`hdt.md`, `firestone.md`, `feature-gap.md`,
`ideas-bg-arena.md`, `*.jsonl`, `LICENSES.md`) is for orientation only - never copy code from it, and never consult it
for combat effects (clean-room rule).
