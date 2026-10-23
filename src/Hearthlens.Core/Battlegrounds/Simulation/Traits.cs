namespace Hearthlens.Core.Battlegrounds;

/// <summary>Minion types. A minion can have several; "ALL" (Amalgam-style) sets every bit.</summary>
[Flags]
public enum Tribe
{
    None = 0,
    Beast = 1 << 0,
    Demon = 1 << 1,
    Dragon = 1 << 2,
    Elemental = 1 << 3,
    Mech = 1 << 4,
    Murloc = 1 << 5,
    Naga = 1 << 6,
    Pirate = 1 << 7,
    Quilboar = 1 << 8,
    Undead = 1 << 9,
    Aberration = 1 << 10,
    All = Beast | Demon | Dragon | Elemental | Mech | Murloc | Naga | Pirate | Quilboar | Undead | Aberration,
}

/// <summary>Combat keywords the simulator applies itself.</summary>
[Flags]
public enum Keyword
{
    None = 0,
    Taunt = 1 << 0,
    DivineShield = 1 << 1,
    Poisonous = 1 << 2,
    Venomous = 1 << 3,
    Reborn = 1 << 4,
    Windfury = 1 << 5,
    MegaWindfury = 1 << 6,
    Stealth = 1 << 7,
}

/// <summary>Card mechanics that effects ask about (e.g. "a friendly Rally minion", "your left-most Deathrattle").</summary>
[Flags]
public enum CardMechanic
{
    None = 0,
    Deathrattle = 1 << 0,
    Rally = 1 << 1,
    Avenge = 1 << 2,
    Battlecry = 1 << 3,
    StartOfCombat = 1 << 4,
    Magnetic = 1 << 5,
}

public static class Traits
{
    /// <summary>Tribes from HearthstoneJSON race names or CARDRACE tag values (e.g. "BEAST", "MECHANICAL", "ALL").</summary>
    public static Tribe ParseTribes(IEnumerable<string>? races)
    {
        var tribes = Tribe.None;
        if (races is null)
        {
            return tribes;
        }

        foreach (var race in races)
        {
            tribes |= ParseTribe(race);
        }

        return tribes;
    }

    public static Tribe ParseTribe(string? race) => race?.ToUpperInvariant() switch
    {
        "BEAST" => Tribe.Beast,
        "DEMON" => Tribe.Demon,
        "DRAGON" => Tribe.Dragon,
        "ELEMENTAL" => Tribe.Elemental,
        "MECHANICAL" or "MECH" => Tribe.Mech,
        "MURLOC" => Tribe.Murloc,
        "NAGA" => Tribe.Naga,
        "PIRATE" => Tribe.Pirate,
        "QUILBOAR" => Tribe.Quilboar,
        "UNDEAD" => Tribe.Undead,
        "ABERRATION" => Tribe.Aberration,
        "ALL" => Tribe.All,
        _ => Tribe.None,
    };

    /// <summary>Keywords printed on a card, from its HearthstoneJSON mechanics.</summary>
    public static Keyword KeywordsFromMechanics(IEnumerable<string>? mechanics)
    {
        var keywords = Keyword.None;
        if (mechanics is null)
        {
            return keywords;
        }

        foreach (var mechanic in mechanics)
        {
            keywords |= mechanic switch
            {
                "TAUNT" => Keyword.Taunt,
                "DIVINE_SHIELD" => Keyword.DivineShield,
                "POISONOUS" => Keyword.Poisonous,
                "VENOMOUS" => Keyword.Venomous,
                "REBORN" => Keyword.Reborn,
                "WINDFURY" => Keyword.Windfury,
                "MEGA_WINDFURY" => Keyword.MegaWindfury,
                "STEALTH" => Keyword.Stealth,
                _ => Keyword.None,
            };
        }

        return keywords;
    }

    public static CardMechanic MechanicsFrom(IEnumerable<string>? mechanics)
    {
        var result = CardMechanic.None;
        if (mechanics is null)
        {
            return result;
        }

        foreach (var mechanic in mechanics)
        {
            result |= mechanic switch
            {
                "DEATHRATTLE" => CardMechanic.Deathrattle,
                "BACON_RALLY" => CardMechanic.Rally,
                "AVENGE" => CardMechanic.Avenge,
                "BATTLECRY" => CardMechanic.Battlecry,
                "START_OF_COMBAT" => CardMechanic.StartOfCombat,
                "MAGNETIC" => CardMechanic.Magnetic,
                _ => CardMechanic.None,
            };
        }

        return result;
    }

    /// <summary>Attacks per turn for a keyword set: Mega-Windfury 4, Windfury 2, else 1.</summary>
    public static int AttacksPerTurn(Keyword keywords) =>
        (keywords & Keyword.MegaWindfury) != 0 ? 4 : (keywords & Keyword.Windfury) != 0 ? 2 : 1;
}
