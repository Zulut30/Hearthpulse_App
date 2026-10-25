using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.App.Overlay;

/// <summary>WPF-only tribe-focus controls for the game plan.</summary>
public sealed partial class OverlayViewModel
{
    /// <summary>The player picked a tribe to build around (race key), or null for any.</summary>
    public event Action<string?>? FocusRequested;

    private string? _compFocus;
    private IReadOnlyList<string> _focusRaces = Array.Empty<string>();
    private IReadOnlyList<FocusChoice> _focusChoices = Array.Empty<FocusChoice>();

    /// <summary>"Any" plus the lobby's tribes; the selected one is the tribe the comps are built around.</summary>
    public IReadOnlyList<FocusChoice> FocusChoices { get => _focusChoices; private set => Set(ref _focusChoices, value); }

    private RelayCommand _focusTribe = null!;

    public RelayCommand FocusTribe => _focusTribe;

    private void RebuildFocus(IReadOnlyList<string> races)
    {
        var focus = _compFocus is { Length: > 0 } f && races.Contains(f) ? f : null;
        var key = $"{focus}|{string.Join(',', races)}";
        _focusRaces = races;
        if (key == _focusKey)
        {
            return;
        }

        _focusKey = key;
        FocusChoices = races.Count == 0
            ? Array.Empty<FocusChoice>()
            : races.Select(r => new FocusChoice(r, Races.Display(r, _locale), r == focus))
                .Prepend(new FocusChoice(string.Empty, Loc.T("Overlay.Plan.AnyTribe"), focus is null))
                .ToList();
    }

    private string _focusKey = string.Empty;

    partial void InitializeFocus()
    {
        _focusTribe = new RelayCommand(p =>
        {
            var key = p as string;
            _compFocus = string.IsNullOrEmpty(key) || key == _compFocus ? null : key;
            RebuildFocus(_focusRaces);
            FocusRequested?.Invoke(_compFocus);
        });
    }

    partial void RefreshFocusLanguage()
    {
        _focusKey = string.Empty;
        RebuildFocus(_focusRaces);
    }

    partial void UpdateFocusSettings(AppSettings settings) => _compFocus = settings.CompFocus;

    partial void UpdateFocusRaces(IReadOnlyList<string> races) => RebuildFocus(races);
}
