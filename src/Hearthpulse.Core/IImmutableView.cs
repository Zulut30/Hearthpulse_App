using System.ComponentModel;

namespace Hearthpulse.Core;

/// <summary>
/// An immutable record the UIs bind to. Its values never change (a new record replaces it), so it raises nothing; it
/// only declares <see cref="INotifyPropertyChanged"/> so WPF watches it through the cheap weak-event path. A binding to
/// a property of an object without the interface makes WPF subscribe through <c>PropertyDescriptor.AddValueChanged</c>
/// under a process-wide lock; on the match page that was about a third of a replay step's UI time (bench <c>match</c>,
/// 2026-11-07), and the subscription holds the record until the binding lets go.
/// </summary>
public interface IImmutableView : INotifyPropertyChanged
{
    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
    {
        add { }
        remove { }
    }
}
