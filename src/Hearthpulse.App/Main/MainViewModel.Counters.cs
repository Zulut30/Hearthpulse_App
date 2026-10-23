using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core.Tracking.Counters;

namespace Hearthpulse.App.Main;

/// <summary>One counter on the settings page with its mode.</summary>
public sealed record CounterSettingRow(string Id, string Title, string Description, bool IsAuto, bool IsAlways, bool IsOff);

public sealed partial class MainViewModel
{
    private RelayCommand? _setCounterMode;

    /// <summary>Raised when a counter's mode changes here, so the host saves and redraws the overlay.</summary>
    public event Action? CountersChanged;

    public IReadOnlyList<CounterSettingRow> CounterRows => Counters.All
        .Select(c =>
        {
            var mode = Counters.ModeOf(Settings.CounterModes, c.Id);
            return new CounterSettingRow(c.Id, c.Title, c.Description, mode == CounterMode.Auto, mode == CounterMode.Always, mode == CounterMode.Off);
        })
        .ToList();

    /// <summary>Parameter "id|Mode", for example "cthun|Always".</summary>
    public RelayCommand SetCounterMode => _setCounterMode ??= new RelayCommand(p =>
    {
        if (p is string value && value.Split('|') is [var id, var mode] && Enum.TryParse<CounterMode>(mode, out var parsed))
        {
            ApplyCounterMode(id, parsed);
            CountersChanged?.Invoke();
        }
    });

    /// <summary>Stores a counter's mode (Auto is the default and is not stored) and refreshes the list.</summary>
    public void ApplyCounterMode(string id, CounterMode mode)
    {
        if (mode == CounterMode.Auto)
        {
            Settings.CounterModes.Remove(id);
        }
        else
        {
            Settings.CounterModes[id] = mode.ToString();
        }

        Raise(nameof(CounterRows));
    }
}
