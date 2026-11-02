# Zero warnings with the Meziantou and VS Threading analyzers; a finding is fixed or suppressed in place

The shipped code in `src/*` builds with `Meziantou.Analyzer` and `Microsoft.VisualStudio.Threading.Analyzers`
(`Directory.Build.props`) and stays at 0 warnings. `.editorconfig` lists every rule that differs from the package
default, each with its reason (no JoinableTaskFactory in this app, `ConfigureAwait(false)` only in the UI-free Core,
string collections already ordinal by default). A remaining finding is either fixed or suppressed at the exact place
with `[SuppressMessage(..., Justification = "...")]`; no project-wide `NoWarn` for analyzer rules.

## Considered options

- **Default rule severities.** Hundreds of findings that change nothing here (e.g. `StringComparer.Ordinal` on
  collections that are already ordinal); the signal drowns.
- **`NoWarn` per project.** Hides the reason: a reader of the affected line cannot see why the rule is off.

## Consequences

- The `.editorconfig` comment is the record of why a rule is off; change the rule and its comment together.
- `tests/*` and `tools/*` are not analysed yet: there the findings were test literals, console output and deliberate
  idioms. Roadmap package Q7 adds a softer rule set for them, makes CI fail on any warning and reviews the
  `SuppressMessage` attributes in `src`; this ADR is updated then.
