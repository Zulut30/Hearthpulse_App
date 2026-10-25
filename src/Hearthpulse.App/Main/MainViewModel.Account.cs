using System.Runtime.InteropServices;
using System.Windows;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.App.Main;

public sealed partial class MainViewModel
{
    private RelayCommand? _copyAccountCode;

    private static void DispatchToUi(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    public RelayCommand CopyAccountCode => _copyAccountCode ??= new RelayCommand(() =>
    {
        try
        {
            Clipboard.SetText(AccountUserCode);
            ShowAccountMessage(() => Loc.T("Main.Account.CodeCopied"));
        }
        catch (ExternalException)
        {
            // Another program holds the clipboard; the code is on screen anyway.
            ShowAccountMessage(() => Loc.T("Main.Account.CopyFailed"));
        }
    });

}
