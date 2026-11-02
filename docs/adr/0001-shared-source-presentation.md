# Shared view models are compiled into each UI, not shipped as a library

Both UIs need the same view-model behaviour, but WPF and Avalonia differ in the types those view models expose
(`Visibility`, `GridLength`, `Thickness`, brushes, dispatchers), and each app's XAML binds to view models in its own
namespace. We keep one copy of the view models in `src/Hearthpulse.Presentation` and compile it into
`Hearthpulse.App` and `Hearthpulse.UI` through `Hearthpulse.Presentation.projitems`, with
`HearthpulsePresentationFlavor` (`Wpf` / `Avalonia`) defining `HEARTHPULSE_WPF` / `HEARTHPULSE_AVALONIA`. Each app keeps
its CLR namespaces, assembly identity and XAML bindings; platform differences live in small local partials.

## Considered options

- **A framework-neutral class library** referenced by both apps. Rejected: every platform type would need an
  abstraction or an adapter layer, and every existing XAML binding and namespace would change at once.
- **Two copies kept in step by review.** The state before this decision (merged in `950e0ab`): every fix had to be
  made twice, and a copy that missed one diverged.

## Consequences

- A change to shared code must build both apps (each compiles a different `#if` branch) and run
  `tests/Hearthpulse.Presentation.Tests` on Windows, which references both.
- Common behaviour is edited once in Presentation; views, controls and `RelayCommand` stay in each UI
  ([the presentation map](../../src/Hearthpulse.Presentation/README.md)). Intentional platform differences are kept,
  not silently normalised.
