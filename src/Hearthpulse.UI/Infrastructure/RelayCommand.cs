using System.Windows.Input;

namespace Hearthpulse.UI.Infrastructure;

/// <summary>
/// A command that is always executable (no command in either UI is ever disabled through it, so it raises no
/// CanExecuteChanged). It stays in each UI, as views and controls do; the Presentation README says why.
/// </summary>
public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public RelayCommand(Action execute) : this(_ => execute())
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
