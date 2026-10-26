namespace Hearthpulse.Core.Arena;

/// <summary>The offer on the Arena draft screen as the memory reader saw it.</summary>
/// <param name="CardIds">The offered cards, heroes or hero powers, left to right.</param>
/// <param name="Underground">The offer belongs to an Underground Arena run; null when the client did not say.</param>
/// <param name="Packages">Per offered card, the cards that come with it (a legendary's package); empty lists otherwise.</param>
public sealed record ArenaDraftOffer(IReadOnlyList<string> CardIds, bool? Underground = null, IReadOnlyList<IReadOnlyList<string>>? Packages = null)
{
    /// <summary>The cards that come with the offered card at <paramref name="index"/>.</summary>
    public IReadOnlyList<string> PackageOf(int index) => Packages is { } packages && index < packages.Count ? packages[index] : [];

    /// <summary>Same cards, packages and mode (records compare lists by reference).</summary>
    public bool SameAs(ArenaDraftOffer? other) =>
        other is not null
        && CardIds.SequenceEqual(other.CardIds)
        && Underground == other.Underground
        && Enumerable.Range(0, CardIds.Count).All(i => PackageOf(i).SequenceEqual(other.PackageOf(i)));
}
