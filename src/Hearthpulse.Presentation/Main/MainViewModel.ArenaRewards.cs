using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;
using Microsoft.Data.Sqlite;
#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>One reward of a run on its chip: an MDI glyph and the amount ("150").</summary>
public sealed record ArenaRewardPart(string Glyph, string Amount);

/// <summary>
/// The rewards editor of one Arena run (<c>docs/arena-rewards.md</c>): five whole numbers, checked as they are typed;
/// empty is 0. One editor is open at a time and outlives a rebuild of the run list.
/// </summary>
public sealed class ArenaRewardsEdit : Observable
{
    private readonly Action<ArenaRewardsEdit, ArenaRewards?> _save;
    private readonly Action _cancel;
    private string _gold;
    private string _dust;
    private string _packs;
    private string _tickets;
    private string _cards;
    private string _error = string.Empty;

    internal ArenaRewardsEdit(string key, string name, ArenaRewards? saved, Action<ArenaRewardsEdit, ArenaRewards?> save, Action cancel)
    {
        Key = key;
        Name = name;
        CanClear = saved is { IsEmpty: false };
        _gold = Field(saved?.Gold);
        _dust = Field(saved?.Dust);
        _packs = Field(saved?.Packs);
        _tickets = Field(saved?.Tickets);
        _cards = Field(saved?.Cards);
        _save = save;
        _cancel = cancel;
        Save = new RelayCommand(() =>
        {
            if (Parse() is { } rewards)
            {
                _save(this, rewards);
            }
        });
        Cancel = new RelayCommand(_cancel);
        Clear = new RelayCommand(() => _save(this, null));
    }

    /// <summary>The run's key (<see cref="ArenaRun.Key"/>).</summary>
    public string Key { get; }

    /// <summary>"Run rewards: Hunter, Oct 28 00:33", for screen readers.</summary>
    public string Name { get; }

    /// <summary>The run has saved rewards ("Clear" shows).</summary>
    public bool CanClear { get; }

    public string Gold { get => _gold; set => SetField(ref _gold, value); }

    public string Dust { get => _dust; set => SetField(ref _dust, value); }

    public string Packs { get => _packs; set => SetField(ref _packs, value); }

    public string Tickets { get => _tickets; set => SetField(ref _tickets, value); }

    public string Cards { get => _cards; set => SetField(ref _cards, value); }

    /// <summary>The first field that is not a whole number 0..99 999, or a failed save; else empty.</summary>
    public string Error { get => _error; private set => Set(ref _error, value); }

    public bool CanSave => Parse() is not null;

    public RelayCommand Save { get; }

    public RelayCommand Cancel { get; }

    public RelayCommand Clear { get; }

    /// <summary>The rewards typed in, or null while a field is not a whole number in range.</summary>
    internal ArenaRewards? Parse() =>
        Number(_gold) is { } gold && Number(_dust) is { } dust && Number(_packs) is { } packs && Number(_tickets) is { } tickets && Number(_cards) is { } cards
            ? new ArenaRewards(gold, dust, packs, tickets, cards)
            : null;

    internal void Failed() => Error = Loc.T("Stats.Arena.Rewards.SaveFailed");

    private void SetField(ref string field, string value)
    {
        if (Set(ref field, value ?? string.Empty))
        {
            Error = FirstInvalid() is { } label ? Loc.F("Stats.Arena.Rewards.Invalid", label, ArenaRewards.MaxValue.ToString("N0", CultureInfo.CurrentCulture)) : string.Empty;
            Raise(nameof(CanSave));
        }
    }

    private string? FirstInvalid() =>
        Number(_gold) is null ? Loc.T("Stats.Arena.Rewards.Gold")
        : Number(_dust) is null ? Loc.T("Stats.Arena.Rewards.Dust")
        : Number(_packs) is null ? Loc.T("Stats.Arena.Rewards.Packs")
        : Number(_tickets) is null ? Loc.T("Stats.Arena.Rewards.Tickets")
        : Number(_cards) is null ? Loc.T("Stats.Arena.Rewards.Cards")
        : null;

    /// <summary>Empty is 0; only the digits 0-9 (no sign, separator or space inside), at most <see cref="ArenaRewards.MaxValue"/>.</summary>
    internal static int? Number(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return 0;
        }

        return trimmed.All(char.IsAsciiDigit) && trimmed.Length <= 5 && int.Parse(trimmed, CultureInfo.InvariantCulture) is var value and <= ArenaRewards.MaxValue
            ? value
            : null;
    }

    private static string Field(int? value) => value is > 0 ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
}

public sealed partial class MainViewModel
{
    private ArenaRewardsEdit? _arenaRewardsEdit;
    private RelayCommand? _editArenaRewards;

    /// <summary>Opens the rewards editor of the run whose key is the parameter (closing another one unsaved).</summary>
    public RelayCommand EditArenaRewards => _editArenaRewards ??= new RelayCommand(p =>
    {
        if (p is not string key || ArenaRuns.FirstOrDefault(r => r.Key == key) is not { } row)
        {
            return;
        }

        _arenaRewardsEdit = new ArenaRewardsEdit(key, Loc.F("Stats.Arena.Rewards.EditorName", row.ClassName, row.When), row.Rewards, SaveArenaRewards, CloseArenaRewards);
        ArenaRuns = WithEditor(ArenaRuns);
    });

    /// <summary>The run rows with the open editor on its run; an editor whose run left the list closes unsaved.</summary>
    private IReadOnlyList<ArenaRunRow> WithEditor(IReadOnlyList<ArenaRunRow> rows)
    {
        if (_arenaRewardsEdit is { } edit && rows.All(r => r.Key != edit.Key))
        {
            _arenaRewardsEdit = null;
        }

        return rows.Select(r => r with { Edit = r.Key == _arenaRewardsEdit?.Key ? _arenaRewardsEdit : null }).ToList();
    }

    private void SaveArenaRewards(ArenaRewardsEdit edit, ArenaRewards? rewards)
    {
        try
        {
            _history.SetArenaRewards(edit.Key, rewards);
        }
        catch (Exception ex) when (ex is SqliteException or System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            Diag.Error("Saving Arena rewards failed", ex);
            edit.Failed();
            return;
        }

        CloseArenaRewards();
        RebuildArenaStats();
    }

    private void CloseArenaRewards()
    {
        _arenaRewardsEdit = null;
        ArenaRuns = WithEditor(ArenaRuns);
    }

    /// <summary>The chip's parts: only the rewards above 0, in the order gold, dust, packs, tickets, cards.</summary>
    internal static IReadOnlyList<ArenaRewardPart> RewardParts(ArenaRewards? rewards) =>
        rewards is null ? [] :
        new[] { ("\U000F0B38", rewards.Gold), ("\U000F01C8", rewards.Dust), ("\U000F03D7", rewards.Packs), ("\U000F0516", rewards.Tickets), ("\U000F06C7", rewards.Cards) }
            .Where(p => p.Item2 > 0)
            .Select(p => new ArenaRewardPart(p.Item1, p.Item2.ToString("N0", CultureInfo.CurrentCulture)))
            .ToList();

    /// <summary>"gold 150, dust 40, packs 1": the chip's tooltip and screen-reader name; empty without rewards.</summary>
    internal static string RewardsText(ArenaRewards? rewards, string separator = ", ", bool withGold = true) =>
        rewards is null ? string.Empty : string.Join(separator, new[]
        {
            withGold && rewards.Gold > 0 ? Loc.F("Stats.Arena.Rewards.GoldPart", rewards.Gold.ToString("N0", CultureInfo.CurrentCulture)) : null,
            rewards.Dust > 0 ? Loc.F("Stats.Arena.Rewards.DustPart", rewards.Dust.ToString("N0", CultureInfo.CurrentCulture)) : null,
            rewards.Packs > 0 ? Loc.P("Stats.Arena.Rewards.PacksPart", rewards.Packs) : null,
            rewards.Tickets > 0 ? Loc.P("Stats.Arena.Rewards.TicketsPart", rewards.Tickets) : null,
            rewards.Cards > 0 ? Loc.P("Stats.Arena.Rewards.CardsPart", rewards.Cards) : null,
        }.OfType<string>());

    /// <summary>The "Rewards" tile: the period's gold, the rest under it; "—" while no run of the period has rewards.</summary>
    internal static StatTile RewardsTile(IEnumerable<ArenaRun> runs, IReadOnlyDictionary<string, ArenaRewards> rewards)
    {
        var entered = runs.Select(r => rewards.GetValueOrDefault(r.Key)).OfType<ArenaRewards>().ToList();
        if (entered.Count == 0)
        {
            return new StatTile(Loc.T("Stats.Arena.Rewards.Tile"), "—", Loc.T("Stats.Arena.Rewards.TileEmpty"));
        }

        var sum = ArenaRewards.Sum(entered);
        var rest = RewardsText(sum, " · ", withGold: false);
        return new StatTile(Loc.T("Stats.Arena.Rewards.Tile"), sum.Gold.ToString("N0", CultureInfo.CurrentCulture),
            rest.Length > 0 ? $"{Loc.T("Stats.Arena.Rewards.TileGold")} · {rest}" : Loc.T("Stats.Arena.Rewards.TileGold"));
    }
}
