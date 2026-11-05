# Mutation testing

Stryker.NET (`dotnet-stryker` 5.0.0, a local tool in `dotnet-tools.json`; Apache-2.0 per the package's LICENSE, a dev
tool that is not part of any build, listed in `THIRD-PARTY-NOTICES.txt`) changes one operator, condition, string or
call at a time in the code below and runs the tests that cover it: a change no test notices (a **survivor**) marks a
behaviour nothing checks. It runs by hand only, in CI: Actions → Mutation tests → Run workflow
(`.github/workflows/mutation.yml`, Windows, since Stryker builds the whole solution with the WPF app). A full run takes
hours, so it never runs on the owner's PC; locally, at most one small target at Idle with the game closed:

```bash
dotnet tool restore --add-source https://api.nuget.org/v3/index.json
dotnet dotnet-stryker -f tests/mutation/likely-deck.json -c 2
```

## Targets

| Target (`tests/mutation/<name>.json`) | Code | Why |
|---|---|---|
| `replay-share` | `Replay/ReplayShare.cs` | The copy of a replay that leaves the machine |
| `replay-sanitize` | `Replay/ReplayImport.cs` (report counts `Sanitize` and the `Clean*` methods) | The import filter: what a foreign file may bring in |
| `crash-report` | `Diagnostics/CrashReport.cs` | What a crash report sends: the personal-data scrubber |
| `likely-deck` | `Constructed/LikelyDeck.cs` | What the overlay tells the player about the opponent's deck |
| `combat-simulator` | `Battlegrounds/CombatSimulator.cs`, `CombatDamage.cs` | The combat odds; runs only the simulator's own 405 tests (all 2,500 took more than two hours) |

## First run (2026-11-05, before the tests below)

Score = killed / (killed + survived + not covered); mutants that do not compile or sit in ignored code are left out.

| Target | Killed | Survived | Not covered | Score | Of covered |
|---|---|---|---|---|---|
| `replay-share` | 13 | 5 | 11 | 45% | 72% |
| `replay-sanitize` (filter methods) | 183 | 67 | 26 | 66% | 73% |
| `crash-report` | 28 | 17 | 3 | 58% | 62% |
| `likely-deck` | 23 | 10 | 30 | 37% | 70% |
| `combat-simulator`: `CombatSimulator.cs` | 169 | 51 | 0 | 77% | 77% |
| `combat-simulator`: `CombatDamage.cs` | 54 | 7 | 0 | 89% | 89% |

`likely-deck`'s "not covered" are all in `Sample`, the render scene's fixed list. `replay-share`'s are the error paths
of writing and cleaning up (`IOException`), `replay-sanitize`'s the summary rebuilt for an old Battlegrounds file.

## Closed (tests added, each red against its mutant)

| Survivor | Risk | Test |
|---|---|---|
| CrashReport: the OS user name matched case-sensitively | A user name in another case reaches the report | `CrashReportTests.The_os_user_name_goes_only_as_a_whole_word_and_keeps_the_frame` |
| CrashReport: the marks `<email>`, `<ip>`, `<number>`, the length limit, five exceptions, empty frames | A report that is unreadable or longer than it should be | `CrashReportTests.Emails_addresses_and_long_numbers_leave_their_own_marks`, `A_message_is_cut_only_past_its_limit`, `A_report_keeps_five_exceptions_and_no_empty_frames` |
| ReplayShare: `All` → `Any` in the mode check | A mode like `../Duos` in the shared file's name | `ReplayShareTests.A_mode_that_is_not_one_word_of_letters_names_no_file` |
| ReplayImport: a bad card id on a board, in the lobby or the final board; a round result outside Win/Loss/Tie | A foreign file brings paths or text into the library | `ReplayImportTests.A_bad_minion_hero_or_result_in_battlegrounds_data_refuses_the_file` |
| ReplayImport: `GameType`, `PlayerClass`, `Result` kept unchecked | The same, through the summary | `ReplayImportTests.Summary_words_that_are_not_words_are_dropped` |
| LikelyDeck: names and costs of rows, unknown cards, copies of a card and its reprint summed, zero plays, the core fallback | A wrong list shown to the player | `OpponentDeckTests.Rows_name_and_cost_the_cards_and_played_copies_add_up_across_reprints`, `A_list_of_unknown_cards_falls_back_to_the_core_and_without_one_there_is_none` |

`likely-deck` re-run after its tests: 10 survivors → 2 (an equivalent `> 0` → `>= 0` inside a sum, and the name order of
two cards of one cost).

## Worth closing next

- **replay-sanitize, limits:** the counts at the edge (`> 200` rounds, `> 8` lobby heroes, `> 14` minions, `> 16` list
  items, `> 1000` shop cards, the event limits) survive as `>=`: no test sits exactly on a limit. One theory per limit,
  at the limit and one past it.
- **replay-sanitize, constructed summary:** `RebuildConstructedSummary` (result words, turns `(turns + 1) / 2`, the
  opponent's class) is checked only through other fields; a test on an old constructed file's summary would cover it.
- **replay-sanitize, Battlegrounds steps:** whether the opponent stays out of view outside combat (`fight`,
  `opponentInView` start values) survives: a step list where the first step is not a fight.
- **replay-share, clean-up:** a copy exactly a day old, and the count of deleted folders.
- Equivalent or harmless, leave: the GUID format and the time format in `CrashReport` (a server field), the `Sample`
  scene in `LikelyDeck`, `Safe(list) ?? []` on lists that are never null by then.

## Combat simulator

Run 42067756832, the simulator's own tests only. Most of the 58 survivors change nothing the player sees:

- **Equivalent:** the tie rate computed first (`(done - wins - losses) / done`) is replaced in every branch below it,
  and the branches for "all wins", "all losses" and "all ties" give what the general one would; the line and the
  blocks can go in a later clean-up.
- **The seed** (`Seed`, FNV-1a over the boards): a field left out only changes which random numbers a board gets.
- **Cancellation:** `HardeningTests.CancelledSimulationStopsEarly` checks it, but that class was not in the target's
  test filter in this run; it is now.
- **Closed:** a spent time budget stops exactly at `MinRuns`
  (`EngineTests.A_spent_time_budget_stops_exactly_at_the_minimum_runs`).
- **Worth closing next:** the damage tally when one side has no minions (the outcome sign passed to `DamageTally`),
  `LethalDealt` when the opponent's health is unknown, and the keyword flags `Poisonous`, `Venomous`, `Stealth`,
  `Windfury`/`MegaWindfury` built from a minion's properties (`CombatSimulator.cs` lines 70-72: no test reads
  `Keywords` back), and `CombatDamage`'s percentile and equality code (lines 81-95).
