namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// A Deity card (<see cref="Tokens.CThunId"/>, <see cref="Tokens.YShaarjId"/>) in its two forms. As a player-level extra it
/// is the sleeping Deity: it counts friendly Aberration deaths (<see cref="Unit.Counter"/>) and, at the fourth, summons
/// the awakened Deity at the right end of the board (<see cref="Unit.Counter2"/> = 1; if the board is full it tries again
/// after the next Aberration death). The summoned minion carries the same effect class, which then does the card's own
/// text. See <see cref="Deity"/> for the model.
/// </summary>
internal abstract class DeityEffect : CombatEffect
{
    protected abstract CardMechanic Mechanics { get; }

    public override void AfterFriendlyDeath(Fight fight, Unit self, Unit dead)
    {
        // Only the sleeping holder counts deaths; Deities themselves do not count towards awakening.
        if (!self.IsExtra || self.Counter2 != 0 || !dead.Is(Tribe.Aberration) || Deity.IsDeity(dead.CardId))
        {
            return;
        }

        if (++self.Counter < Deity.AberrationDeathsToAwaken)
        {
            return;
        }

        var side = self.Side;
        var deity = Tokens.Make(self.CardId, Deity.Attack(side), Deity.Health(side), Tribe.Aberration, mechanics: Mechanics);
        if (fight.Tracing) fight.Trace($"{(side.IsOpponent ? "their" : "our")} Deity {self.CardId} awakens");
        if (fight.Summon(side, deity, side.Count) is not null)
        {
            self.Counter2 = 1;
        }
    }
}
