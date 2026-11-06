using System.Globalization;
using System.Text;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Infrastructure;
#else
namespace Hearthpulse.UI.Infrastructure;
#endif

/// <summary>
/// The Settings search: every option is found by its label in the interface language, by the same label in the other
/// language and by a few extra words (<see cref="Keywords"/>), so "scale", "размер" and "масштаб" all find the overlay
/// size. Words match by their start, ignoring case, "ё" and punctuation; every word of the query must match.
/// The views collect the options from their Settings tabs and jump to the one picked.
/// </summary>
public static class SettingsSearch
{
    /// <summary>The most results a query lists.</summary>
    public const int MaxResults = 12;

    /// <summary>Extra words (ru and en) for options whose label does not say what players search for.</summary>
    public static IReadOnlyDictionary<string, string> Keywords { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Settings.Overlay.Size"] = "масштаб увеличить уменьшить scale zoom bigger smaller",
        ["Settings.Overlay.Opacity"] = "прозрачность прозрачный transparency transparent",
        ["Settings.Overlay.Enabled"] = "оверлей включить выключить overlay enable disable",
        ["Settings.Overlay.Density"] = "строки компактно rows compact",
        ["Settings.Language.Label"] = "язык русский английский language english russian",
        ["Settings.App.AutoUpdate"] = "обновление версия update version",
        ["Settings.App.StartWithWindows"] = "автозапуск автозагрузка autostart startup boot",
        ["Settings.App.Tray"] = "трей закрыть свернуть tray close minimize",
        ["Settings.App.ReduceMotion"] = "анимация движение animation motion",
        ["Settings.Gallery.Title"] = "тема цвет оформление цвета theme colour color skin",
        ["Settings.Accent.Title"] = "цвет акцент цвета colour color accent",
        ["Settings.Background.Title"] = "фон герой арт картинка персонаж background hero art picture",
        ["Settings.Shape.Corners"] = "углы скругление закругление corners rounded radius",
        ["Settings.Shape.Density"] = "плотность отступы компактно density spacing compact",
        ["Settings.Shape.Scale"] = "шрифт текст размер масштаб font text size scale zoom",
        ["Settings.Layout.Arrange"] = "панели расположение двигать panels layout move",
        ["Settings.Leaderboard.Title"] = "калибровка лидеры calibration leaderboard",
        ["Settings.Counters.Show"] = "счётчики counters",
        ["Settings.Account.Title"] = "аккаунт вход профиль войти account login sign profile",
        ["Settings.File.Title"] = "файл экспорт импорт перенос резервная копия file export import backup transfer",
        ["Settings.Reset.All"] = "сброс по умолчанию reset default defaults",
        ["Stream.Mode.Switch"] = "стрим obs twitch скрыть ники stream hide names",
        ["Discord.Switch"] = "discord дискорд статус status",
    };

    private static readonly Dictionary<string, Dictionary<string, List<string>>> KeysByText = new(StringComparer.Ordinal);

    /// <summary>What a label is found by: the label, its Loc key's text in every language and the key's keywords.</summary>
    public static string Haystack(string label, string? key = null)
    {
        var words = new StringBuilder(label);
        foreach (var k in key is null ? KeysOf(label) : [key])
        {
            foreach (var language in Loc.Languages)
            {
                if (Loc.Table(language).TryGetValue(k, out var text))
                {
                    words.Append(' ').Append(text);
                }
            }

            if (Keywords.TryGetValue(k, out var extra))
            {
                words.Append(' ').Append(extra);
            }
        }

        return Normalize(words.ToString());
    }

    /// <summary>Lower case, "ё" as "е", letters and digits only, single spaces.</summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = true;
        foreach (var c in text.ToLower(CultureInfo.InvariantCulture))
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c == 'ё' ? 'е' : c);
                space = false;
            }
            else if (!space)
            {
                builder.Append(' ');
                space = true;
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Every word of the query starts a word of the haystack (both normalized).</summary>
    public static bool Matches(string query, string haystack)
    {
        var words = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return false;
        }

        var hay = " " + haystack;
        return words.All(w => hay.Contains(" " + w, StringComparison.Ordinal));
    }

    /// <summary>
    /// The options matching <paramref name="query"/>, best first: a label that starts with the query, then one whose
    /// words do, then a match in the other language or the keywords; at most <see cref="MaxResults"/>.
    /// </summary>
    public static IReadOnlyList<T> Find<T>(string query, IEnumerable<T> items, Func<T, string> label, Func<T, string> haystack)
    {
        var q = Normalize(query);
        if (q.Length == 0)
        {
            return [];
        }

        return items
            .Select(item => (Item: item, Label: Normalize(label(item)), Hay: haystack(item)))
            .Where(x => Matches(q, x.Hay))
            .OrderBy(x => x.Label.StartsWith(q, StringComparison.Ordinal) ? 0 : Matches(q, x.Label) ? 1 : 2)
            .ThenBy(x => x.Label.Length)
            .Take(MaxResults)
            .Select(x => x.Item)
            .ToList();
    }

    /// <summary>The Loc keys whose text in the interface language is <paramref name="label"/>.</summary>
    private static IReadOnlyList<string> KeysOf(string label)
    {
        var language = Loc.Instance.Language;
        lock (KeysByText)
        {
            if (!KeysByText.TryGetValue(language, out var map))
            {
                map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var (key, text) in Loc.Table(language))
                {
                    if (!map.TryGetValue(text, out var keys))
                    {
                        map[text] = keys = [];
                    }

                    keys.Add(key);
                }

                KeysByText[language] = map;
            }

            return map.TryGetValue(label, out var found) ? found : [];
        }
    }
}
