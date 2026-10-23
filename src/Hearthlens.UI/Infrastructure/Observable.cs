using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Hearthlens.UI.Infrastructure;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// View models rebuild their lists on every snapshot, up to ten times a second. A new list with the same items
    /// made WPF throw away and rebuild every row (and set its card art again) each time, which a slow PC could not
    /// keep up with: the overlay froze. A list whose items are all equal by value counts as unchanged. Items that only
    /// have reference equality (mutable classes) always count as changed, so their rows still refresh.
    /// </summary>
    private static bool SameItems<T>(T field, T value)
    {
        if (field is not System.Collections.IList left || value is not System.Collections.IList right || left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            var item = left[i];
            if ((item is not null && !HasValueEquality(item.GetType())) || !Equals(item, right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> ValueEquality = new();

    private static bool HasValueEquality(Type type) => ValueEquality.GetOrAdd(type, t =>
        t.IsValueType || t == typeof(string)
        || t.GetMethod(nameof(Equals), [typeof(object)])?.DeclaringType is { } declaring && declaring != typeof(object));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value) || SameItems(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute) : this(_ => execute())
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
