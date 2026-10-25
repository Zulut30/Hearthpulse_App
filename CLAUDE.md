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

Agent `hearthpulse-legal` (`.claude/agents/`): ban risk, licences, Blizzard IP and privacy check before a release or
a merge that touches game memory, input, third-party code/data, paid features or user data. Report goes to
the optional sibling `../reference/reviews/LEGAL-*.md` after verifying it exists, or the agreed workspace report
location if it is absent.

Ground rules: restore with `--source https://api.nuget.org/v3/index.json`; run the app and `--render-ui` only with
`HEARTHPULSE_DATA` pointing at a copy of the data folder; hidden game information: during a game - only what the
player can see; after the match the review may show everything the log revealed by the end of the game (the match
page only, `docs/match-replay.md`); every user-facing feature goes into both UIs.

