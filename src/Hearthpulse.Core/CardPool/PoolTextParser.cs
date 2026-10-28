using System.Globalization;
using System.Text.RegularExpressions;

namespace Hearthpulse.Core.CardPool;

/// <summary>What the parser made of a card's text.</summary>
public enum PoolParseStatus
{
    /// <summary>A generator with a pool the catalog can answer.</summary>
    Pool,

    /// <summary>No Discover or random card generation in the text.</summary>
    NotGenerator,

    /// <summary>It generates, but not from the card catalog (your deck, the opponent's hand, a copy...).</summary>
    NotPool,

    /// <summary>It generates, but the description has words the parser does not know: no pool is shown.</summary>
    Unrecognized,

    /// <summary>
    /// It draws from a pool the catalog cannot answer (an entry of the exception table: a named group of tokens, a
    /// pool that depends on the game state): the panel says "pool not computed" with the reason instead of nothing.
    /// </summary>
    Uncomputed,
}

/// <summary>A generator found in card text.</summary>
/// <param name="Count">How many cards it makes ("two random", "Summon three..."); 1 when not stated.</param>
/// <param name="Phrase">The text the query was read from, for logs and the panel's description.</param>
public sealed record PoolGenerator(PoolVerb Verb, int Count, PoolQuery Query, string Phrase);

/// <param name="Detail">For NotPool/Unrecognized: why, or the words that were not understood.</param>
public sealed record PoolParse(PoolParseStatus Status, PoolGenerator? Generator = null, string? Detail = null)
{
    public static PoolParse NotGenerator { get; } = new(PoolParseStatus.NotGenerator);
}

/// <summary>
/// Reads the pool a generator card draws from out of its English text: "Discover a Battlecry minion",
/// "Get two random 4-Cost spells", "Summon a random 6, 4, and 2-Cost Taunt minion", "Add two random spells from other
/// classes that cost (5) or more", "Discover a Legendary Beast from any class". Grammar and limits: docs/discover-pool.md.
/// Anything it cannot read word for word is <see cref="PoolParseStatus.Unrecognized"/> - no guessed pools.
/// </summary>
public static partial class PoolTextParser
{
    private static readonly Dictionary<string, string> Tribes = new(StringComparer.Ordinal)
    {
        ["Beast"] = "BEAST", ["Demon"] = "DEMON", ["Dragon"] = "DRAGON", ["Elemental"] = "ELEMENTAL", ["Mech"] = "MECHANICAL",
        ["Murloc"] = "MURLOC", ["Pirate"] = "PIRATE", ["Naga"] = "NAGA", ["Undead"] = "UNDEAD", ["Totem"] = "TOTEM",
        ["Draenei"] = "DRAENEI", ["Quilboar"] = "QUILBOAR",
    };

    private static readonly HashSet<string> GenerationVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "gets", "add", "adds", "summon", "summons", "cast", "casts", "fill", "fills", "transform", "transforms",
        "shuffle", "shuffles", "equip", "equips", "replace", "replaces", "look", "become", "becomes",
    };

    private static readonly HashSet<string> OtherVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "deal", "deals", "destroy", "destroys", "give", "gives", "return", "returns", "reduce", "reduces", "trigger", "triggers",
        "discard", "discards", "draw", "draws", "attack", "attacks", "steal", "steals", "silence", "silences", "freeze", "freezes",
        "take", "takes", "set", "sets", "restore", "restores", "swap", "swaps", "split", "spend", "spends", "choose", "chooses",
        "gain", "gains", "have", "has", "double", "doubles", "increase", "increases", "heal", "heals",
        "put", "puts", "remove", "removes", "reveal", "reveals", "play", "plays", "kill", "kills", "upgrade", "upgrades",
    };

    public static PoolParse Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return PoolParse.NotGenerator;
        }

        var fallback = PoolParse.NotGenerator;
        foreach (var anchor in Anchors(text))
        {
            var result = anchor.Discover ? ReadDiscover(text, anchor.Index) : ReadRandom(text, anchor.Index);
            if (result.Status == PoolParseStatus.Pool)
            {
                return result;
            }

            // An unreadable generator outranks one that is "not from the catalog": it should be logged.
            if (Rank(result.Status) > Rank(fallback.Status))
            {
                fallback = result;
            }
        }

        return fallback;

        static int Rank(PoolParseStatus status) => status switch
        {
            PoolParseStatus.Unrecognized => 2,
            PoolParseStatus.NotPool => 1,
            _ => 0,
        };
    }

    private static IEnumerable<(int Index, bool Discover)> Anchors(string text)
    {
        var anchors = new List<(int, bool)>();
        foreach (Match m in DiscoverRegex().Matches(text))
        {
            anchors.Add((m.Index + m.Length, true));
        }

        foreach (Match m in RandomRegex().Matches(text))
        {
            anchors.Add((m.Index, false));
        }

        return anchors.OrderBy(a => a.Item1);
    }

    // "Discover a/an/two/any <descriptor>"; not "you Discover", "Discovered", "Quest: Discover 8 cards".
    private static PoolParse ReadDiscover(string text, int index)
    {
        var phraseStart = index - "Discover".Length;
        var cursor = new Cursor(text, index);
        var count = 1;
        if (cursor.Take(CountRegex()) is { } c)
        {
            count = CountOf(c.Groups[1].Value);
        }

        if (cursor.Take(CopyRegex()) is not null)
        {
            return new PoolParse(PoolParseStatus.NotPool, Detail: "copy of a card");
        }

        return ReadDescriptor(text, cursor, PoolVerb.Discover, count, phraseStart);
    }

    // "<generation verb> ... [count] random <descriptor>".
    private static PoolParse ReadRandom(string text, int index)
    {
        var clauseStart = text.LastIndexOfAny(['.', ':', ';', '!', '?'], Math.Max(0, index - 1)) + 1;
        var before = text[clauseStart..index];
        Match? verb = null;
        foreach (Match w in WordRegex().Matches(before))
        {
            if (GenerationVerbs.Contains(w.Value) || OtherVerbs.Contains(w.Value))
            {
                verb = w;
            }
        }

        // "deal 3 damage to a random enemy", "a random friendly minion", "a random player's deck": targets, not cards.
        if (verb is null || !GenerationVerbs.Contains(verb.Value) || TargetRegex().IsMatch(text, index + "random".Length))
        {
            return PoolParse.NotGenerator;
        }

        var count = 1;
        if (CountBeforeRegex().Match(before) is { Success: true } c)
        {
            count = CountOf(c.Groups[1].Value);
        }

        var cursor = new Cursor(text, index + "random".Length);
        return ReadDescriptor(text, cursor, PoolVerb.Random, count, clauseStart + verb.Index);
    }

    private static PoolParse ReadDescriptor(string text, Cursor cursor, PoolVerb verb, int count, int phraseStart)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        var races = new HashSet<string>(StringComparer.Ordinal);
        var schools = new HashSet<string>(StringComparer.Ordinal);
        var runes = new HashSet<string>(StringComparer.Ordinal);
        var classes = new HashSet<string>(StringComparer.Ordinal);
        var costs = new HashSet<int>();
        var mechanics = new List<IReadOnlySet<string>>();
        string? rarity = null, parity = null, parityStat = null;
        int? minCost = null, maxCost = null, minAttack = null;
        var scope = ClassScope.Default;
        var past = false;
        var multipleTypes = false;
        var noun = false;

        // Adjectives, then the noun.
        while (!noun)
        {
            cursor.SkipSpaces();
            if (cursor.Take(FillerRegex()) is not null)
            {
                continue;
            }

            if (cursor.Take(OwnerRegex()) is { } owner)
            {
                return new PoolParse(PoolParseStatus.NotPool, Detail: owner.Value.Trim() + " card");
            }

            if (cursor.Take(ClassRegex()) is { } cls)
            {
                classes.Add(cls.Groups[1].Value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant());
                scope = ClassScope.Named;
                continue;
            }

            if (cursor.Take(RarityRegex()) is { } r)
            {
                rarity = r.Groups[1].Value.ToUpperInvariant();
                continue;
            }

            if (cursor.Take(CostListRegex()) is { } cl)
            {
                foreach (Capture cost in cl.Groups["n"].Captures)
                {
                    costs.Add(int.Parse(cost.Value, CultureInfo.InvariantCulture));
                }

                continue;
            }

            if (cursor.Take(ParityRegex()) is { } p)
            {
                parity = p.Groups[1].Value.ToLowerInvariant();
                parityStat = p.Groups[2].Value;
                continue;
            }

            // Before the spell schools: "Frost Rune card" is a rune, "Frost spell" a school.
            if (cursor.Take(RuneRegex()) is { } rune)
            {
                foreach (Capture symbol in rune.Groups["r"].Captures)
                {
                    runes.Add(symbol.Value.ToUpperInvariant());
                }

                continue;
            }

            if (cursor.Take(SchoolRegex()) is { } s)
            {
                foreach (Capture school in s.Groups["s"].Captures)
                {
                    schools.Add(school.Value.ToUpperInvariant());
                }

                continue;
            }

            if (TakeKeywords(cursor) is { } group)
            {
                mechanics.Add(group);
                continue;
            }

            if (TakeTribes(cursor) is { } tribes)
            {
                races.UnionWith(tribes);
                types.Add("MINION");
                // "a Dragon" ends here; "a Dragon minion" goes on to the noun.
                noun = cursor.Peek(NounRegex()) is null;
                continue;
            }

            if (cursor.Take(NounRegex()) is { } n)
            {
                var word = n.Groups[1].Value;
                switch (word.TrimEnd('s').Replace("Spell", "spell", StringComparison.Ordinal))
                {
                    case "minion": types.Add("MINION"); break;
                    case "spell": types.Add("SPELL"); break;
                    case "weapon": types.Add("WEAPON"); break;
                    case "location": types.Add("LOCATION"); break;
                    case "Secret":
                        types.Add("SPELL");
                        mechanics.Add(new HashSet<string>(StringComparer.Ordinal) { "SECRET" });
                        break;
                    case "Hero card" or "Heroe" or "heroe": types.Add("HERO"); break;
                }

                noun = true;
                continue;
            }

            return Unrecognized(text, cursor);
        }

        // What follows the noun: cost limits, class, era and keyword clauses, then the end of the phrase.
        while (true)
        {
            if (cursor.Take(CostLimitRegex()) is { } limit)
            {
                var value = int.Parse(limit.Groups["n"].Value, CultureInfo.InvariantCulture);
                switch (limit.Groups["dir"].Value)
                {
                    case "more": minCost = value; break;
                    case "less": maxCost = value; break;
                    default: costs.Add(value); break;
                }

                continue;
            }

            if (cursor.Take(FromRegex()) is { } from)
            {
                var what = from.Groups[1].Value;
                if (what == "the past")
                {
                    past = true;
                }
                else
                {
                    scope = what.ToLowerInvariant() switch
                    {
                        "any class" => ClassScope.Any,
                        "your class" => ClassScope.Own,
                        "your opponent's class" or "the opponent's class" or "your opponent's classes" => ClassScope.Opponent,
                        _ => ClassScope.Other,
                    };
                }

                continue;
            }

            if (cursor.Take(ElsewhereRegex()) is { } elsewhere)
            {
                return new PoolParse(PoolParseStatus.NotPool, Detail: elsewhere.Value.Trim());
            }

            if (cursor.Take(DarkGiftRegex()) is not null)
            {
                continue;
            }

            if (cursor.Take(MultipleTypesRegex()) is not null)
            {
                multipleTypes = true;
                continue;
            }

            if (cursor.Take(MinAttackRegex()) is { } atk)
            {
                minAttack = int.Parse(atk.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }

            if (cursor.Peek(WithKeywordRegex()) is not null)
            {
                cursor.Take(WithKeywordRegex());
                if (TakeKeywords(cursor) is { } group)
                {
                    mechanics.Add(group);
                    continue;
                }

                return Unrecognized(text, cursor);
            }

            if (cursor.Peek(EndRegex()) is not null)
            {
                break;
            }

            return Unrecognized(text, cursor);
        }

        var query = new PoolQuery
        {
            Types = types,
            Races = races,
            Schools = schools,
            Runes = runes,
            Mechanics = mechanics,
            // Only Death Knight cards have runes: a stolen Hematurge still offers them to another class.
            Scope = runes.Count > 0 && scope == ClassScope.Default ? ClassScope.Any : scope,
            Rarity = rarity,
            Costs = costs,
            MinCost = minCost,
            MaxCost = maxCost,
            Parity = parity,
            ParityStat = parityStat,
            MinAttack = minAttack,
            MultipleTypes = multipleTypes,
            Classes = classes,
            Past = past,
        };
        var phrase = text[phraseStart..cursor.Position].Trim(' ', ':', '.', ',');
        return new PoolParse(PoolParseStatus.Pool, new PoolGenerator(verb, count, query, phrase));
    }

    private static PoolParse Unrecognized(string text, Cursor cursor)
    {
        var rest = text[cursor.Position..];
        return new PoolParse(PoolParseStatus.Unrecognized, Detail: rest.Length > 48 ? rest[..48] + "..." : rest);
    }

    /// <summary>"Combo, Battlecry, or Stealth" -> one group of alternatives; null when no keyword is here.</summary>
    private static HashSet<string>? TakeKeywords(Cursor cursor)
    {
        HashSet<string>? group = null;
        var save = cursor.Position;
        while (true)
        {
            cursor.SkipSpaces();
            var found = PoolKeywords.All.FirstOrDefault(k => cursor.AtWord(k.Name));
            if (found.Name is null)
            {
                break;
            }

            cursor.Advance(found.Name.Length);
            (group ??= new HashSet<string>(StringComparer.Ordinal)).Add(found.Tag);
            save = cursor.Position;
            if (cursor.Take(ListSeparatorRegex()) is null)
            {
                break;
            }
        }

        cursor.Position = save;
        return group;
    }

    private static HashSet<string>? TakeTribes(Cursor cursor)
    {
        HashSet<string>? group = null;
        var save = cursor.Position;
        while (true)
        {
            cursor.SkipSpaces();
            if (cursor.Take(TribeRegex()) is not { } m)
            {
                break;
            }

            (group ??= new HashSet<string>(StringComparer.Ordinal)).Add(Tribes[m.Groups[1].Value]);
            save = cursor.Position;
            if (cursor.Take(ListSeparatorRegex()) is null)
            {
                break;
            }
        }

        cursor.Position = save;
        return group;
    }

    private static int CountOf(string word) => word.ToLowerInvariant() switch
    {
        "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6, "seven" => 7,
        var n when int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) => v,
        _ => 1,
    };

    private sealed class Cursor(string text, int position)
    {
        public int Position { get; set; } = position;

        public Match? Take(Regex regex)
        {
            var m = regex.Match(text, Position);
            if (!m.Success)
            {
                return null;
            }

            Position = m.Index + m.Length;
            return m;
        }

        public Match? Peek(Regex regex) => regex.Match(text, Position) is { Success: true } m ? m : null;

        public void SkipSpaces()
        {
            while (Position < text.Length && text[Position] == ' ')
            {
                Position++;
            }
        }

        public bool AtWord(string word) =>
            string.CompareOrdinal(text, Position, word, 0, word.Length) == 0
            && (Position + word.Length == text.Length || !char.IsLetter(text[Position + word.Length]));

        public void Advance(int length) => Position += length;
    }

    // "Spend a Corpse to Discover a Blood Rune card" is a Discover; "Go on a Sidequest to Discover amazing loot" is not.
    [GeneratedRegex(@"(?<!\byou |\byou've |\byou'd |\byou'll |\bSidequest to |Quest: |\bthey |\bcan )\bDiscover\b(?!ed|s)")]
    private static partial Regex DiscoverRegex();

    [GeneratedRegex(@"\brandom\b")]
    private static partial Regex RandomRegex();

    [GeneratedRegex(@"[A-Za-z']+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\G ?(a|an|one|two|three|four|five|any|\d+) (?=\S)")]
    private static partial Regex CountRegex();

    [GeneratedRegex(@"\b(a|an|one|two|three|four|five|six|seven|\d+) $")]
    private static partial Regex CountBeforeRegex();

    [GeneratedRegex(@"\G ?(copy|copies) of\b")]
    private static partial Regex CopyRegex();

    [GeneratedRegex(@"\G(different|Temporary|new|random|additional|other|another) (?=\S)")]
    private static partial Regex FillerRegex();

    [GeneratedRegex(@"\G(Death Knight|Demon Hunter|Druid|Hunter|Mage|Paladin|Priest|Rogue|Shaman|Warlock|Warrior|Neutral) (?=\S)")]
    private static partial Regex ClassRegex();

    [GeneratedRegex(@"\G(Legendary|Epic|Rare|Common) (?=\S)")]
    private static partial Regex RarityRegex();

    // "3-Cost", "6, 4, and 2-Cost", "10 and 1-Cost".
    [GeneratedRegex(@"\G(?:(?<n>\d+)(?:, | and |, and ))*(?<n>\d+)-Cost (?=\S)")]
    private static partial Regex CostListRegex();

    [GeneratedRegex(@"\G(odd|even)-(Cost|Attack|Health) (?=\S)", RegexOptions.IgnoreCase)]
    private static partial Regex ParityRegex();

    // "Fire spell", "Shadow Spells", "Holy and Shadow spell".
    [GeneratedRegex(@"\G(?<s>Arcane|Fel|Fire|Frost|Holy|Nature|Shadow)(?:(?:,| and|,? or) (?<s>Arcane|Fel|Fire|Frost|Holy|Nature|Shadow))* (?=[Ss]pells?\b)")]
    private static partial Regex SchoolRegex();

    // "Blood Rune card", "Unholy Rune card", "Blood, Frost or Unholy card" (only before "card": "Frost spell" is a school).
    [GeneratedRegex(@"\G(?<r>Blood|Frost|Unholy)(?:(?:,| and|,? or) (?<r>Blood|Frost|Unholy))*(?: Rune)? (?=cards?\b)")]
    private static partial Regex RuneRegex();

    [GeneratedRegex(@"\G(Beast|Demon|Dragon|Elemental|Mech|Murloc|Pirate|Naga|Undead|Totem|Draenei|Quilboar)s?\b")]
    private static partial Regex TribeRegex();

    [GeneratedRegex(@"\G ?(Hero cards?|[Hh]eroes|cards?|minions?|[Ss]pells?|weapons?|locations?|Secrets?)\b")]
    private static partial Regex NounRegex();

    [GeneratedRegex(@"\G(?:,? or|,? and|,)(?= )")]
    private static partial Regex ListSeparatorRegex();

    [GeneratedRegex(@"\G,? (?:that|which) costs? \((?<n>\d+)\)(?: or (?<dir>more|less))?|\G,? costing \((?<n>\d+)\) or (?<dir>more|less)")]
    private static partial Regex CostLimitRegex();

    [GeneratedRegex(@"\G from (another class|a different class|other classes|(?i:any) class|your class|your opponent's class|your opponent's classes|the opponent's class|the past)\b")]
    private static partial Regex FromRegex();

    [GeneratedRegex(@"\G(?: (?:from|in|on|of) (?:your|their|the|an|each|both|your opponent's|the opponent's|the enemy|each player's|both players') ?(?:opponent's |enemy )?(?:hand|deck|hands|decks|graveyard|board|side)| (?:that|which) (?:died|you've|you|your|was|were|has|have|started|didn't)| you've| your opponent (?:has|played))")]
    private static partial Regex ElsewhereRegex();

    [GeneratedRegex(@"\G (?:with|and) an? Dark Gift\b")]
    private static partial Regex DarkGiftRegex();

    [GeneratedRegex(@"\G with (\d+) or more Attack\b")]
    private static partial Regex MinAttackRegex();

    [GeneratedRegex(@"\G with multiple minion types\b")]
    private static partial Regex MultipleTypesRegex();

    [GeneratedRegex(@"\G(friendly|enemy|your|their|your opponent's|the opponent's) (?=\S)")]
    private static partial Regex OwnerRegex();

    [GeneratedRegex(@"\G (?:enem(?:y|ies)|friendly|player'?s?|adjacent|opponents?|targets?|characters?|ally|allies)\b")]
    private static partial Regex TargetRegex();

    [GeneratedRegex(@"\G with (?=[A-Z])")]
    private static partial Regex WithKeywordRegex();

    [GeneratedRegex(@"\G(?:$|[.,;:!?)(]| (?:and|to|into|for|then|instead|if|when|whenever|after|until|It|They|it|they|at|on|plus|each|as|but|-)\b| -)")]
    private static partial Regex EndRegex();
}
