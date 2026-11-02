# `Loc.Instance` stays a static singleton

Roadmap package Q3 moves ambient statics (`AppPaths`, `ReplayStore`/`ReplayLibrary`/`ReplayUploads.Default`,
`ArchetypeTranslations.Current`, `EffectRegistry.Default`) into constructors. `Loc.Instance`
(`src/Hearthpulse.Core/Localization/Loc.cs`) is kept: both UIs' XAML binds to it through the `{i:Loc Key}` markup
extension, which has no constructor to receive a dependency, and the interface language is one process-wide value
that updates every bound text at once. Passing a `Loc` through every view model would add a parameter everywhere for
no gain in testability: tests switch the language on the instance and restore it.

## Consequences

- New code reads strings through `Loc.Instance` (or the markup extension) and does not add another localisation
  service.
- Any other new static needs the same kind of justification here; the default is a constructor parameter.
