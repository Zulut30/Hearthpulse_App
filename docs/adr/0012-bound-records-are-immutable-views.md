# Records the UIs bind to implement `IImmutableView`

Display rows and views are immutable records: a change builds a new record and the view model swaps it in. WPF does not
know that. A binding to a CLR property of an object without `INotifyPropertyChanged` subscribes to it through
`PropertyDescriptor.AddValueChanged` (`ValueChangedEventManager`), under a process-wide lock, and unsubscribes the same
way when the binding moves to another record. On the match page every replay step re-binds the table's cards, and that
bookkeeping was about a third of a step's UI-thread time (bench `match`, trace of 2026-11-07); the subscription also
keeps the record alive until the binding lets go.

`Hearthpulse.Core.IImmutableView` derives from `INotifyPropertyChanged` with an empty default implementation of the
event. A record that lists it is watched through the ordinary weak-event path and never raises anything. It changes
neither the record's equality nor its JSON.

## Consequences

- Every public record in `src/Hearthpulse.Presentation`, and every Core record a view binds to (`MatchViews.cs`,
  `MatchPlayerContract.cs`, `MatchAnalysis.cs`, the card pool and ladder views), lists `IImmutableView`. A new display
  record does the same.
- A record that really changes in place is a class deriving from `Observable` instead; `IImmutableView` is only for
  values that never change.
- Avalonia's compiled bindings do not need it; it costs them nothing.
