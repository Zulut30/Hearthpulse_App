# Shared presentation sources

This directory contains one maintained implementation of the view models and display records used by the WPF
and Avalonia apps. It is a shared-source directory, not a standalone project or a new assembly. Both apps compile
these files into their existing assemblies, so the runtime still has separate platform types and instances.

`Hearthpulse.App.csproj` and `Hearthpulse.UI.csproj` import `Hearthpulse.Presentation.projitems` after setting
`HearthpulsePresentationFlavor` to `Wpf` or `Avalonia`. The import includes this directory's C# files and defines
`HEARTHPULSE_WPF` or `HEARTHPULSE_AVALONIA`; an invalid/missing flavor fails compilation. Platform conditionals
retain the original `Hearthpulse.App.*` and `Hearthpulse.UI.*` namespaces. Existing XAML names, typed bindings,
public member types and assembly identities therefore remain in place.

## Where changes belong

| Area | Shared source | Local platform code |
|---|---|---|
| Main window state | `Main/MainViewModel*.cs` | App/UI `Main/MainViewModel*.cs`: dispatch, clipboard, established platform behavior; WPF Constructed/theme features |
| Deck details and match replay | `Main/Decks/DeckDetailsViewModel.cs` (its rows: `DeckDetailsRows.cs`), `Main/Match/MatchViewModel.cs` | Views and code-behind in each UI |
| Overlay state and rows | `Overlay/OverlayViewModel*.cs`, `Overlay/OverlayModels*.cs` | `OverlayViewModel.Platform.cs`, `OverlayModels.Platform.cs` |
| Property notification | `Infrastructure/Observable.cs` | `RelayCommand` stays local because command notification semantics differ |
| Tracking and statistics algorithms | `src/Hearthpulse.Core` (outside this directory) | Hosts dispatch completed snapshots/results to the UI |
| XAML, controls, resources and rendering | None | Each App/UI/desktop host retains its framework implementation |

For a common feature, add or change the presentation partial once here, then wire the views in both UIs. New C#
files are picked up by the shared import. Follow an existing file's conditional namespace/import pattern. Add a
small platform partial or hook only when the framework or established behavior actually differs; keep common
formatting, selection, snapshot mapping and collection updates shared.

The remaining platform differences are intentional compatibility boundaries. For example, WPF odds shares are
`GridLength` while Avalonia exposes `double`; hand offsets use each framework's `Thickness`; dispatcher timers
and account clipboard actions remain local. Main-window history counts, title wording, uppercase tile labels,
WPF meta/theme features and Avalonia compact behavior retain their previous contracts. Do not silently
normalize these differences while moving code. Preserve constructor defaults, public properties, event ordering
and bindings, including properties present on only one platform.

`Main/Stats` under App/UI contains views and code-behind. Its common presentation calculations live in
`Main/MainViewModel.MyStats.cs` here, with domain aggregates in Core. There is no second set of standalone Stats
model files to keep synchronized.

## Verification

Build both App and UI after changing shared sources: each compiles a different conditional branch. The usual
`tests/Hearthpulse.Tests` project remains net10.0 and covers Core/Avalonia, including Headless binding checks.
The separate `tests/Hearthpulse.Presentation.Tests` project targets net10.0-windows and references both apps to
check presentation behavior and retained platform differences. Run this additional suite on Windows:

```text
dotnet restore tests/Hearthpulse.Presentation.Tests/Hearthpulse.Presentation.Tests.csproj --source https://api.nuget.org/v3/index.json
dotnet test tests/Hearthpulse.Presentation.Tests/Hearthpulse.Presentation.Tests.csproj -c Release --no-restore
```

Use short, isolated artifact paths if another session is building. Preserve the existing tests' scratch-data
isolation. Test counts are evidence from a specific run, not a fixed architectural acceptance threshold.
Compilation and contract tests do not replace rendered PNG review when the visible layout or bindings change.
