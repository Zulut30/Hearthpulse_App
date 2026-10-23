---
name: hearthpulse-testing
description: Use when writing, fixing or running Hearthpulse tests (tests/Hearthpulse.Tests, xunit) - synthetic Power.log lines, JSON fixtures, fake HttpMessageHandler instead of the network, temp-folder data isolation, Linux CI (ubuntu-latest) with WindowsFact/UnixFact, the module initializer that isolates the data folder, and why a long --artifacts-path breaks SQLite. Use it before claiming "tests pass" and whenever a test fails only on one OS, culture or machine.
---

# Hearthpulse tests

One project: `tests/Hearthpulse.Tests` (net10.0, xunit 2.9, global `using Xunit`). It references
**`src/Hearthpulse.Core` only** - WPF/Avalonia view models are not testable from here, so logic that needs a test
belongs in Core (a common review finding). Related: [hearthpulse-tracking](../hearthpulse-tracking/SKILL.md),
[hearthpulse-counters](../hearthpulse-counters/SKILL.md), [hearthpulse-review](../hearthpulse-review/SKILL.md).

## Run

```
dotnet restore tests/Hearthpulse.Tests/Hearthpulse.Tests.csproj --source https://api.nuget.org/v3/index.json
dotnet test tests/Hearthpulse.Tests/Hearthpulse.Tests.csproj --no-restore
dotnet test tests/Hearthpulse.Tests/Hearthpulse.Tests.csproj --no-restore --filter "FullyQualifiedName~CountersTests"
```

- No NuGet source is configured on this machine; `dotnet test` does not take `--source`, so restore first.
- Parallel sessions: add `--artifacts-path <dir>` to restore and test so outputs do not collide. **Keep `<dir>`
  short** (e.g. `%TEMP%\hpa`): under a long scratchpad path the native `e_sqlite3.dll` fails to load
  ("file name or extension is too long", 0x800700CE) and every `HistoryStore` test fails with
  `TypeInitializationException` - that is the path, not your change.
- The test output is localised on this machine (Russian `dotnet` messages): grep for `Не пройден` / `пройдено` as
  well as `Failed` / `Passed`.

## Baseline (checked on claude/quality-hardening-431c90 + main 433e8bf, Oct 2026)

780 tests: 779 pass, 1 skipped (a `[UnixFact]` on Windows), **no known failures** (Debug and Release). The two old
ones are fixed: ranks are stored culture-independently (`RankInfo.Text`), and the Battlegrounds view test replays the
fixture `Fixtures/BattlegroundsGame`; runs over `D:\Hearthstone\Logs` are `[RealLogsFact]` (skipped without the logs).

Anything failing is yours. Report counts as "X passed, none failed" and compare the total with the baseline - a
smaller total after a merge means tests were lost.

## Style

- File per feature: `<Feature>Tests.cs`, `public sealed class`, method names are sentences with underscores:
  `A_hidden_creator_is_not_named_through_its_dbf_id`, `Counter_modes_default_to_auto`.
- Assert behaviour the player sees (view records, history rows), not private state.
- Tests that pin a rule (no hidden information, golden handling, atomic writes) are as important as happy paths;
  add the negative test when you close a leak.

### Power.log input: synthetic lines in the test

There are **no Power.log fixture files**. Each test file builds a game from inline lines and feeds a `PowerParser`
with its own listeners:

- `CountersTests.Play(params string[] bodies)` -> `(GameState, ConstructedTracker)`; helpers `Card`, `PlayFromHand`,
  `Cast`, `Die`, `View`, `Value(view, id, opponent)`. Ranked game, local player 1, opponent 2.
- `BattlegroundsCountersTests.Play` / `Start(gameType, bodies)`; helpers `Entity`, `PlayerEnchant`, `Zone`,
  `PlayerTag`, `Counter`.
- `OpponentHandTests`, `ParserTests`, `DecksLogTests`, `GameFactsTests` - same idea.
- Cards come from a tiny inline `CardDb.Parse("enUS", json)` or `CardDb.Empty`.

Make the synthetic lines follow the **real** order (e.g. `TURN=1` is set before the opening deal; the Coin can be a
skin such as `JAIL_COIN3` with `COIN_CARD=1`). Copy the shape from a real log - see "Find a tag in real logs" in
[hearthpulse-tracking](../hearthpulse-tracking/SKILL.md). A test built on an imagined order passed while the feature
failed on real games (deck-hand review M2).

### Real-log tests

`RealLogTests`, `AdvisorTests`, `CombatOddsTests`, `DiscoverTests`, one test in `HistoryMigrationTests` read
`D:\Hearthstone\Logs` and return early when it is missing. Keep that pattern for exploratory checks, but never make
a feature's only test depend on local logs - it proves nothing on CI.

### JSON fixtures

`tests/Hearthpulse.Tests/Fixtures/` (copied to output by the csproj): `card-pool.json.gz` (built by
`tools/cardpool/pool_fixture.py`) and `Mulligan/*.json` (API answers: `{id}-decks|matchups|mulligan.json`,
`archetypes.json`, `hsguru-*.json`). Each test class reads them with its own small `Fixture(name)` helper via
`Path.Combine(AppContext.BaseDirectory, "Fixtures", ...)`. When you add fixture data, record how it was produced
(script or exact request) so the numbers can be reproduced.

### No network: fake `HttpMessageHandler`

Every network client takes an `HttpClient`, so tests pass `new HttpClient(handler)`:

| Fake | Where | Pattern |
|---|---|---|
| `FixtureServer` | `ApiLoadTests` | routes by path/query fragment to fixture files, 404 otherwise, records `Requests`; `new HearthpulseData(new HearthpulseApi(new HttpClient(server), "https://api.test/"), cacheDir)` |
| `FakeSite` | `AccountTests` | stateful device-flow site (code, token rotation, revoke, `/me`), `Offline`, held responses via `TaskCompletionSource`; `new AccountClient(new HttpClient(site), FakeSite.Base)` |
| `Stub(status, body)`, `Offline` | `UpdateTests` | `UpdateService.CheckAsync(new HttpClient(new Stub(HttpStatusCode.OK, json)))` |

Write a private nested `HttpMessageHandler` subclass in the test file; there is no shared mock library. Time and
delays are injected too (`AccountService.Delay`, a swappable clock) - never `Task.Delay` real seconds.

### Data isolation

- Tests run in parallel (no collections). `TestDataFolder` (a `[ModuleInitializer]`) points `HEARTHPULSE_DATA` at a temp
  folder before anything reads `AppPaths` and deletes it at the end: never set it in a test. Read the shared log with
  `TestDataFolder.ReadLog()` - a plain `File.ReadAllText` locks out `Diag` in a parallel test and that line is lost.
- A `GameSession` in a test: set `Clock`, `IsHearthstoneRunning` and `ReadsMemory = false` (internal hooks), so time is
  yours and a client running on the machine is not read.
- Use a unique temp path and clean it in `Dispose`: `Path.Combine(Path.GetTempPath(), "hp-<area>-" + Guid.NewGuid().ToString("N"))`
  or `Directory.CreateTempSubdirectory("hl-platform-")`.
- Pass paths into the class under test (`new TokenStore(path)`, `new HistoryStore(dbPath)`,
  `new HearthpulseData(api, cacheDir)`). `AppPaths` is static and creates the real folder on first use - code you
  want to test should take a path instead of reading `AppPaths`.
- Changing `CultureInfo.CurrentCulture` is per thread: set it in the test and restore it in `finally`
  (`AccountTests.The_session_note_uses_english_month_names_under_any_culture`).

### Platform-specific tests (Linux CI)

`.github/workflows/ci.yml` runs the tests on `windows-latest` (whole solution) **and** `ubuntu-latest` (Avalonia app
+ tests); `release.yml` also tests on Linux. A Windows-only assumption turns CI red:

- `[WindowsFact]` / `[UnixFact]` are defined at the bottom of `tests/Hearthpulse.Tests/AccountTests.cs`
  (`FactAttribute` subclasses setting `Skip`). Use them for behaviour that really differs per OS
  (`FileShare.None` locking, DPAPI, 0600 file modes). There is no `WindowsTheory`; add one beside them if needed.
- Prefer OS-neutral tests: e.g. simulate a failed write by putting a directory where the temp file goes (what
  `596a54b` did) instead of relying on Windows file locks.
- Paths: `Path.Combine`, no `\` literals; dates: avoid midnight/UTC edges (use local noon).

## Checklist for a new feature

1. Core logic has tests in `tests/Hearthpulse.Tests/<Feature>Tests.cs`; rule-type behaviour has negative tests.
2. No real network, no real data folder, no dependency on `D:\Hearthstone\Logs` for the main assertion.
3. Runs on Linux (no Windows APIs without `[WindowsFact]`), any culture, any time zone.
4. Hard-coded counts updated where the suite pins them (e.g. the BG counter totals in
   `BattlegroundsCountersTests`, unique ids in `CountersTests.Counter_modes_default_to_auto`).
5. Full run: total = baseline + your new tests, none failing.
