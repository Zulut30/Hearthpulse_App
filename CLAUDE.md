# Hearthpulse

Hearthstone tracker and in-game overlay on .NET 10: WPF app `src/Hearthpulse.App` (what the installer ships),
Avalonia preview `src/Hearthpulse.UI` + host `src/Hearthpulse.Desktop`, domain logic in `src/Hearthpulse.Core`, and
shared view models in `src/Hearthpulse.Presentation`. Solution `Hearthpulse.slnx`.

Presentation is shared source, not another DLL: App/UI import `Hearthpulse.Presentation.projitems` with
`HearthpulsePresentationFlavor` set to `Wpf` / `Avalonia`, retaining their existing CLR namespaces, assembly
identities and XAML bindings. Edit common view-model behavior once there. Keep framework adapters and intentional
platform differences in the small local partials; views, controls and `RelayCommand` stay in each UI. See
[the presentation map](src/Hearthpulse.Presentation/README.md).

Tests: `tests/Hearthpulse.Tests` remains the cross-platform net10.0 Core/Avalonia suite (including Headless binding
checks). `tests/Hearthpulse.Presentation.Tests` targets net10.0-windows, references both UIs and checks their
presentation contracts together; run it on Windows as well when changing shared presentation code.

Feature specs, data contracts and project records: [docs/README.md](docs/README.md). Shared build settings (satellite
languages, no symbols in published apps): `Directory.Build.props` / `Directory.Build.targets`.

Project skills live in `.claude/skills/`. Start from the one that matches the task:

| Task | Skill |
|---|---|
| Project map, data flow, build, conventions | `hearthpulse-dev` |
| Colours, tokens, fonts, styles, theme presets | `hearthpulse-design` |
| Hearthstone rules, Power.log, card ids | `hearthstone-context` |
| Add/change an overlay panel | `hearthpulse-overlay-panel` |
| Add/change a main-window page or tab | `hearthpulse-main-page` |
| Render PNGs and check the UI | `hearthpulse-visual-check` |
| New Power.log tracking, history columns | `hearthpulse-tracking` |
| Add an in-game counter | `hearthpulse-counters` |
| API, site account, HearthstoneJSON, licences | `hearthpulse-data-sources` |
| Writing and running tests | `hearthpulse-testing` |
| Versions, tags, installers, updater | `hearthpulse-release` |
| Reviewing a branch | `hearthpulse-review` |
| Splitting a big task into parallel sessions | `hearthpulse-parallel-work` |
| Task -> sessions -> merge, push, build, restart, no confirmations | `hearthpulse-ship` |
| Task -> lead's card -> designer, developers, QA, reviewer | `hearthpulse-team` |
| UI review of a screen or panel, one ranked report | `better-interface` |
| UI regressions in a branch (reviewer's UI pass) | `interface-review` |
| Keyboard, focus, names, hit areas, motion, scale | `better-accessibility` |
| Grouping, spacing, compact, room for long Russian | `better-layout` |
| Interface text ru/en, errors, empty states | `better-writing` |
| Type ramp, wrapping, trimming, numbers, punctuation | `better-typography` |
| Colour roles, meaning colours, contrast in 7 themes | `better-colors` |
| Polish: radii, depth, MDI glyphs, animation values | `better-ui` |
| One component under hostile data, PNGs (owner-invoked) | `ui-break` |
| Every state of one component, PNGs (owner-invoked) | `ui-states` |
| Variants of one UI piece to pick from (owner-invoked) | `ui-variant` |
| Test first, red -> green, honest tests | `test-driven-development` |
| Acting on QA / review findings | `receiving-code-review` |
| Parallel subagents inside one session | `dispatching-parallel-agents` |
| Module interfaces, seams, deep modules | `codebase-design` |
| Architecture review report (owner-invoked) | `improve-codebase-architecture` |
| Project glossary `docs/GLOSSARY.md`, ADRs `docs/adr/` | `domain-modeling` |
| Interview the owner about a plan, on request | `grilling`, `grill-with-docs` |
| Research from primary sources into a file | `research` |
| Writing skills, agent files, CLAUDE.md | `writing-for-agents` |
| Session retrospective (owner-invoked) | `retro` |
| Re-explain the last message plainly (owner-invoked) | `wait-what` |

The rows from `better-interface` to `ui-variant` are UI skills rewritten for WPF/Avalonia from jakubkrehel/skills;
the last eleven rows are general practices adapted from mattpocock/skills and obra/superpowers (all MIT): sources and
changes in `.claude/skills/THIRD-PARTY-NOTICES.md`.

Team agents (`.claude/agents/`, run by `hearthpulse-team`): `hearthpulse-lead` (task card and work packages),
`hearthpulse-designer` (design.md), `hearthpulse-core-dev` and `hearthpulse-ui-dev` (developer sessions),
`hearthpulse-qa` (acceptance criteria and tests), `hearthpulse-reviewer` (branch review).

Agent `hearthpulse-legal` (`.claude/agents/`): ban risk, licences, Blizzard IP and privacy check before a release or
a merge that touches game memory, input, third-party code/data, paid features or user data. Report goes to
the optional sibling `../reference/reviews/LEGAL-*.md` after verifying it exists, or the agreed workspace report
location if it is absent.

Ground rules: restore with `--source https://api.nuget.org/v3/index.json`; run the app and `--render-ui` only with
`HEARTHPULSE_DATA` pointing at a copy of the data folder; hidden game information: during a game - only what the
player can see; after the match the review may show everything the log revealed by the end of the game (the match
page only, `docs/match-replay.md`); the one in-game exception is BG opponents' MMR from Blizzard's public
leaderboard, off by default, on the conditions in `docs/bg-opponent-mmr.md` (owner, 2026-10-29); every user-facing
feature goes into both UIs; the build stays at 0 warnings, including the Meziantou and VS Threading analyzers on `src/*`
(rule set and reasons in `.editorconfig`: fix a finding, or suppress it in place with a `Justification`); coverage:
`hearthpulse-testing`.

**Default workflow (owner's standing permission, 2026-10-27).** The coordinating session (the one the owner talks to
in `D:\HeartLens-main`) carries every feature or fix task end to end with `hearthpulse-ship`: sessions in worktrees,
verify, merge into `main`, push, rebuild and restart the app, delete the finished sessions, with no "commit? merge?
push?" questions. A session started in a `wt-*` worktree commits to its own `claude/*` branch only and never merges
or pushes. Releases (version bump, tag) still wait for the owner's word.

