using System.Text.Json;

namespace Hearthpulse.Core.Cards;

public sealed partial class CardDb
{
    /// <summary>
    /// Overlays the hsbg.cards supplement on the HearthstoneJSON cards: fills fields HearthstoneJSON is missing
    /// (tavern tier, text, races, keywords, golden ids) and appends current-patch pool cards it does not know yet.
    /// </summary>
    internal static List<Card> ApplySupplement(List<Card> cards, BgCardDb supplement)
    {
        if (supplement.Count == 0)
        {
            return cards;
        }

        var byId = new Dictionary<string, int>(cards.Count + supplement.Count, StringComparer.OrdinalIgnoreCase);
        var byDbf = new Dictionary<int, int>(cards.Count + supplement.Count);
        for (var i = 0; i < cards.Count; i++)
        {
            byId[cards[i].Id] = i;
            if (cards[i].DbfId > 0)
            {
                byDbf[cards[i].DbfId] = i;
            }
        }

        foreach (var b in supplement.Cards)
        {
            if (byId.TryGetValue(b.ExternalId, out var index) || (b.Id > 0 && byDbf.TryGetValue(b.Id, out index)))
            {
                var patched = Patch(cards[index], b);
                if (!ReferenceEquals(patched, cards[index]))
                {
                    cards[index] = patched;
                    byId[patched.Id] = index;
                }

                continue;
            }

            if (!b.IsPoolCard || (!b.IsMinion && !b.IsSpell))
            {
                continue;
            }

            var added = NewPoolCard(b);
            byId[added.Id] = cards.Count;
            if (added.DbfId > 0)
            {
                byDbf[added.DbfId] = cards.Count;
            }

            cards.Add(added);
        }

        return cards;
    }

    private static Card Patch(Card card, BgCard b)
    {
        var techLevel = card.TechLevel > 0 ? card.TechLevel : b.TechLevel;
        var text = card.Text.Length > 0 ? card.Text : b.Text;
        var name = card.Name.Length > 0 ? card.Name : b.Name;
        var races = card.Races.Count > 0 ? card.Races : MapRaces(b.MinionTypes);
        var mechanics = card.Mechanics.Count > 0 ? card.Mechanics : MapMechanics(b.Keywords);
        var premium = card.BattlegroundsPremiumDbfId > 0 ? card.BattlegroundsPremiumDbfId : b.GoldenDbfId;
        var normal = card.BattlegroundsNormalDbfId;
        if (normal == 0 && b.GoldenDbfId > 0 && card.DbfId == b.GoldenDbfId && b.Id > 0)
        {
            normal = b.Id;
        }

        var poolMinion = card.IsBattlegroundsPoolMinion || (b.IsPoolCard && b.IsMinion);
        var poolSpell = card.IsBattlegroundsPoolSpell || (b.IsPoolCard && b.IsSpell);
        var hero = card.IsBattlegroundsHero || b.IsHero;
        var duos = card.IsBattlegroundsDuosExclusive || b.IsDuosOnly;
        var skinParent = card.BattlegroundsSkinParentId;
        if (skinParent == 0 && b.IsHeroSkin && b.ParentId > 0)
        {
            skinParent = b.ParentId;
        }

        if (techLevel == card.TechLevel && text == card.Text && name == card.Name && ReferenceEquals(races, card.Races)
            && ReferenceEquals(mechanics, card.Mechanics) && premium == card.BattlegroundsPremiumDbfId
            && normal == card.BattlegroundsNormalDbfId && poolMinion == card.IsBattlegroundsPoolMinion
            && poolSpell == card.IsBattlegroundsPoolSpell && hero == card.IsBattlegroundsHero
            && duos == card.IsBattlegroundsDuosExclusive && skinParent == card.BattlegroundsSkinParentId)
        {
            return card;
        }

        return new Card
        {
            Id = card.Id,
            DbfId = card.DbfId,
            Name = name,
            Text = text,
            Cost = card.Cost,
            Attack = card.Attack,
            Health = card.Health,
            Armor = card.Armor,
            Type = card.Type,
            Rarity = card.Rarity,
            CardClass = card.CardClass,
            Set = card.Set,
            Races = races,
            Mechanics = mechanics,
            Runes = card.Runes,
            Collectible = card.Collectible,
            TechLevel = techLevel,
            IsBattlegroundsPoolMinion = poolMinion,
            IsBattlegroundsPoolSpell = poolSpell,
            IsBattlegroundsHero = hero,
            IsBattlegroundsDuosExclusive = duos,
            BattlegroundsPremiumDbfId = premium,
            BattlegroundsNormalDbfId = normal,
            BattlegroundsSkinParentId = skinParent,
        };
    }

    private static Card NewPoolCard(BgCard b) => new()
    {
        Id = b.ExternalId,
        DbfId = b.Id,
        Name = b.Name,
        Text = b.Text,
        Cost = b.ManaCost,
        Attack = b.Attack,
        Health = b.Health,
        Type = b.IsMinion ? "MINION" : "SPELL",
        Set = "BATTLEGROUNDS",
        Races = MapRaces(b.MinionTypes),
        Mechanics = MapMechanics(b.Keywords),
        TechLevel = b.TechLevel,
        IsBattlegroundsPoolMinion = b.IsMinion,
        IsBattlegroundsPoolSpell = b.IsSpell,
        IsBattlegroundsDuosExclusive = b.IsDuosOnly,
        BattlegroundsPremiumDbfId = b.GoldenDbfId,
    };

    private static IReadOnlyList<string> MapRaces(IReadOnlyList<string> minionTypes)
    {
        if (minionTypes.Count == 0)
        {
            return Array.Empty<string>();
        }

        var races = new List<string>(minionTypes.Count);
        foreach (var type in minionTypes)
        {
            races.Add(type.ToUpperInvariant());
        }

        return races;
    }

    private static IReadOnlyList<string> MapMechanics(IReadOnlyList<string> keywords)
    {
        if (keywords.Count == 0)
        {
            return Array.Empty<string>();
        }

        var mechanics = new List<string>(keywords.Count);
        foreach (var keyword in keywords)
        {
            mechanics.Add(keyword.ToUpperInvariant().Replace(' ', '_'));
        }

        return mechanics;
    }
}
