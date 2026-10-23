using System.Runtime.InteropServices;
using System.Windows;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Localization;

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
    private Func<string> _accountMessage = () => string.Empty;

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
        ? Loc.T("Main.Account.WaitingOpened")
        : Loc.T("Main.Account.WaitingOpen");

    /// <summary>The last error (or confirmation) under the card; kept as a text maker so it follows a language change.</summary>
    public string AccountMessage => _accountMessage();

    private void ShowAccountMessage(Func<string> text)
    {
        _accountMessage = text;
        Raise(nameof(AccountMessage));
    }

    /// <summary>Gets a code from hearthpulse.net, opens the confirmation page and waits for the player.</summary>
    public RelayCommand SignIn => _signIn ??= new RelayCommand(async () =>
    {
        if (_account is null || AccountWaiting)
        {
            return;
        }

        ShowAccountMessage(() => string.Empty);
        AccountWaiting = true;
        using var cancel = new CancellationTokenSource();
        _signInCancel = cancel;
        try
        {
            var error = await _account.SignInAsync((code, opened) => Application.Current?.Dispatcher.BeginInvoke(() => ShowAccountCode(code, opened)), cancel.Token);
            var message = error?.Message ?? string.Empty;
            ShowAccountMessage(() => message);
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
            ShowAccountMessage(() => Loc.T("Main.Account.CodeCopied"));
        }
        catch (ExternalException)
        {
            // Another program holds the clipboard; the code is on screen anyway.
            ShowAccountMessage(() => Loc.T("Main.Account.CopyFailed"));
        }
    });

    public RelayCommand OpenSignInPage => _openSignInPage ??= new RelayCommand(() =>
    {
        if (_accountCode is not null && _account?.OpenSignInPage(_accountCode) == false)
        {
            var uri = AccountCodeUri;
            ShowAccountMessage(() => Loc.F("Main.Account.OpenFailedAt", uri));
        }
    });

    public RelayCommand SignOut => _signOut ??= new RelayCommand(async () =>
    {
        if (_account is not null)
        {
            await _account.SignOutAsync();
            ShowAccountMessage(() => Loc.T("Main.Account.SignedOut"));
        }
    });

    public RelayCommand OpenAccountProfile => _openAccountProfile ??= new RelayCommand(() =>
    {
        if (_account?.OpenProfile() == false)
        {
            ShowAccountMessage(() => Loc.T("Main.Account.BrowserFailed"));
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
    }
}
