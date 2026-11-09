namespace Hearthpulse.Core.Battlegrounds.Effects.Player;

// Hero powers and trinkets whose number grows over the game (English card text, patch 37.0). The number is read from the
// card's own entity at capture (SimSide.ExtraTags): TAG_SCRIPT_DATA_NUM_1 = Attack gained, TAG_SCRIPT_DATA_NUM_2 =
// Health gained (NUM_1 for both when NUM_2 is absent), on top of the printed value. ASSUMPTION, not checked against a
// real log of these cards (the owner's logs have none in combat): when the tag is not in the capture the fight is "not
// simulated" with missing_input instead of guessing the printed value. Only the local player's cards are captured, so the
// opponent's always report missing_input.

/// <summary>The captured growth of a player-level card.</summary>
internal static class GrowingValue
{
    public const string AttackTag = "TAG_SCRIPT_DATA_NUM_1";
    public const string HealthTag = "TAG_SCRIPT_DATA_NUM_2";
    public const string Description = "TAG_SCRIPT_DATA_NUM_1/2 своей карты как рост с начала игры (допущение, не сверено с логом; у соперника не читается)";

    /// <summary>Attack and Health gained, or null when the card's tags were not captured.</summary>
    public static (int Attack, int Health)? Growth(SimSide side, string cardId) =>
        side.ExtraTag(cardId, AttackTag) is { } attack
            ? (Math.Max(0, attack), Math.Max(0, side.ExtraTag(cardId, HealthTag) ?? attack))
            : null;
}

/// <summary>
/// Broodmother (BG22_HERO_305p, Onyxia): "Avenge (4): Summon a 1/1 Whelp that attacks immediately. Improve this by +1/+1."
/// The Whelp (a Dragon) is summoned at the right end and attacks right after the attack in progress; every trigger in the
/// fight improves the next Whelp by +1/+1. The Whelp is the 1/1 token BG22_HERO_305t (English text and the token card;
/// the ruRU text of 2026-11-07 says 2/2). Avenge progress from before the capture (and whether it carries over between
/// combats) is in no known tag, so the fight is always missing_input: the effect runs, the odds are an estimate.
/// </summary>
[CardEffect("BG22_HERO_305p")]
internal sealed class Broodmother : AvengeEffect
{
    public const string WhelpId = "BG22_HERO_305t";

    public override string CapturedInput => GrowingValue.Description;

    public override string UncapturedInput => "прогресс Avenge (4) до снимка";

    protected override int Threshold(Unit self) => 4;

    protected override void Avenge(Fight fight, Unit self)
    {
        if (GrowingValue.Growth(self.Side.Info, self.CardId) is not { } growth)
        {
            return;
        }

        var (attack, health) = growth;
        // Counter: improvements made in this fight.
        var whelp = fight.Summon(self.Side, WhelpId, 1 + attack + self.Counter, 1 + health + self.Counter, tribes: Tribe.Dragon);
        self.Counter++;
        if (whelp is not null)
        {
            fight.AttackSoon(whelp);
        }
    }
}

/// <summary>
/// Tentacular (BG23_HERO_201p, Ozumat): "When you have space in combat, summon a 2/2 Tentacle with Taunt. (Gains +1/+1
/// after you sell a minion!)" One Tentacle (a Beast) per combat, as with Stormpike Strength: with space at Start of Combat
/// it is already on the captured board (CREATOR = the hero power) and nothing is owed; otherwise it comes once a friendly
/// minion has died and left room. Only that late Tentacle needs the captured size.
/// </summary>
[CardEffect("BG23_HERO_201p")]
internal sealed class Tentacular : CombatEffect
{
    public const string TentacleId = "BG23_HERO_201pt";

    public override string CapturedInput => GrowingValue.Description;

    public override bool HasInputs(SimSide side, string cardId) => !Owed(side, cardId) || GrowingValue.Growth(side, cardId) is not null;

    public override void OnFightStart(Fight fight, Unit self) => self.Counter = Owed(self.Side.Info, self.CardId) ? 1 : 0;

    public override void AfterFriendlyDeath(Fight fight, Unit self, Unit dead)
    {
        if (self.Counter == 0 || self.Side.IsFull || GrowingValue.Growth(self.Side.Info, self.CardId) is not { } growth)
        {
            return;
        }

        var (attack, health) = growth;
        self.Counter = 0;
        fight.Summon(self.Side, TentacleId, 2 + attack, 2 + health, Keyword.Taunt, tribes: Tribe.Beast);
    }

    /// <summary>A Tentacle is owed: the board was full at capture and no Tentacle from this power is on it.</summary>
    private static bool Owed(SimSide side, string cardId) =>
        side.Minions.Count >= Side.MaxMinions && !side.Minions.Any(m => m.CreatorCardId == cardId);
}

/// <summary>
/// Fang Anklet (BG35_MagicItem_701): "Start of Combat: Your Beasts have +1/+1 this combat. After you summon a Beast,
/// improve this permanently." The Start of Combat part resolved before capture, so the captured Beasts already have it
/// and are not buffed again; Beasts summoned later this combat get the same +X/+X. The improvement is for later combats:
/// it does not raise this combat's aura.
/// </summary>
[CardEffect("BG35_MagicItem_701")]
internal sealed class FangAnklet : CombatEffect
{
    public override string CapturedInput => GrowingValue.Description;

    public override bool HasInputs(SimSide side, string cardId) => GrowingValue.Growth(side, cardId) is not null;

    public override void OnFightStart(Fight fight, Unit self)
    {
        if (GrowingValue.Growth(self.Side.Info, self.CardId) is { } growth)
        {
            self.Side.AddSummonBuff(Tribe.Beast, 1 + growth.Attack, 1 + growth.Health);
        }
    }
}
