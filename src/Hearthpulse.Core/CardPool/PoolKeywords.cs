namespace Hearthpulse.Core.CardPool;

/// <summary>Keyword names as card text writes them, and the HearthstoneJSON/API tags they stand for.</summary>
public static class PoolKeywords
{
    /// <summary>Text name -> tag, longest names first so "Divine Shield" wins over a shorter prefix.</summary>
    public static readonly IReadOnlyList<(string Name, string Tag)> All =
    [
        ("Start of Game", "START_OF_GAME_KEYWORD"), ("Honorable Kill", "HONORABLEKILL"), ("Divine Shield", "DIVINE_SHIELD"),
        ("Spell Damage", "SPELLPOWER"), ("Choose One", "CHOOSE_ONE"), ("Miniaturize", "MINIATURIZE"), ("Deathrattle", "DEATHRATTLE"),
        ("Manathirst", "MANATHIRST"), ("Spellburst", "SPELLBURST"), ("Poisonous", "POISONOUS"), ("Tradeable", "TRADEABLE"),
        ("Battlecry", "BATTLECRY"), ("Gigantify", "GIGANTIFY"), ("Quickdraw", "QUICKDRAW"), ("Twinspell", "TWINSPELL"),
        ("Lifesteal", "LIFESTEAL"), ("Windfury", "WINDFURY"), ("Venomous", "VENOMOUS"), ("Discover", "DISCOVER"),
        ("Colossal", "COLOSSAL"), ("Excavate", "EXCAVATE"), ("Overload", "OVERLOAD"), ("Starship", "STARSHIP"),
        ("Magnetic", "MODULAR"), ("Overheal", "OVERHEAL"), ("Stealth", "STEALTH"), ("Elusive", "ELUSIVE"), ("Outcast", "OUTCAST"),
        ("Corrupt", "CORRUPT"), ("Inspire", "INSPIRE"), ("Herald", "HERALD"), ("Reborn", "REBORN"), ("Frenzy", "FRENZY"),
        ("Infuse", "INFUSE"), ("Freeze", "FREEZE"), ("Charge", "CHARGE"), ("Avenge", "AVENGE"), ("Finale", "FINALE"),
        ("Combo", "COMBO"), ("Taunt", "TAUNT"), ("Imbue", "IMBUE"), ("Forge", "FORGE"), ("Titan", "TITAN"), ("Quest", "QUEST"),
        ("Rush", "RUSH"), ("Echo", "ECHO"),
    ];

    /// <summary>
    /// Mechanics shown as pool percentages, in display order for ties. Technical tags (TRIGGER_VISUAL, AURA...) are
    /// left out; "Card Draw", "Deal Face Damage" and "Heal" come from text rules (<see cref="PoolStats"/>).
    /// </summary>
    public static readonly IReadOnlyList<(string Tag, string Label)> Shown =
    [
        ("DISCOVER", "Discover"), (PoolStats.CardDrawTag, "Card Draw"), ("TAUNT", "Taunt"), (PoolStats.FaceDamageTag, "Deal Face Damage"),
        ("BATTLECRY", "Battlecry"), ("DEATHRATTLE", "Deathrattle"), ("IMBUE", "Imbue"), (PoolStats.HealTag, "Heal"),
        ("HERALD", "Herald"), ("RUSH", "Rush"), ("CHARGE", "Charge"), ("DIVINE_SHIELD", "Divine Shield"), ("LIFESTEAL", "Lifesteal"),
        ("STEALTH", "Stealth"), ("ELUSIVE", "Elusive"), ("REBORN", "Reborn"), ("WINDFURY", "Windfury"), ("POISONOUS", "Poisonous"),
        ("FREEZE", "Freeze"), ("SILENCE", "Silence"), ("COMBO", "Combo"), ("CHOOSE_ONE", "Choose One"), ("OVERLOAD", "Overload"),
        ("OUTCAST", "Outcast"), ("SPELLPOWER", "Spell Damage"), ("SECRET", "Secret"), ("COLOSSAL", "Colossal"),
        ("TRADEABLE", "Tradeable"), ("QUEST", "Quest"), ("START_OF_GAME_KEYWORD", "Start of Game"), ("EXCAVATE", "Excavate"),
        ("STARSHIP", "Starship"), ("MANATHIRST", "Manathirst"), ("FORGE", "Forge"), ("MINIATURIZE", "Miniaturize"),
    ];
}
