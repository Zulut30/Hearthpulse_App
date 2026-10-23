using System.Runtime.InteropServices;
using System.Windows;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core.Accounts;

namespace Hearthpulse.App.Main;

public sealed partial class MainViewModel
{
    private AccountService? _account;
    private RelayCommand? _signIn;
    private RelayCommand? _cancelSignIn;
    private RelayCommand? _copyAccountCode;
    private RelayCommand? _openSignInPage;
    private RelayCommand? _signOut;
    private RelayCommand? _openAccountProfile;
    private CancellationTokenSource? _signInCancel;
    private DeviceCode? _accountCode;
    private bool _accountBrowserOpened;
    private bool _accountWaiting;
    private string _accountMessage = string.Empty;

    /// <summary>Connects the Settings page to the account service; the host restores a saved session afterwards.</summary>
    public void AttachAccount(AccountService account)
    {
        _account = account;
        account.Changed += () => Application.Current?.Dispatcher.BeginInvoke(RaiseAccount);
        RaiseAccount();
    }

    public bool AccountSignedIn => _account?.User is not null;

    /// <summary>Neither signed in nor waiting for the browser: the card offers the sign-in button.</summary>
    public bool AccountSignedOut => !AccountSignedIn && !AccountWaiting;

    /// <summary>The profile part of the card; hidden while a new sign-in runs.</summary>
    public bool AccountShowProfile => AccountSignedIn && !AccountWaiting;

    /// <summary>"Signed in until 22 Nov 2026"; close to the end, a nudge to sign in again (see <see cref="AccountService.SessionNote"/>).</summary>
    public string AccountSessionNote => _account?.SessionNote ?? string.Empty;

    public bool AccountSessionEndingSoon => _account?.SessionEndingSoon == true;

    public string AccountName => _account?.User?.DisplayName ?? string.Empty;

    /// <summary>Paid access in one line ("Boosty subscription: Arena, Battlegrounds").</summary>
    public string AccountSubscription => _account?.Entitlements.Summary ?? string.Empty;

    public bool AccountHasSubscription => _account?.Entitlements.HasAccess == true;

    /// <summary>A sign-in is under way (getting a code, or waiting for the player to confirm it).</summary>
    public bool AccountWaiting
    {
        get => _accountWaiting;
        private set
        {
            if (Set(ref _accountWaiting, value))
            {
                Raise(nameof(AccountSignedOut));
                Raise(nameof(AccountShowProfile));
            }
        }
    }

    /// <summary>The code to type on the site has arrived.</summary>
    public bool AccountHasCode => _accountCode is not null;

    public string AccountUserCode => _accountCode?.UserCode ?? string.Empty;

    /// <summary>Where to type the code: "hearthpulse.net/connect".</summary>
    public string AccountCodeUri => _accountCode?.DisplayUri ?? string.Empty;

    /// <summary>Under the code: whether the browser has the page open or the player has to open it.</summary>
    public string AccountWaitingHint => _accountBrowserOpened
        ? "Waiting for confirmation… The page has opened in your browser."
        : "Waiting for confirmation… Open the address above in your browser.";

    /// <summary>The last error (or confirmation) under the card.</summary>
    public string AccountMessage { get => _accountMessage; private set => Set(ref _accountMessage, value); }

    /// <summary>Gets a code from hearthpulse.net, opens the confirmation page and waits for the player.</summary>
    public RelayCommand SignIn => _signIn ??= new RelayCommand(async () =>
    {
        if (_account is null || AccountWaiting)
        {
            return;
        }

        AccountMessage = string.Empty;
        AccountWaiting = true;
        using var cancel = new CancellationTokenSource();
        _signInCancel = cancel;
        try
        {
            var error = await _account.SignInAsync((code, opened) => Application.Current?.Dispatcher.BeginInvoke(() => ShowAccountCode(code, opened)), cancel.Token);
            AccountMessage = error?.Message ?? string.Empty;
        }
        finally
        {
            _signInCancel = null;
            ShowAccountCode(null, opened: false);
            AccountWaiting = false;
        }
    });

    public RelayCommand CancelSignIn => _cancelSignIn ??= new RelayCommand(() => _signInCancel?.Cancel());

    public RelayCommand CopyAccountCode => _copyAccountCode ??= new RelayCommand(() =>
    {
        try
        {
            Clipboard.SetText(AccountUserCode);
            AccountMessage = "Code copied.";
        }
        catch (ExternalException)
        {
            // Another program holds the clipboard; the code is on screen anyway.
            AccountMessage = "Couldn't copy the code. Type it in by hand.";
        }
    });

    public RelayCommand OpenSignInPage => _openSignInPage ??= new RelayCommand(() =>
    {
        if (_accountCode is not null && _account?.OpenSignInPage(_accountCode) == false)
        {
            AccountMessage = "Couldn't open your browser. Open " + AccountCodeUri + " yourself.";
        }
    });

    public RelayCommand SignOut => _signOut ??= new RelayCommand(async () =>
    {
        if (_account is not null)
        {
            await _account.SignOutAsync();
            AccountMessage = "Signed out.";
        }
    });

    public RelayCommand OpenAccountProfile => _openAccountProfile ??= new RelayCommand(() =>
    {
        if (_account?.OpenProfile() == false)
        {
            AccountMessage = "Couldn't open your browser.";
        }
    });

    private void ShowAccountCode(DeviceCode? code, bool opened)
    {
        _accountCode = code;
        _accountBrowserOpened = opened;
        Raise(nameof(AccountHasCode));
        Raise(nameof(AccountUserCode));
        Raise(nameof(AccountCodeUri));
        Raise(nameof(AccountWaitingHint));
    }

    private void RaiseAccount()
    {
        Raise(nameof(AccountSignedIn));
        Raise(nameof(AccountSignedOut));
        Raise(nameof(AccountShowProfile));
        Raise(nameof(AccountSessionNote));
        Raise(nameof(AccountSessionEndingSoon));
        Raise(nameof(AccountName));
        Raise(nameof(AccountSubscription));
        Raise(nameof(AccountHasSubscription));
        Raise(nameof(AskUpload));
    }
}
