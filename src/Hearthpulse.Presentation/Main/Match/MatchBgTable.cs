#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.Collections.ObjectModel;
using Hearthpulse.Core.Replay;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Match;
#else
namespace Hearthpulse.UI.Main.Match;
#endif

/// <summary>A hero of the Battlegrounds table as the view binds it: <see cref="MatchBgHeroView"/> in place.</summary>
public sealed class MatchBgTableHero : Observable
{
    private static readonly MatchBgHeroView None = new(string.Empty, string.Empty, 0, 0, 0);

    private MatchBgHeroView _view = None;

    public string HeroCardId => _view.HeroCardId;

    public string Name => _view.Name;

    public int Health => _view.Health;

    public int Armor => _view.Armor;

    public int Tier => _view.Tier;

    public ObservableCollection<MatchBgCardItem> HeroPowers { get; } = [];

    /// <summary>Trinkets, the quest and its reward, in the order the game shows them.</summary>
    public ObservableCollection<MatchBgCardItem> Trinkets { get; } = [];

    public string HealthText => _view.HealthText;

    public string ArmorText => _view.ArmorText;

    public string HealthTip => _view.HealthTip;

    internal void Update(MatchBgHeroView? hero)
    {
        var old = _view;
        var now = hero ?? None;
        _view = now;
        MatchItems.Sync(HeroPowers, now.HeroPowers);
        MatchItems.Sync(Trinkets, now.Trinkets);
        if (old.HeroCardId != now.HeroCardId)
        {
            Raise(nameof(HeroCardId));
        }

        if (old.Name != now.Name)
        {
            Raise(nameof(Name));
        }

        if (old.Tier != now.Tier)
        {
            Raise(nameof(Tier));
        }

        if (old.Health != now.Health)
        {
            Raise(nameof(Health));
            Raise(nameof(HealthText));
        }

        if (old.Armor != now.Armor)
        {
            Raise(nameof(Armor));
            Raise(nameof(ArmorText));
        }

        if (old.Health != now.Health || old.Armor != now.Armor)
        {
            Raise(nameof(HealthTip));
        }
    }

    /// <summary>The page was built again (another language): every text is read anew.</summary>
    internal void RaiseAll() => Raise(string.Empty);
}

/// <summary>
/// The Battlegrounds table as the view binds it (<see cref="MatchViewModel.BgTable"/>): one object for the page's life,
/// each step changes only the properties and cards that differ (<see cref="MatchBgStepView"/> in place).
/// </summary>
public sealed class MatchBgTable : Observable
{
    private readonly MatchBgTableHero _hero = new();
    private readonly MatchBgTableHero _opponent = new();
    private MatchBgStepView? _step;

    public bool HasStep => _step is not null;

    public int Index => _step?.Index ?? 0;

    public int Count => _step?.Count ?? 0;

    public int Turn => _step?.Turn ?? 0;

    public MatchPhase Phase => _step?.Phase ?? MatchPhase.Start;

    public MatchStepKind Kind => _step?.Kind ?? default;

    public string Title => _step?.Title ?? string.Empty;

    public string Action => _step?.Action ?? string.Empty;

    public string Detail => _step?.Detail ?? string.Empty;

    /// <summary>Our hero; null before the step has one.</summary>
    public MatchBgTableHero? Hero => _step?.Hero is null ? null : _hero;

    /// <summary>The combat opponent (combat steps only).</summary>
    public MatchBgTableHero? Opponent => _step?.Opponent is null ? null : _opponent;

    public ObservableCollection<MatchBgCardItem> Board { get; } = [];

    /// <summary>The combat opponent's board on a combat step (<see cref="InCombat"/>); between combats the last one's, not shown.</summary>
    public ObservableCollection<MatchBgCardItem> OpponentBoard { get; } = [];

    /// <summary>Our partner's board while it fights (<see cref="PartnerFighting"/>); otherwise the last one shown, hidden.</summary>
    public ObservableCollection<MatchBgCardItem> PartnerBoard { get; } = [];

    /// <summary>Bob's offer on a tavern step (<see cref="ShowShop"/>); on other steps the last offer shown, hidden.</summary>
    public ObservableCollection<MatchBgCardItem> Shop { get; } = [];

    public ObservableCollection<MatchBgCardItem> Hand { get; } = [];

    public SyncList<MatchChoiceView> Choices { get; } = new();

    public bool PartnerFighting => _step?.PartnerFighting ?? false;

    public string Gold => _step?.Gold ?? string.Empty;

    public bool ShopFrozen => _step?.ShopFrozen ?? false;

    public string? ResultKey => _step?.ResultKey;

    public bool InCombat => _step?.InCombat ?? false;

    public bool BeforeTavern => _step?.BeforeTavern ?? false;

    public bool ShowShop => _step?.ShowShop ?? false;

    public bool ChoicesOnTop => _step?.ChoicesOnTop ?? false;

    public bool ChoicesBelow => _step?.ChoicesBelow ?? false;

    public bool ShowEmptyBoard => _step?.ShowEmptyBoard ?? false;

    /// <summary>Shows a step on the table (null: none); false when it is the step already there.</summary>
    internal bool Update(MatchBgStepView? step)
    {
        var old = _step;
        if (ReferenceEquals(old, step) || (old is not null && old.Equals(step)))
        {
            return false;
        }

        _step = step;
        _hero.Update(step?.Hero);
        MatchItems.Sync(Board, step?.Board ?? []);
        MatchItems.Sync(Hand, step?.Hand ?? []);

        // A row the step does not show keeps the cards it showed last: Bob's offer through a combat, the opponent and
        // its board through a tavern, the partner's board while ours fights. They come back at the next tavern or
        // combat changed in place. Emptied and filled again, 3-7 cards and a hero line were built anew twice a turn:
        // those were the slowest steps of a Battlegrounds replay (the 5 % over 30 ms).
        if (step is null || step.ShowShop)
        {
            MatchItems.Sync(Shop, step?.Shop ?? []);
        }

        if (step is null || step.InCombat)
        {
            _opponent.Update(step?.Opponent);
            MatchItems.Sync(OpponentBoard, step?.OpponentBoard ?? []);
        }

        if (step is null || step.PartnerFighting)
        {
            MatchItems.Sync(PartnerBoard, step?.PartnerBoard ?? []);
        }

        Choices.Sync(step?.Choices ?? []);
        Changed(old is not null, step is not null, nameof(HasStep));
        Changed(old?.Index ?? 0, Index, nameof(Index));
        Changed(old?.Count ?? 0, Count, nameof(Count));
        Changed(old?.Turn ?? 0, Turn, nameof(Turn));
        Changed(old?.Phase ?? MatchPhase.Start, Phase, nameof(Phase));
        Changed(old?.Kind ?? default, Kind, nameof(Kind));
        Changed(old?.Title ?? string.Empty, Title, nameof(Title));
        Changed(old?.Action ?? string.Empty, Action, nameof(Action));
        Changed(old?.Detail ?? string.Empty, Detail, nameof(Detail));
        Changed(old?.Hero is null, step?.Hero is null, nameof(Hero));
        Changed(old?.Opponent is null, step?.Opponent is null, nameof(Opponent));
        Changed(old?.PartnerFighting ?? false, PartnerFighting, nameof(PartnerFighting));
        Changed(old?.Gold ?? string.Empty, Gold, nameof(Gold));
        Changed(old?.ShopFrozen ?? false, ShopFrozen, nameof(ShopFrozen));
        Changed(old?.ResultKey, ResultKey, nameof(ResultKey));
        Changed(old?.InCombat ?? false, InCombat, nameof(InCombat));
        Changed(old?.BeforeTavern ?? false, BeforeTavern, nameof(BeforeTavern));
        Changed(old?.ShowShop ?? false, ShowShop, nameof(ShowShop));
        Changed(old?.ChoicesOnTop ?? false, ChoicesOnTop, nameof(ChoicesOnTop));
        Changed(old?.ChoicesBelow ?? false, ChoicesBelow, nameof(ChoicesBelow));
        Changed(old?.ShowEmptyBoard ?? false, ShowEmptyBoard, nameof(ShowEmptyBoard));
        return true;
    }

    /// <summary>The page was built again (another language): every text is read anew.</summary>
    internal void RaiseAll()
    {
        _hero.RaiseAll();
        _opponent.RaiseAll();
        Raise(string.Empty);
    }

    private void Changed<T>(T old, T now, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(old, now))
        {
            Raise(name);
        }
    }
}
