---
name: hearthpulse-testing
description: Use when writing, fixing or running Hearthpulse tests (Hearthpulse.Tests and Windows Hearthpulse.Presentation.Tests, xunit) - shared view-model contracts, Avalonia Headless bindings, synthetic Power.log lines, JSON fixtures, fake HttpMessageHandler, temp-folder isolation, WindowsFact/UnixFact, and short --artifacts-path for SQLite. Use it before claiming tests pass and when a test fails only on one OS, culture or machine.
---

# Hearthpulse tests

The cross-platform project `tests/Hearthpulse.Tests` (net10.0, xunit 2.9, global `using Xunit`) references
`src/Hearthpulse.Core` and `src/Hearthpulse.UI`. Avalonia view models can be tested directly; `Avalonia.Headless`
supports UI binding checks without a window/GPU (`LocBindingTests`). WPF is not referenced by this cross-platform
test project. `tests/Hearthpulse.Presentation.Tests` is an additional net10.0-windows suite referencing both App
and UI for presentation contracts. It uses the existing scratch-data initializer and runs on Windows. Keep domain
rules in Core and shared view-model behavior in `src/Hearthpulse.Presentation`; its sources compile into both
existing UI assemblies, so both variants must be checked.
Related: [hearthpulse-tracking](../hearthpulse-tracking/SKILL.md),
[hearthpulse-counters](../hearthpulse-counters/SKILL.md), [hearthpulse-review](../hearthpulse-review/SKILL.md).

## Run

```
dotnet restore tests/Hearthpulse.Tests/Hearthpulse.Tests.csproj --source https://api.nuget.org/v3/index.json
dotnet test tests/Hearthpulse.Tests/Hearthpulse.Tests.csproj --no-restore
dotnet test tests/Hearthpulse.Tests/Hearthpulse.Tests.csproj --no-restore --filter "FullyQualifiedName~CountersTests"
```

For shared presentation or platform adapter changes, also run on Windows:

```
dotnet restore tests/Hearthpulse.Presentation.Tests/Hearthpulse.Presentation.Tests.csproj --source https://api.nuget.org/v3/index.json
dotnet test tests/Hearthpulse.Presentation.Tests/Hearthpulse.Presentation.Tests.csproj -c Release --no-restore
```

Build both UI hosts as well: the shared import selects `HEARTHPULSE_WPF` / `HEARTHPULSE_AVALONIA` from
`HearthpulsePresentationFlavor`. Tests against Avalonia alone cannot validate the WPF compile branch. Linux CI
runs the cross-platform suite; Windows also runs the presentation suite. Keep their results and skips separate.
Preserve intentional differences (public types, constructor defaults, command notifications, platform-only
properties) in contract tests rather than forcing the two UIs to have identical APIs or behavior.

- Restore with the explicit NuGet source above; do not assume another machine's configured sources. `dotnet test`
  does not take `--source`, so restore first.
- Parallel sessions: add `--artifacts-path <dir>` to restore and test so outputs do not collide. **Keep `<dir>`
  short** (e.g. `%TEMP%\hpa`): under a long scratchpad path the native `e_sqlite3.dll` fails to load
  ("file name or extension is too long", 0x800700CE) and every `HistoryStore` test fails with
  `TypeInitializationException` - that is the path, not your change.
- Test output follows the machine's language. When parsing output, handle `Не пройден` / `пройдено` as well as
  `Failed` / `Passed`; prefer a TRX report for machine-readable results.
- Coverage (coverlet, both suites): add `--collect:"XPlat Code Coverage" --results-directory <dir>`; the report is
  `<dir>/<guid>/coverage.cobertura.xml` (per assembly `line-rate` / `branch-rate`; the WPF app is `Hearthpulse`). CI's
  Windows job collects it and writes the per-project table into the job summary (artifact `coverage-windows`).
  Instrumentation makes hot code ~15x slower: a test that asserts on wall-clock time gets
  `[Trait("Category", "Timing")]`, and coverage runs use `--filter "Category!=Timing"` (CI runs those in a step of
  their own).

## Establish the baseline for the change

Run the selected base commit with the same SDK, configuration, OS and fixtures when attributing a failure to a
change. Record its SHA and passed/failed/skipped counts. Historical snapshots are not acceptance thresholds:
`433e8bf` had 780 tests; the Windows Release audit of `7657bb5` found 1165 (1164 passed, one Unix-only skip).
Use the new run's results, not either old count, when reporting current status.

A count difference is a reason to inspect the discovered cases and diff, not proof that tests were lost or that
coverage was preserved. Check the actual behavior and meaningful assertions of added/removed cases.

## Style

- File per feature: `<Feature>Tests.cs`, `public sealed class`, method names are sentences with underscores:
  `A_hidden_creator_is_not_named_through_its_dbf_id`, `Counter_modes_default_to_auto`.
- Assert behaviour the player sees (view records, history rows), not private state.
- Tests that pin a rule (no hidden information, golden handling, atomic writes) are as important as happy paths;
  add the negative test when you close a leak.

### Power.log input: synthetic lines in the test

Most focused parser tests build games from inline lines and feed a `PowerParser` with their own listeners.
The repository also has a real compressed log, `Fixtures/BattlegroundsGame/power.log.gz`, used by repeatable
Battlegrounds replay tests; use a small recorded fixture when ordering matters beyond an isolated event.
Existing inline helpers:

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

Tests that need the developer's kept logs use `[RealLogsFact]`, defined in `RealLogTests.cs`; it reports a skip
when `RealLogTests.LogRoot` (currently `D:\Hearthstone\Logs`) is unavailable. Do not silently return from a test and
count it as passed. Repository fixtures run on every machine. Never make a feature's only assertion depend on a
locally installed game; report which kept-log checks actually ran.

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

- Tests normally run in parallel. `LocBindingTests` uses the nonparallel `GlobalLanguage` collection because it
  changes the shared localization service. `TestDataFolder` (a `[ModuleInitializer]`) points `HEARTHPULSE_DATA` at a temp
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
3. Cross-platform cases run on Linux (no Windows APIs without `[WindowsFact]`), any culture and any time zone.
   WPF/Avalonia contract comparisons belong in the additional Windows presentation project; do not retarget the
   existing net10.0 suite to Windows or silently skip it on Linux.
4. Hard-coded counts updated where the suite pins them (e.g. the BG counter totals in
   `BattlegroundsCountersTests`, unique ids in `CountersTests.Counter_modes_default_to_auto`).
5. Required run succeeds with the intended cases discovered; report passed/failed/skipped and explain changed counts.

## Skill scripts and server code

The .NET suite does not execute Python helpers or the Cloudflare Worker. Run their tests when those files change:

```
python -B -I -m unittest discover -s tools/skill_checks -v
node --test server/pool/test/validation.test.mjs
```

The Python checks require Pillow (visual comparisons) and PyYAML (skill frontmatter). They also verify the catalog's
relative recipe links. Visual checks mock the .NET child process and use temporary images/data: no real GUI,
account, game configuration or network is touched. Node uses its built-in test runner. CI runs both script suites.
