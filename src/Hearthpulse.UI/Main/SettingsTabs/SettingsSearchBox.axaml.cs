using Avalonia.Controls;
using Avalonia.Input;

namespace Hearthpulse.UI.Main.SettingsTabs;

/// <summary>The Settings search box (its data: MainViewModel.SettingsTools.cs). Enter jumps to the first match, Esc clears.</summary>
public partial class SettingsSearchBox : UserControl
{
    public SettingsSearchBox() => InitializeComponent();

    /// <summary>Puts the keyboard in the box (Ctrl+F on the Settings page).</summary>
    public void FocusQuery()
    {
        Query.Focus();
        Query.SelectAll();
    }

    private void OnQueryKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        if (e.Key == Key.Escape && model.SettingsSearching)
        {
            model.SettingsQuery = string.Empty;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && model.SettingsHits.Count > 0)
        {
            model.JumpToSetting.Execute(model.SettingsHits[0]);
            e.Handled = true;
        }
    }
}
