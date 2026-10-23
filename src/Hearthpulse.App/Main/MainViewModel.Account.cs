using System.Windows;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core.Accounts;

namespace Hearthpulse.App.Main;

public sealed partial class MainViewModel
{
    private AccountService? _account;
    private RelayCommand? _signIn;
    private RelayCommand? _cancelSignIn;
    private RelayCommand? _signOut;
    private RelayCommand? _openAccountProfile;
    private CancellationTokenSource? _signInCancel;
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

    public string AccountName => _account?.User?.DisplayName ?? string.Empty;

    /// <summary>A browser sign-in is under way.</summary>
    public bool AccountWaiting
    {
        get => _accountWaiting;
        private set
        {
            if (Set(ref _accountWaiting, value))
            {
                Raise(nameof(AccountSignedOut));
            }
        }
    }

    /// <summary>The last error (or confirmation) under the card.</summary>
    public string AccountMessage { get => _accountMessage; private set => Set(ref _accountMessage, value); }

    /// <summary>Opens the browser on hearthpulse.net and waits there for the player.</summary>
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
            AccountMessage = (await _account.SignInAsync(cancel.Token))?.Message ?? string.Empty;
        }
        finally
        {
            _signInCancel = null;
            AccountWaiting = false;
        }
    });

    public RelayCommand CancelSignIn => _cancelSignIn ??= new RelayCommand(() => _signInCancel?.Cancel());

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

    private void RaiseAccount()
    {
        Raise(nameof(AccountSignedIn));
        Raise(nameof(AccountSignedOut));
        Raise(nameof(AccountName));
    }
}
