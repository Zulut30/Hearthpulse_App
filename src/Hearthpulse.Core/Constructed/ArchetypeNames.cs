using System.Globalization;
using System.Text;

namespace Hearthpulse.Core.Constructed;

/// <summary>
/// HSGuru archetype names: the class they belong to, a normalised form for matching HSReplay's names, and the Russian
/// label. The labels and the matcher come from the HeartPulse site (server/modules/constructedArchetypes:
/// fallbackTranslations.ts, archetypeLabel.ts) so the app and the site name archetypes alike.
/// </summary>
public static class ArchetypeNames
{
    private static readonly (string Word, string Class)[] ClassWords =
    [
        ("death knight", "DEATHKNIGHT"), ("demon hunter", "DEMONHUNTER"), ("druid", "DRUID"), ("hunter", "HUNTER"),
        ("mage", "MAGE"), ("paladin", "PALADIN"), ("priest", "PRIEST"), ("rogue", "ROGUE"), ("shaman", "SHAMAN"),
        ("warlock", "WARLOCK"), ("warrior", "WARRIOR"),
    ];

    /// <summary>Built-in Russian labels of HSGuru names (HeartPulse <c>ARCHETYPE_FALLBACK_LABELS</c>).</summary>
    private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["Ace Hunter"] = "Эйс Охотник",
        ["Aggro Paladin"] = "Агро Паладин",
        ["Animancer Warlock"] = "Анимансер Чернокнижник",
        ["Ashamane Rogue"] = "Ашамейн Разбойник",
        ["Aura Paladin"] = "Аура Паладин",
        ["Ayaya Rogue"] = "Ая Разбойник",
        ["Azshara Druid"] = "Азшара Друид",
        ["Blood Warrior"] = "Кровавый Воин",
        ["Briarspawn Warrior"] = "Брайарспаун Воин",
        ["Broxigar DH"] = "Броксигар Охотник на демонов",
        ["Burn Mage"] = "Берн Маг",
        ["Burn Rogue"] = "Берн Разбойник",
        ["Burn Warrior"] = "Берн Воин",
        ["C'Thun Warlock"] = "К'Тун Чернокнижник",
        ["Chef Druid"] = "Шеф-повар Друид",
        ["Companion Hunter"] = "Компаньон Охотник",
        ["Control Priest"] = "Контроль Жрец",
        ["Dino Egglock"] = "Дино Кхелос Чернокнижник",
        ["Divergence Warlock"] = "Дивергенция Чернокнижник",
        ["Dragon Druid"] = "Дракон Друид",
        ["Dragon Hunter"] = "Дракон Охотник",
        ["Dragon Warrior"] = "Дракон Воин",
        ["Dude Paladin"] = "Токен Паладин",
        ["Egg Paladin"] = "Кхелос Паладин",
        ["Egg Warrior"] = "Кхелос Воин",
        ["Egglock"] = "Кхелос Чернокнижник",
        ["Elemental Mage"] = "Элементаль Маг",
        ["Elwynn Boar Warlock"] = "Чернокнижник на Эльвинских вепрях",
        ["End of Turnadin"] = "Ноздорму Паладин",
        ["Enrage Warrior"] = "Исступление Воин",
        ["Face Hunter"] = "Фейс Охотник",
        ["Frost DK"] = "Фрост Рыцарь смерти",
        ["Glacial Shaman"] = "Ледяной Шаман",
        ["Gladiator Warrior"] = "Гладиатор Воин",
        ["Hand Warlock"] = "Хендлок",
        ["Harold DH"] = "Охотник на демонов на возвещении",
        ["Harold DK"] = "Рыцарь смерти на возвещении",
        ["Harold Egglock"] = "Кхелос Чернокнижник на возвещении",
        ["Harold Rogue"] = "Разбойник на возвещении",
        ["Harold Shaman"] = "Шаман на возвещении",
        ["Harold Warrior"] = "Воин на возвещении",
        ["Herald DH"] = "Охотник на демонов на возвещении",
        ["Herald DK"] = "Рыцарь смерти на возвещении",
        ["Herald DeathKnight"] = "Рыцарь смерти на возвещении",
        ["Herald Egg Warlock"] = "Кхелос Чернокнижник на возвещении",
        ["Herald Rogue"] = "Разбойник на возвещении",
        ["Herald Shaman"] = "Шаман на возвещении",
        ["Herald Warrior"] = "Воин на возвещении",
        ["Hostage Druid"] = "Заложник Друид",
        ["Imbue Paladin"] = "Паладин на силе героя",
        ["Imbue Priest"] = "Жрец на силе героя",
        ["Imbue Rogue"] = "Разбойник на силе героя",
        ["Insanity Warlock"] = "Инсанити Чернокнижник",
        ["Krona Druid"] = "Крона Друид",
        ["Leyline Mage"] = "Лейлайн Маг",
        ["Manastorm Mage"] = "Манашторм Маг",
        ["Merithra Druid"] = "Меритра Друид",
        ["Mug Shaman"] = "Кружечный Шаман",
        ["No Hand Hunter"] = "Охотник без руки",
        ["No Minion DH"] = "Спелл Охотник на демонов",
        ["Pain Warlock"] = "Пейнлок",
        ["Pure Paladin"] = "Чистый Паладин",
        ["Quest DH"] = "Квест Охотник на демонов",
        ["Quest Druid"] = "Квест Друид",
        ["Quest Hunter"] = "Квест Охотник",
        ["Quest Mage"] = "Квест Маг",
        ["Quest Rogue"] = "Квест Разбойник",
        ["Quest Shaman"] = "Квест Шаман",
        ["Quest Warrior"] = "Квест Воин",
        ["Rafaam Warlock"] = "Рафаам Чернокнижник",
        ["Rafaamlock"] = "Рафаам Чернокнижник",
        ["Renathal Big Warlock"] = "Ренатал Биг Чернокнижник",
        ["Renathal Reno Tick Tock Egg Warlock"] = "Ренатал Рено Тик-Ток Кхелос Чернокнижник",
        ["Renathal Reno Tick Tock Warlock"] = "Ренатал Рено Тик-Ток Чернокнижник",
        ["Renathal Tick Tock Warlock"] = "Ренатал Тик-Ток Чернокнижник",
        ["Seed Warlock"] = "Квестлайн Чернокнижник",
        ["Soothsayer Priest"] = "Предсказатель Жрец",
        ["Thief Priest"] = "Вор Жрец",
        ["Token Druid"] = "Токен Друид",
        ["Two Rogue"] = "Двухбитный Разбойник",
        ["Unholy DK"] = "Нечестивый Рыцарь смерти",
        ["Unholy DeathKnight"] = "Нечестивый Рыцарь смерти",
        ["Vanessa Rogue"] = "Ванесса Разбойник",
        ["Void DemonHunter"] = "Бездна Охотник на демонов",
        ["Wallow Warlock"] = "Валлоу Чернокнижник",
        ["Zee Shaman"] = "Зи Шаман",
    }.ToDictionary(p => TranslationKey(p.Key), p => p.Value, StringComparer.Ordinal);

    /// <summary>
    /// "Quest DH" → "quest demon hunter", "Zoolock" → "zoo warlock", "Harold DK" → "herald death knight": lower case,
    /// no punctuation, class abbreviations spelled out, and HSGuru's "Harold" read as HSReplay's "Herald".
    /// </summary>
    public static string Normalize(string name)
    {
        var words = new List<string>();
        foreach (var word in Words(name))
        {
            switch (word)
            {
                case "dh" or "demonhunter":
                    words.AddRange(["demon", "hunter"]);
                    break;
                case "dk" or "deathknight":
                    words.AddRange(["death", "knight"]);
                    break;
                case "pally":
                    words.Add("paladin");
                    break;
                case "harold":
                    words.Add("herald");
                    break;
                case { Length: > 4 } when word.EndsWith("lock", StringComparison.Ordinal) && word != "warlock":
                    words.AddRange([word[..^4], "warlock"]);
                    break;
                default:
                    words.Add(word);
                    break;
            }
        }

        return string.Join(' ', words);
    }

    /// <summary>The class an archetype name spells out ("DEMONHUNTER" for "Quest DH"), or null when it names none.</summary>
    public static string? ClassOf(string name)
    {
        var padded = " " + Normalize(name) + " ";
        return ClassWords.FirstOrDefault(c => padded.Contains(" " + c.Word + " ", StringComparison.Ordinal)).Class;
    }

    /// <summary>True when two names are the same archetype once normalised (word order does not matter).</summary>
    public static bool SameArchetype(string a, string b) =>
        new HashSet<string>(Normalize(a).Split(' ')).SetEquals(Normalize(b).Split(' '));

    /// <summary>
    /// The Russian label: the exact translation, else the longest translated name inside it with the other words in
    /// brackets ("Harold C'Thun Warlock" → its own label or "… (C'Thun)"), else the English name unchanged.
    /// </summary>
    public static string RussianLabel(string name)
    {
        var key = TranslationKey(name);
        if (key.Length == 0)
        {
            return name;
        }

        if (Labels.TryGetValue(key, out var exact))
        {
            return exact;
        }

        var best = Labels.Keys.Where(k => key.Contains(k, StringComparison.Ordinal)).MaxBy(k => k.Length);
        if (best is null)
        {
            return name;
        }

        var trimmed = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var start = key.IndexOf(best, StringComparison.Ordinal);
        var qualifier = (trimmed[..start] + " " + trimmed[(start + best.Length)..]).Trim(' ', '-', '–', '—', ':', '·', ',');
        qualifier = string.Join(' ', qualifier.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return qualifier.Length > 0 ? $"{Labels[best]} ({qualifier})" : Labels[best];
    }

    /// <summary>
    /// Labels made distinct within one list: when two archetypes still share a label, all but the shortest-named one
    /// add their English name in brackets (HeartPulse <c>disambiguateArchetypeLabels</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Distinct(IReadOnlyDictionary<string, string> labels)
    {
        var result = new Dictionary<string, string>(labels, StringComparer.Ordinal);
        foreach (var group in labels.GroupBy(p => p.Value).Where(g => g.Select(p => p.Key.ToLowerInvariant()).Distinct().Count() > 1))
        {
            var kept = group.OrderBy(p => p.Key.Length).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;
            foreach (var (english, label) in group)
            {
                if (!string.Equals(english, kept, StringComparison.OrdinalIgnoreCase))
                {
                    result[english] = $"{label} ({english})";
                }
            }
        }

        return result;
    }

    /// <summary>Trimmed, single-spaced, lower case (the site's <c>archetypeTranslationKey</c>).</summary>
    private static string TranslationKey(string name) =>
        string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLower(CultureInfo.InvariantCulture);

    private static IEnumerable<string> Words(string name)
    {
        var word = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                word.Append(ch);
            }
            else if (ch is not ('\'' or '’') && word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            yield return word.ToString();
        }
    }
}
