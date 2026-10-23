using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using Hearthlens.App.Infrastructure;
using Hearthlens.Core.Cards;
using Hearthlens.Core.Constructed;

namespace Hearthlens.App.Main;

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
    string Coin);

public sealed record MetaDeckRow(string Name, string Class, string HeroId, string WinRate, double Edge, string Games, string Dust, string Code);

public sealed record MulliganRow(string CardId, string Name, string WinRate, double Edge, string KeepRate, string Seen);

public sealed partial class MainViewModel
{
    private ConstructedMeta? _constructedMeta;
    private IReadOnlyList<MetaArchetypeRow> _metaArchetypes = [];
    private IReadOnlyList<MetaDeckRow> _metaDecks = [];
    private IReadOnlyList<MetaDeckRow> _archetypeDecks = [];
    private IReadOnlyList<MulliganRow> _mulligan = [];
    private IReadOnlyList<MetaChoice> _metaClasses = [];
    private MetaArchetypeRow? _selectedArchetype;
    private string _metaClass = string.Empty;
    private bool _sortByPopularity;
    private RelayCommand? _setMetaSort;
    private string _constructedMetaStatus = "Meta stats loading…";
    private string _archetypeSummary = string.Empty;
    private string _copyNote = string.Empty;
    private RelayCommand? _setMetaFormat;
    private RelayCommand? _setMetaRank;
    private RelayCommand? _setMetaPeriod;
    private RelayCommand? _setMetaClass;
    private RelayCommand? _copyDeck;

    /// <summary>Raised when the player changes a meta filter; the host downloads that slice and calls <see cref="SetConstructedMeta"/>.</summary>
    public event Action? ConstructedMetaRequested;

    public IReadOnlyList<MetaChoice> MetaFormats => Choices(ConstructedMeta.Formats, Settings.MetaFormat);

    public IReadOnlyList<MetaChoice> MetaRanks => Choices(ConstructedMeta.Ranks, Settings.MetaRank);

    public IReadOnlyList<MetaChoice> MetaPeriods => Choices(ConstructedMeta.Periods, Settings.MetaPeriod);

    /// <summary>"All classes" plus every class with an archetype in the current data.</summary>
    public IReadOnlyList<MetaChoice> MetaClasses { get => _metaClasses; private set => Set(ref _metaClasses, value); }

    public IReadOnlyList<MetaArchetypeRow> MetaArchetypes { get => _metaArchetypes; private set => Set(ref _metaArchetypes, value); }

    /// <summary>Most played decklists, best first among those with enough games.</summary>
    public IReadOnlyList<MetaDeckRow> MetaDecks { get => _metaDecks; private set => Set(ref _metaDecks, value); }

    public IReadOnlyList<MetaDeckRow> ArchetypeDecks { get => _archetypeDecks; private set => Set(ref _archetypeDecks, value); }

    public IReadOnlyList<MulliganRow> Mulligan { get => _mulligan; private set => Set(ref _mulligan, value); }

    public string ConstructedMetaStatus { get => _constructedMetaStatus; private set => Set(ref _constructedMetaStatus, value); }

    public string ArchetypeSummary { get => _archetypeSummary; private set => Set(ref _archetypeSummary, value); }

    public string CopyNote { get => _copyNote; private set => Set(ref _copyNote, value); }

    public MetaArchetypeRow? SelectedArchetype
    {
        get => _selectedArchetype;
        set
        {
            if (Set(ref _selectedArchetype, value))
            {
                RebuildArchetypeDetail();
            }
        }
    }

    public RelayCommand SetMetaFormat => _setMetaFormat ??= new RelayCommand(p => ChangeMetaFilter(() => Settings.MetaFormat = ConstructedMeta.Key(ConstructedMeta.Formats, p as string)));

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
            CopyNote = $"{deck.Name} copied. In Hearthstone, make a new deck and it offers to paste it.";
        }
        catch (COMException)
        {
            CopyNote = "The clipboard is busy. Try again.";
        }
    });

    public void SetConstructedMeta(ConstructedMeta? meta)
    {
        if (meta is null)
        {
            ConstructedMetaStatus = "Meta stats are unavailable right now. Hearthlens tries again later.";
            return;
        }

        _constructedMeta = meta;
        RebuildConstructedMeta();

        // Your own decks show the archetype they belong to.
        var selected = SelectedDeck?.Key;
        Decks = Decks.Select(d => d with { Meta = DeckMeta(d.DeckCode, d.ClassKey) }).ToList();
        SelectedDeck = Decks.FirstOrDefault(d => d.Key == selected) ?? Decks.FirstOrDefault();
    }

    private void ChangeMetaFilter(Action apply)
    {
        apply();
        Raise(nameof(MetaFormats));
        Raise(nameof(MetaRanks));
        Raise(nameof(MetaPeriods));
        ConstructedMetaStatus = "Loading…";
        ConstructedMetaRequested?.Invoke();
    }

    private static IReadOnlyList<MetaChoice> Choices(IReadOnlyList<MetaOption> options, string? selected)
    {
        var key = ConstructedMeta.Key(options, selected);
        return options.Select(o => new MetaChoice(o.Key, o.Label, o.Key == key)).ToList();
    }

    /// <summary>"Quest Priest · 58% in the meta" for one of your decks, or empty when it matches no archetype.</summary>
    private string DeckMeta(string? deckCode, string? heroClass)
    {
        if (_constructedMeta is not { } meta || deckCode is null || DeckString.TryDecode(deckCode) is not { } deck)
        {
            return string.Empty;
        }

        var ids = deck.Cards.Keys.Select(dbf => _cards.Get(dbf)?.Id).OfType<string>().ToList();
        return meta.Match(heroClass, ids) is { } archetype
            ? $"{archetype.Name} · {archetype.WinRate.ToString("P0", CultureInfo.CurrentCulture)} in the meta"
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
        if (_constructedMeta is not { } meta)
        {
            return;
        }

        MetaClasses = new[] { new MetaChoice(string.Empty, "All classes", _metaClass.Length == 0) }
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
                $"{ConstructedMeta.ClassName(a.Class)} · {a.Games.ToString("N0", CultureInfo.CurrentCulture)} games",
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

        var updated = meta.Updated == DateTime.MinValue ? string.Empty : $" · updated {meta.Updated.ToLocalTime():dd MMM HH:mm}";
        ConstructedMetaStatus = meta.Archetypes.Count == 0
            ? "No meta stats for this format and rank yet."
            : $"{ConstructedMeta.Attribution} · {ConstructedMeta.Label(ConstructedMeta.Ranks, meta.Rank)} · {ConstructedMeta.Label(ConstructedMeta.Periods, meta.Period)} · {meta.Games:N0} games{updated}";
    }

    private void RebuildArchetypeDetail()
    {
        var archetype = _constructedMeta?.Archetypes.FirstOrDefault(a => a.Id == _selectedArchetype?.Id);
        if (archetype is null)
        {
            ArchetypeSummary = string.Empty;
            ArchetypeDecks = [];
            Mulligan = [];
            return;
        }

        ArchetypeSummary = $"{ConstructedMeta.ClassName(archetype.Class)} · {archetype.WinRate.ToString("P1", CultureInfo.CurrentCulture)} win rate over {archetype.Games:N0} games · "
            + $"{archetype.Share.ToString("P1", CultureInfo.CurrentCulture)} of the meta";

        ArchetypeDecks = _constructedMeta!.Decks
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
                c.InHandWinRate - archetype.WinRate,
                $"kept {c.KeepRate.ToString("P0", CultureInfo.CurrentCulture)}",
                c.InHand.ToString("N0", CultureInfo.CurrentCulture)))
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
    private static string ClassHero(string heroClass) => heroClass.ToUpperInvariant() switch
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
