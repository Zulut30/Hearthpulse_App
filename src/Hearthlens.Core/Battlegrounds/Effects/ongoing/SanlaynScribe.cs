namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// San'layn Scribe (BGDUO31_208, Duos): "Has +4/+4 for each of your team's San'layn Scribes that died this game
/// (wherever this is)." Golden: "+8/+8". Same model as <see cref="EternalKnight"/>: a friendly Scribe dying in combat
/// raises every living friendly Scribe; a Scribe summoned in combat (a reborn copy) comes with the full bonus. Only this
/// player's board fights here, so only its own Scribes can die during the fight; the teammate's earlier deaths are part
/// of the captured stats.
/// </summary>
[CardEffect(Id)]
internal sealed class SanlaynScribe : CountingAura
{
    public const string Id = "BGDUO31_208";

    protected override string CardId => Id;

    protected override (int Attack, int Health) Printed => (4, 4);

    protected override (int Attack, int Health) PerEvent => (4, 4);

    protected override int EventsThisFight(Fight fight, Side side) => EternalKnight.DeadThisFight(side, Id);

    public override void AfterFriendlyDeath(Fight fight, Unit self, Unit dead)
    {
        if (dead.CardId == Id)
        {
            CatchUp(fight, self);
        }
    }
}
