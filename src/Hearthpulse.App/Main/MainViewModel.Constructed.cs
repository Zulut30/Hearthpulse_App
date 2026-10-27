using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.App.Main;

public sealed record MetaChoice(string Key, string Label, bool Selected);

public sealed record MetaArchetypeRow(
    int Id,
    string Name,
    string HeroId,
    string Detail,
    string Tier,
    string WinRate,
    double Edge,
    double BarFraction,
    string First,
    string Coin)
{
    /// <summary>Whether the source split this archetype's games by who went first.</summary>
    public bool HasPlaySplit => First != "—" || Coin != "—";
}

public sealed record MetaDeckRow(string Name, string Class, string HeroId, string WinRate, double Edge, string Games, string Dust, string Code);

/// <summary>One card of the mulligan guide; <see cref="Seen"/> is its tooltip ("Seen in 1,234 opening hands").</summary>
public sealed record MulliganRow(string CardId, string Name, string WinRate, double Edge, string KeepRate, string Seen);

public sealed partial class MainViewModel
{
    /// <summary>Community stats by format key ("standard", "wild"), as the host loaded them.</summary>
    private readonly Dictionary<string, ConstructedMeta> _constructedMetas = new(StringComparer.Ordinal);
    private IReadOnlyList<MetaArchetypeRow> _metaArchetypes = [];
    private IReadOnlyList<MetaDeckRow> _metaDecks = [];
    private IReadOnlyList<MetaDeckRow> _archetypeDecks = [];
    private IReadOnlyList<MulliganRow> _mulligan = [];
    private IReadOnlyList<MetaChoice> _metaClasses = [];
    private MetaArchetypeRow? _selectedArchetype;
    private string _metaClass = string.Empty;
    private bool _sortByPopularity;
    private RelayCommand? _setMetaSort;
    private Func<string> _constructedMetaStatus = () => Loc.T("Main.Meta.Loading");
    private string _archetypeSummary = string.Empty;
    private Func<string> _copyNote = () => string.Empty;
    private RelayCommand? _setMetaRank;
    private RelayCommand? _setMetaPeriod;
    private RelayCommand? _setMetaClass;
    private RelayCommand? _copyDeck;

    /// <summary>
    /// Raised when the page needs stats: a format key ("standard", "wild") the page shows and has none for, or null when
    /// the player changed a meta filter (every format in use loads again). The host calls <see cref="SetConstructedMeta"/>.
    /// </summary>
    public event Action<string?>? ConstructedMetaRequested;

    public IReadOnlyList<MetaChoice> MetaRanks => Choices(ConstructedMeta.Ranks, Settings.MetaRank);

    public IReadOnlyList<MetaChoice> MetaPeriods => Choices(ConstructedMeta.Periods, Settings.MetaPeriod);

    /// <summary>"All classes" plus every class with an archetype in the current data.</summary>
    public IReadOnlyList<MetaChoice> MetaClasses { get => _metaClasses; private set => Set(ref _metaClasses, value); }

    public IReadOnlyList<MetaArchetypeRow> MetaArchetypes
    {
        get => _metaArchetypes;
        private set
        {
            if (Set(ref _metaArchetypes, value))
            {
                Raise(nameof(MetaHasPlaySplit));
            }
        }
    }

    /// <summary>The "1st"/"Coin" columns: shown only when the source has those numbers, not as a column of dashes.</summary>
    public bool MetaHasPlaySplit => _metaArchetypes.Any(r => r.HasPlaySplit);

    /// <summary>Most played decklists, best first among those with enough games.</summary>
    public IReadOnlyList<MetaDeckRow> MetaDecks { get => _metaDecks; private set => Set(ref _metaDecks, value); }

    public IReadOnlyList<MetaDeckRow> ArchetypeDecks { get => _archetypeDecks; private set => Set(ref _archetypeDecks, value); }

    public IReadOnlyList<MulliganRow> Mulligan { get => _mulligan; private set => Set(ref _mulligan, value); }

    /// <summary>Where the meta numbers come from, or why there are none; kept as a text maker so it follows a language change.</summary>
    public string ConstructedMetaStatus => _constructedMetaStatus();

    public string ArchetypeSummary { get => _archetypeSummary; private set => Set(ref _archetypeSummary, value); }

    public string CopyNote => _copyNote();

    /// <summary>The selected archetype's name over its detail, or a nudge to pick one.</summary>
    public string SelectedArchetypeTitle => _selectedArchetype?.Name ?? Loc.T("Main.Meta.PickArchetype");

    public MetaArchetypeRow? SelectedArchetype
    {
        get => _selectedArchetype;
        set
        {
            if (Set(ref _selectedArchetype, value))
            {
                Raise(nameof(SelectedArchetypeTitle));
                RebuildArchetypeDetail();
            }
        }
    }

    public RelayCommand SetMetaRank => _setMetaRank ??= new RelayCommand(p => ChangeMetaFilter(() => Settings.MetaRank = ConstructedMeta.Key(ConstructedMeta.Ranks, p as string)));

    public RelayCommand SetMetaPeriod => _setMetaPeriod ??= new RelayCommand(p => ChangeMetaFilter(() => Settings.MetaPeriod = ConstructedMeta.Key(ConstructedMeta.Periods, p as string)));

    public RelayCommand SetMetaClass => _setMetaClass ??= new RelayCommand(p =>
    {
        _metaClass = p as string ?? string.Empty;
        RebuildConstructedMeta();
    });

    /// <summary>"WinRate" or "Popularity".</summary>
    public RelayCommand SetMetaSort => _setMetaSort ??= new RelayCommand(p =>
    {
        _sortByPopularity = p as string == "Popularity";
        RebuildConstructedMeta();
    });

    public RelayCommand CopyDeck => _copyDeck ??= new RelayCommand(p =>
    {
        if (p is not MetaDeckRow deck)
        {
            return;
        }

        try
        {
            Clipboard.SetText(deck.Code);
            ShowCopyNote(() => Loc.F("Main.Meta.Copied", deck.Name));
        }
        catch (COMException)
        {
            ShowCopyNote(() => Loc.T("Main.Meta.ClipboardBusy"));
        }
    });

    /// <summary>Stats of one format from the host; null when the page's format could not be loaded.</summary>
    public void SetConstructedMeta(ConstructedMeta? meta)
    {
        if (meta is null)
        {
            ShowMetaStatus(() => Loc.T("Main.Meta.Unavailable"));
            return;
        }

        _constructedMetas[meta.Format] = meta;
        RebuildConstructedMeta();
        RebuildConstructedStats();

        // Your own decks show the archetype they belong to, and the deck page compares with it.
        RebuildDecks();
    }

    private void ShowMetaStatus(Func<string> text)
    {
        _constructedMetaStatus = text;
        Raise(nameof(ConstructedMetaStatus));
    }

    private void ShowCopyNote(Func<string> text)
    {
        _copyNote = text;
        Raise(nameof(CopyNote));
    }

    private void ChangeMetaFilter(Action apply)
    {
        apply();
        Raise(nameof(MetaRanks));
        Raise(nameof(MetaPeriods));
        ShowMetaStatus(() => Loc.T("Main.Meta.LoadingShort"));
        ConstructedMetaRequested?.Invoke(null);
    }

    /// <summary>Asks the host for the page's format stats while it has none (the tier list waits with "Loading").</summary>
    private void RequestPageMeta()
    {
        if (PageMeta is null && ConstructedMeta.FormatKeyOf(_ctFormat) is { } key)
        {
            ShowMetaStatus(() => Loc.T("Main.Meta.LoadingShort"));
            ConstructedMetaRequested?.Invoke(key);
        }
    }

    /// <summary>The stats of the format the page shows.</summary>
    private ConstructedMeta? PageMeta => ConstructedMetaFor(_ctFormat);

    private static IReadOnlyList<MetaChoice> Choices(IReadOnlyList<MetaOption> options, string? selected)
    {
        var key = ConstructedMeta.Key(options, selected);
        return options.Select(o => new MetaChoice(o.Key, o.Label, o.Key == key)).ToList();
    }

    /// <summary>"Quest Priest · 58% in the meta" for one of your decks (its format's stats), or empty when it matches no archetype.</summary>
    private string DeckMeta(string? deckCode, string? heroClass, string format)
    {
        if (ConstructedMetaFor(format) is not { } meta || deckCode is null || DeckString.TryDecode(deckCode) is not { } deck)
        {
            return string.Empty;
        }

        var ids = deck.Cards.Keys.Select(dbf => _cards.Get(dbf)?.Id).OfType<string>().ToList();
        return meta.Match(heroClass, ids) is { } archetype
            ? Loc.F("Main.Meta.DeckInMeta", archetype.Name, archetype.WinRate.ToString("P0", CultureInfo.CurrentCulture))
            : string.Empty;
    }

    private static string TierOf(double winRate) => winRate switch
    {
        >= 0.56 => "S",
        >= 0.53 => "A",
        >= 0.50 => "B",
        >= 0.47 => "C",
        _ => "D",
    };

    private void RebuildConstructedMeta()
    {
        // Another format's stats never stand in: the tier list stays empty until the page's own are in.
        if (PageMeta is not { } meta)
        {
            MetaClasses = [];
            MetaArchetypes = [];
            MetaDecks = [];
            SelectedArchetype = null;
            RebuildArchetypeDetail();
            return;
        }

        MetaClasses = new[] { new MetaChoice(string.Empty, Loc.T("Main.Meta.AllClasses"), _metaClass.Length == 0) }
            .Concat(meta.Archetypes
                .Select(a => a.Class)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(ConstructedMeta.ClassName, StringComparer.CurrentCulture)
                .Select(c => new MetaChoice(c, ConstructedMeta.ClassName(c), string.Equals(c, _metaClass, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        var archetypes = meta.Archetypes
            .Where(a => _metaClass.Length == 0 || string.Equals(a.Class, _metaClass, StringComparison.OrdinalIgnoreCase));
        MetaArchetypes = (_sortByPopularity ? archetypes.OrderByDescending(a => a.Games) : archetypes.OrderByDescending(a => a.WinRate))
            .Select(a => new MetaArchetypeRow(
                a.Id,
                a.Name,
                ClassHero(a.Class),
                Loc.P("Main.Meta.ArchetypeDetail", a.Games, ConstructedMeta.ClassName(a.Class)),
                TierOf(a.WinRate),
                a.WinRate.ToString("P1", CultureInfo.CurrentCulture),
                a.WinRate - 0.5,
                Math.Clamp((a.WinRate - 0.40) / 0.25, 0.04, 1),
                a.FirstGames > 0 ? a.FirstWinRate.ToString("P0", CultureInfo.CurrentCulture) : "—",
                a.CoinGames > 0 ? a.CoinWinRate.ToString("P0", CultureInfo.CurrentCulture) : "—"))
            .ToList();

        // Prefer lists with 200+ games; a thin slice (high rank, few days) falls back to every list.
        var minGames = meta.Decks.Count(d => d.Games >= 200) >= 20 ? 200 : 0;
        MetaDecks = meta.Decks
            .Where(d => _metaClass.Length == 0 || string.Equals(d.Class, _metaClass, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.Games >= minGames)
            .OrderByDescending(d => d.WinRate)
            .Take(60)
            .Select(DeckRowOf)
            .ToList();

        SelectedArchetype = MetaArchetypes.FirstOrDefault(r => r.Id == _selectedArchetype?.Id) ?? MetaArchetypes.FirstOrDefault();
        RebuildArchetypeDetail();

        var updated = meta.Updated == DateTime.MinValue ? string.Empty : Loc.F("Main.Meta.Updated", meta.Updated.ToLocalTime());
        var status = meta.Archetypes.Count == 0
            ? Loc.T("Main.Meta.NoStats")
            : Loc.P("Main.Meta.Status", meta.Games, meta.Source, ConstructedMeta.Label(ConstructedMeta.Ranks, meta.Rank), ConstructedMeta.Label(ConstructedMeta.Periods, meta.Period), updated);
        ShowMetaStatus(() => status);
    }

    private void RebuildArchetypeDetail()
    {
        var archetype = PageMeta?.Archetypes.FirstOrDefault(a => a.Id == _selectedArchetype?.Id);
        if (archetype is null)
        {
            ArchetypeSummary = string.Empty;
            ArchetypeDecks = [];
            Mulligan = [];
            return;
        }

        // A source without play-rate data reports a 0 % share: that is left out rather than shown as "0.0 % of the meta".
        ArchetypeSummary = Loc.P(archetype.Share > 0 ? "Main.Meta.Summary" : "Main.Meta.SummaryNoShare", archetype.Games, ConstructedMeta.ClassName(archetype.Class),
            archetype.WinRate.ToString("P1", CultureInfo.CurrentCulture), archetype.Share.ToString("P1", CultureInfo.CurrentCulture));

        ArchetypeDecks = PageMeta!.Decks
            .Where(d => d.ArchetypeId == archetype.Id)
            .OrderByDescending(d => d.Games >= 200 ? 1 : 0)
            .ThenByDescending(d => d.WinRate)
            .Take(5)
            .Select(DeckRowOf)
            .ToList();

        // Best cards to keep first, by the win rate of games where the card stayed in the opening hand.
        Mulligan = archetype.Mulligan
            .Take(12)
            .Select(c => new MulliganRow(
                c.CardId,
                _cards.NameOf(c.CardId),
                c.InHandWinRate.ToString("P0", CultureInfo.CurrentCulture),
                c.InHandWinRate - (archetype.MulliganBaseline ?? archetype.WinRate),
                Loc.F("Main.Meta.Kept", c.KeepRate.ToString("P0", CultureInfo.CurrentCulture)),
                Loc.P("Main.Meta.SeenIn", c.InHand)))
            .ToList();
    }

    private MetaDeckRow DeckRowOf(MetaDeck d) => new(
        d.Archetype,
        ConstructedMeta.ClassName(d.Class),
        ClassHero(d.Class),
        d.WinRate.ToString("P1", CultureInfo.CurrentCulture),
        d.WinRate - 0.5,
        d.Games.ToString("N0", CultureInfo.CurrentCulture),
        DustOf(d.DeckString) is var dust and > 0 ? dust.ToString("N0", CultureInfo.CurrentCulture) : "—",
        d.DeckString);

    /// <summary>Dust to craft every card of the deck; Core set cards are free for everyone.</summary>
    private int DustOf(string deckCode)
    {
        if (DeckString.TryDecode(deckCode) is not { } deck)
        {
            return 0;
        }

        var dust = 0;
        foreach (var (dbf, count) in deck.Cards)
        {
            if (_cards.Get(dbf) is not { } card || card.Set == "CORE")
            {
                continue;
            }

            dust += count * card.Rarity switch
            {
                "COMMON" => 40,
                "RARE" => 100,
                "EPIC" => 400,
                "LEGENDARY" => 1600,
                _ => 0,
            };
        }

        return dust;
    }

    /// <summary>The class's default hero, for its portrait.</summary>
    internal static string ClassHero(string heroClass) => heroClass.ToUpperInvariant() switch
    {
        "WARRIOR" => "HERO_01",
        "SHAMAN" => "HERO_02",
        "ROGUE" => "HERO_03",
        "PALADIN" => "HERO_04",
        "HUNTER" => "HERO_05",
        "DRUID" => "HERO_06",
        "WARLOCK" => "HERO_07",
        "MAGE" => "HERO_08",
        "PRIEST" => "HERO_09",
        "DEMONHUNTER" => "HERO_10",
        "DEATHKNIGHT" => "HERO_11",
        _ => string.Empty,
    };
}
