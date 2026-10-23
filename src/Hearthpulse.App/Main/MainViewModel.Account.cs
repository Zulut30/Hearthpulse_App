using System.Globalization;
using System.Windows;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core.Accounts;

namespace Hearthpulse.App.Main;

public sealed partial class MainViewModel
{
    private AccountService? _account;
    private RelayCommand? _signOut;
    private RelayCommand? _toggleAccountMode;
    private bool _accountRegister;
    private bool _accountBusy;
    private string _accountMessage = string.Empty;
    private string _accountEmailInput = string.Empty;
    private string _accountNameInput = string.Empty;

    /// <summary>Connects the Settings page to the account service; the host restores a saved session afterwards.</summary>
    public void AttachAccount(AccountService account)
    {
        _account = account;
        account.Changed += () => Application.Current?.Dispatcher.BeginInvoke(RaiseAccount);
        RaiseAccount();
    }

    public bool AccountSignedIn => _account?.User is not null;

    public string AccountName => _account?.User?.DisplayName ?? string.Empty;

    public string AccountEmail => _account?.User?.Email ?? string.Empty;

    public string AccountSince => _account?.User?.CreatedAt is { } created
        ? "Member since " + created.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)
        : string.Empty;

    /// <summary>The form creates a new account instead of signing in to an existing one.</summary>
    public bool AccountRegister
    {
        get => _accountRegister;
        private set
        {
            if (Set(ref _accountRegister, value))
            {
                Raise(nameof(AccountSubmitText));
                Raise(nameof(AccountSwitchText));
            }
        }
    }

    public string AccountSubmitText => AccountRegister ? "Create account" : "Sign in";

    public string AccountSwitchText => AccountRegister ? "I already have an account" : "Create a Hearthpulse account";

    public bool AccountBusy { get => _accountBusy; private set => Set(ref _accountBusy, value); }

    /// <summary>The last error (or confirmation) under the form.</summary>
    public string AccountMessage { get => _accountMessage; private set => Set(ref _accountMessage, value); }

    public string AccountEmailInput { get => _accountEmailInput; set => Set(ref _accountEmailInput, value); }

    public string AccountNameInput { get => _accountNameInput; set => Set(ref _accountNameInput, value); }

    public RelayCommand ToggleAccountMode => _toggleAccountMode ??= new RelayCommand(() =>
    {
        AccountRegister = !AccountRegister;
        AccountMessage = string.Empty;
    });

    public RelayCommand SignOut => _signOut ??= new RelayCommand(async () =>
    {
        if (_account is not null)
        {
            await _account.SignOutAsync();
            AccountMessage = "Signed out.";
        }
    });

    /// <summary>Signs in or creates the account. The password comes from the view (a PasswordBox cannot be bound).</summary>
    public async Task SubmitAccountAsync(string password)
    {
        if (_account is null || AccountBusy)
        {
            return;
        }

        var email = AccountEmailInput.Trim();
        if (email.Length < 3 || !email.Contains('@', StringComparison.Ordinal))
        {
            AccountMessage = "Enter your email address.";
            return;
        }

        if (password.Length == 0 || (AccountRegister && password.Length < 8))
        {
            AccountMessage = AccountRegister ? "Choose a password of at least 8 characters." : "Enter your password.";
            return;
        }

        AccountBusy = true;
        AccountMessage = string.Empty;
        try
        {
            var name = AccountNameInput.Trim().Length > 0 ? AccountNameInput.Trim() : email.Split('@')[0];
            var error = AccountRegister
                ? await _account.SignUpAsync(email, password, name)
                : await _account.SignInAsync(email, password);
            AccountMessage = error?.Message ?? string.Empty;
            if (error is null)
            {
                AccountNameInput = string.Empty;
            }
        }
        finally
        {
            AccountBusy = false;
        }
    }

    private void RaiseAccount()
    {
        Raise(nameof(AccountSignedIn));
        Raise(nameof(AccountName));
        Raise(nameof(AccountEmail));
        Raise(nameof(AccountSince));
    }
}
