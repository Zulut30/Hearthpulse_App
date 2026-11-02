# Zero warnings with the Meziantou and VS Threading analyzers; a finding is fixed or suppressed in place

Every project builds with `Meziantou.Analyzer` and `Microsoft.VisualStudio.Threading.Analyzers`
(`Directory.Build.props`) and stays at 0 warnings; CI fails on any warning (`HEARTHPULSE_STRICT` in `ci.yml`). `.editorconfig` lists every rule that differs from the package
default, each with its reason (no JoinableTaskFactory in this app, `ConfigureAwait(false)` only in the UI-free Core,
string collections already ordinal by default). A remaining finding is either fixed or suppressed at the exact place
with `[SuppressMessage(..., Justification = "...")]`; no project-wide `NoWarn` for analyzer rules.

## Considered options

- **Default rule severities.** Hundreds of findings that change nothing here (e.g. `StringComparer.Ordinal` on
  collections that are already ordinal); the signal drowns.
- **`NoWarn` per project.** Hides the reason: a reader of the affected line cannot see why the rule is off.

## Consequences

- The `.editorconfig` comment is the record of why a rule is off; change the rule and its comment together.
- `tests/*` and `tools/*` get a softer set (`.editorconfig`, section `[{tests,tools}/**.cs]`): the correctness rules
  stay warnings (unobserved awaitables, async void, `==` against an overridden `Equals`, finalizers); culture, string
  comparison, allocation and blocking-call rules are off, since there the findings were test literals, console output
  and deliberate fixture I/O.
- Only CI is strict. NuGet vulnerability advisories (NU1901-NU1904) stay warnings even there: one published overnight
  must not stop every branch. The release workflow does not set the strict switch.
- Fire-and-forget work on the UI thread is a `void` method that calls `SomethingAsync().Forget("...")`, not `async void`
  (roadmap Q7, 2026-11-02). The `SignIn` / `SignOut` commands keep their async lambdas: `RelayCommand` takes an
  `Action` in both UIs, and an exception they do not expect should still reach the crash window.
