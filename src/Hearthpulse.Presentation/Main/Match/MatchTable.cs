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

/// <summary>
/// One side of the Constructed table as the view binds it: the same object for the whole page, each step changes only
/// the properties and cards that differ from the step before (<see cref="MatchSideView"/> in place).
/// </summary>
public sealed class MatchTableSide : Observable
{
    private static readonly MatchSideView None = new(string.Empty, false, string.Empty, string.Empty, 0, 0, null, null, [], [], [], string.Empty, string.Empty);

    private MatchSideView _view = None;
    private MatchCardItem? _weapon;
    private MatchCardItem? _heroPower;

    public string Label => _view.Label;

    public bool IsLocal => _view.IsLocal;

    public string HeroCardId => _view.HeroCardId;

    public string Hero => _view.Hero;

    public int Health => _view.Health;

    public int Armor => _view.Armor;

    public MatchCardItem? Weapon => _weapon;

    public MatchCardItem? HeroPower => _heroPower;

    public ObservableCollection<MatchCardItem> Board { get; } = [];

    public ObservableCollection<MatchCardItem> Hand { get; } = [];

    public ObservableCollection<MatchCardItem> Secrets { get; } = [];

    public string Mana => _view.Mana;

    public string Deck => _view.Deck;

    public string HealthText => _view.HealthText;

    public string ArmorText => _view.ArmorText;

    public string HealthTip => _view.HealthTip;

    public int HeroRef => _view.HeroRef;

    public bool HeroActing => _view.HeroActing;

    public bool HeroTargeted => _view.HeroTargeted;

    public int HealthChange => _view.HealthChange;

    public string HealthChangeText => _view.HealthChangeText;

    public string HealthChangeTip => _view.HealthChangeTip;

    public int DeckCount => _view.DeckCount;

    public int HandCount => _view.HandCount;

    public int SecretCount => _view.SecretCount;

    public string DeckTip => _view.DeckTip;

    public string HandTip => _view.HandTip;

    public string SecretTip => _view.SecretTip;

    /// <summary>Shows the side of another step (null: no step, an empty side).</summary>
    internal void Update(MatchSideView? side)
    {
        var old = _view;
        var now = side ?? None;
        _view = now;
        MatchItems.Sync(Board, now.Board);
        MatchItems.Sync(Hand, now.Hand);
        MatchItems.Sync(Secrets, now.Secrets);
        var weapon = MatchItems.Slot(_weapon, now.Weapon);
        if (!ReferenceEquals(weapon, _weapon))
        {
            _weapon = weapon;
            Raise(nameof(Weapon));
        }

        var heroPower = MatchItems.Slot(_heroPower, now.HeroPower);
        if (!ReferenceEquals(heroPower, _heroPower))
        {
            _heroPower = heroPower;
            Raise(nameof(HeroPower));
        }

        Changed(old.Label, now.Label, nameof(Label));
        Changed(old.IsLocal, now.IsLocal, nameof(IsLocal));
        Changed(old.HeroCardId, now.HeroCardId, nameof(HeroCardId));
        Changed(old.Hero, now.Hero, nameof(Hero));
        Changed(old.Mana, now.Mana, nameof(Mana));
        Changed(old.Deck, now.Deck, nameof(Deck));
        Changed(old.HeroRef, now.HeroRef, nameof(HeroRef));
        Changed(old.HeroActing, now.HeroActing, nameof(HeroActing));
        Changed(old.HeroTargeted, now.HeroTargeted, nameof(HeroTargeted));
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

        if (old.HealthChange != now.HealthChange)
        {
            Raise(nameof(HealthChange));
            Raise(nameof(HealthChangeText));
            Raise(nameof(HealthChangeTip));
        }

        if (old.DeckCount != now.DeckCount)
        {
            Raise(nameof(DeckCount));
            Raise(nameof(DeckTip));
        }

        if (old.HandCount != now.HandCount)
        {
            Raise(nameof(HandCount));
            Raise(nameof(HandTip));
        }

        if (old.SecretCount != now.SecretCount)
        {
            Raise(nameof(SecretCount));
            Raise(nameof(SecretTip));
        }
    }

    /// <summary>The page was built again (another language): every text is read anew.</summary>
    internal void RaiseAll() => Raise(string.Empty);

    private void Changed<T>(T old, T now, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(old, now))
        {
            Raise(name);
        }
    }
}

/// <summary>
/// The Constructed table as the view binds it (<see cref="MatchViewModel.Table"/>): one object for the page's life, so
/// a step moves only what changed. The view bound the step's record before (<see cref="MatchViewModel.Step"/>, a new one
/// each step), and every binding of the table and of each card's triggers was evaluated again on every step.
/// </summary>
public sealed class MatchTable : Observable
{
    private MatchStepView? _step;
    private int _version;

    public MatchTableSide Top { get; } = new();

    public MatchTableSide Bottom { get; } = new();

    /// <summary>The step's choice rows (Discover options, both mulligans); rows are equal by value, cards included.</summary>
    public SyncList<MatchChoiceView> Choices { get; } = new();

    /// <summary>A step is on the table (a Constructed replay is open).</summary>
    public bool HasStep => _step is not null;

    public int Index => _step?.Index ?? 0;

    public int Count => _step?.Count ?? 0;

    public string Turn => _step?.Turn ?? string.Empty;

    public string Action => _step?.Action ?? string.Empty;

    public string Detail => _step?.Detail ?? string.Empty;

    public bool LocalActs => _step?.LocalActs ?? false;

    /// <summary>The step's acting card and its target by replay id (the arrow's ends), 0 when none is on the table.</summary>
    public int ActorRef => _step?.ActorRef ?? 0;

    public int TargetRef => _step?.TargetRef ?? 0;

    public bool IsAttack => _step?.IsAttack ?? false;

    /// <summary>
    /// Counts the table's changes (a step, the other side at the bottom): the arrow layer looks for its ends again when
    /// it moves, not on every layout pass.
    /// </summary>
    public int Version => _version;

    /// <summary>Shows a step on the table (null: none); false when it is the step already there.</summary>
    internal bool Update(MatchStepView? step)
    {
        var old = _step;
        if (ReferenceEquals(old, step) || (old is not null && old.Equals(step)))
        {
            return false;
        }

        _step = step;
        Top.Update(step?.Top);
        Bottom.Update(step?.Bottom);
        Choices.Sync(step?.Choices ?? []);
        Changed(old is not null, step is not null, nameof(HasStep));
        Changed(old?.Index ?? 0, Index, nameof(Index));
        Changed(old?.Count ?? 0, Count, nameof(Count));
        Changed(old?.Turn ?? string.Empty, Turn, nameof(Turn));
        Changed(old?.Action ?? string.Empty, Action, nameof(Action));
        Changed(old?.Detail ?? string.Empty, Detail, nameof(Detail));
        Changed(old?.LocalActs ?? false, LocalActs, nameof(LocalActs));
        Changed(old?.ActorRef ?? 0, ActorRef, nameof(ActorRef));
        Changed(old?.TargetRef ?? 0, TargetRef, nameof(TargetRef));
        Changed(old?.IsAttack ?? false, IsAttack, nameof(IsAttack));
        _version++;
        Raise(nameof(Version));
        return true;
    }

    /// <summary>The page was built again (another language): every text is read anew.</summary>
    internal void RaiseAll()
    {
        Top.RaiseAll();
        Bottom.RaiseAll();
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
