# The statics left after package Q3 are immutable tables, test seams or one process-wide preference

Package Q3 moved the ambient services (`AppPaths`, the replay stores, `ArchetypeTranslations.Current`) into
constructors. A grep for `static ... Default|Instance|Current` and for settable statics over `src/` (2026-11-07) leaves
these, kept on purpose:

| Static | Kind | Why it stays |
|---|---|---|
| `Loc.Instance` | process-wide service | ADR 0007 |
| `EffectRegistry.Default` (`Core/Battlegrounds/Effects`) | immutable table | Built once by reflection from Core's own effect classes; it is a function of the assembly, holds no state and has no other value in production. Callers that need another set already pass one (`SimOptions.Effects`, `BattlegroundsTracker.Effects`, tests use `EffectRegistry.Empty` / `FromAssembly`). |
| `SimOptions.Default`, `ClassScope.Default`, `PendingMurlocs.Instance`, `Loc.Formatter.Instance` | immutable values | A default value or a stateless marker object, not a service. |
| `GameProcess.MayBeRunning`, `ClientConfig.ClientStartTime` (`internal ... { get; set; }`) | test seams | Internal, set only by tests (and the bench by reflection) to stand in for the Win32 window probe and the client's start time, and always restored. Turning them into constructor parameters would thread a probe through the static `HearthstoneInstall`/`ClientConfig` helpers and every caller for no change in production. |
| `Motion.Reduced` (`Presentation/Infrastructure`) | process-wide preference | The "reduce motion" setting read by the platform `Motion` helpers that views call from code-behind and styles; like the interface language it is one value for the whole process, set by the host when settings load or change. |

## Consequences

- New code still takes its dependencies through constructors; a new static needs a row here with its reason.
- A test seam stays `internal`, is restored by the test that sets it, and is never set by production code.
