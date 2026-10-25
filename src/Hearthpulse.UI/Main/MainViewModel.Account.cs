using Avalonia.Threading;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.UI.Main;

public sealed partial class MainViewModel
{
    private static void DispatchToUi(Action action) => Dispatcher.UIThread.Post(action);

    /// <summary>The view copied the code (Avalonia's clipboard belongs to the top level, not the view model).</summary>
    public void AccountCodeCopied(bool copied) =>
        ShowAccountMessage(copied ? () => Loc.T("Main.Account.CodeCopied") : () => Loc.T("Main.Account.CopyFailed"));

}
