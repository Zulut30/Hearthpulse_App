using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Hearthpulse.Core.Localization;

/// <summary>
/// The interface language and its strings. Strings live in embedded JSON files, one set per language
/// (<c>Localization/Strings/en/*.json</c>, <c>ru/*.json</c>): a flat map of key to text. English is complete; another
/// language falls back to English for a key it lacks. Both UIs bind to <see cref="Instance"/>'s indexer
/// (<c>{i:Loc Settings.Title}</c>), which updates every bound text when the language changes. See <c>docs/localization.md</c>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary>English: the source language and the fallback for every key.</summary>
    public const string English = "en";

    /// <summary>Russian.</summary>
    public const string Russian = "ru";

    /// <summary>Settings value that follows the operating system's language.</summary>
    public const string Auto = "auto";

    /// <summary>Languages the interface ships in.</summary>
    public static IReadOnlyList<string> Languages { get; } = [English, Russian];

    private const string ResourcePrefix = "Hearthpulse.Loc.";

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables = LoadTables();

    private readonly HashSet<string> _reportedMissing = new(StringComparer.Ordinal);

    internal Loc()
    {
    }

    /// <summary>The one instance the UIs bind to.</summary>
    public static Loc Instance { get; } = new();

    /// <summary>The current language: <see cref="English"/> or <see cref="Russian"/>.</summary>
    public string Language { get; private set; } = English;

    /// <summary>Whether the interface is in Russian.</summary>
    public static bool IsRussian => Instance.Language == Russian;

    /// <summary>
    /// Culture for dates the interface writes out ("23 окт." in Russian, "23 Oct" in English, whatever the system
    /// language): month names follow the interface language. Numbers keep the system's format, like the rest of the UI.
    /// </summary>
    public static CultureInfo DateCulture => Instance.Language == Russian
        ? CultureInfo.GetCultureInfo("ru-RU")
        : CultureInfo.CurrentCulture.TwoLetterISOLanguageName == English ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo("en-US");

    /// <summary>The HearthstoneJSON card locale that goes with the current language ("ruRU" or "enUS").</summary>
    public static string CardLocale => CardLocaleOf(Instance.Language);

    /// <summary>The text of <paramref name="key"/> in the current language (what <c>{i:Loc Settings.Title}</c> binds to).</summary>
    public string this[string key] => Lookup(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changed, on the thread that changed it.</summary>
    public event Action? LanguageChanged;

    /// <summary>The text of <paramref name="key"/> in the current language.</summary>
    public static string T(string key) => Instance.Lookup(key);

    /// <summary>
    /// The text of <paramref name="key"/> with <c>{0}</c>-style placeholders filled, using the current culture's number
    /// format as the rest of the UI does.
    /// </summary>
    public static string F(string key, params object?[] args)
    {
        var format = Instance.Lookup(key);
        try
        {
            return string.Format(Formatter.Instance, format, args);
        }
        catch (FormatException)
        {
            Instance.Report(key, "bad format");
            return format;
        }
    }

    /// <summary>
    /// The plural form of <paramref name="key"/> for <paramref name="count"/>, with <c>{0}</c> as the count. The text
    /// holds the forms separated by <c>|</c>: English "one|other", Russian "one|few|many" (1 игра, 2 игры, 5 игр).
    /// </summary>
    public static string P(string key, long count, params object?[] more)
    {
        var forms = Instance.Lookup(key).Split('|');
        var form = forms[Math.Min(PluralIndex(Instance.Language, count), forms.Length - 1)];
        var args = new object?[more.Length + 1];
        args[0] = count;
        more.CopyTo(args, 1);
        try
        {
            return string.Format(Formatter.Instance, form, args);
        }
        catch (FormatException)
        {
            Instance.Report(key, "bad format");
            return form;
        }
    }

    /// <summary>
    /// The text of <paramref name="key"/> when the current language has one, else <paramref name="fallback"/>: for
    /// names defined in code (counters, tribes) whose English stays next to their definition.
    /// </summary>
    public static string Or(string key, string fallback) =>
        Tables.TryGetValue(Instance.Language, out var table) && table.TryGetValue(key, out var text) ? text : fallback;

    /// <summary>Whether <paramref name="key"/> exists in the English table.</summary>
    public static bool Has(string key) => Tables.TryGetValue(English, out var table) && table.ContainsKey(key);

    /// <summary>Every key of <paramref name="language"/>'s table (for tests and tools).</summary>
    public static IReadOnlyDictionary<string, string> Table(string language) =>
        Tables.TryGetValue(language, out var table) ? table : new Dictionary<string, string>();

    /// <summary>
    /// The language a settings value stands for: "en" or "ru" as is; anything else ("auto") Russian when the installed
    /// Hearthstone client (<paramref name="clientLocale"/>, e.g. "ruRU") or the operating system is Russian, else English.
    /// </summary>
    public static string Resolve(string? setting, CultureInfo? system = null, string? clientLocale = null)
    {
        if (setting is { } value && Languages.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return value.ToLowerInvariant();
        }

        if (clientLocale is not null && clientLocale.StartsWith(Russian, StringComparison.OrdinalIgnoreCase))
        {
            return Russian;
        }

        var culture = system ?? CultureInfo.CurrentUICulture;
        return culture.TwoLetterISOLanguageName.Equals(Russian, StringComparison.OrdinalIgnoreCase) ? Russian : English;
    }

    /// <summary>The HearthstoneJSON card locale for an interface language.</summary>
    public static string CardLocaleOf(string language) => language == Russian ? "ruRU" : "enUS";

    /// <summary>Switches the language; bound texts update and <see cref="LanguageChanged"/> fires when it differs.</summary>
    public void SetLanguage(string language)
    {
        var resolved = Resolve(language);
        if (resolved == Language)
        {
            return;
        }

        Language = resolved;
        // Every indexer binding re-reads: WPF listens for "Item[]", Avalonia for "Item".
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke();
    }

    /// <summary>
    /// The plural form index for <paramref name="count"/>: English 0 for one, 1 otherwise; Russian 0 for 1, 21, 31...,
    /// 1 for 2-4, 22-24..., 2 for the rest.
    /// </summary>
    internal static int PluralIndex(string language, long count)
    {
        var n = Math.Abs(count);
        if (language != Russian)
        {
            return n == 1 ? 0 : 1;
        }

        var lastTwo = n % 100;
        var last = n % 10;
        return last == 1 && lastTwo != 11 ? 0
            : last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? 1
            : 2;
    }

    private string Lookup(string key)
    {
        if (Tables.TryGetValue(Language, out var table) && table.TryGetValue(key, out var text))
        {
            return text;
        }

        if (Tables.TryGetValue(English, out var english) && english.TryGetValue(key, out var fallback))
        {
            return fallback;
        }

        Report(key, "missing");
        return key;
    }

    private void Report(string key, string problem)
    {
        lock (_reportedMissing)
        {
            if (!_reportedMissing.Add(key))
            {
                return;
            }
        }

        Diag.Info($"Localization: {problem} string '{key}'");
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> LoadTables()
    {
        var assembly = typeof(Loc).Assembly;
        var tables = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var language in Languages)
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix + language + ".", StringComparison.Ordinal)).Order())
            {
                Read(assembly, name, table);
            }

            tables[language] = table;
        }

        return tables;
    }

    private static void Read(Assembly assembly, string name, Dictionary<string, string> table)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return;
            }

            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    table[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException ex)
        {
            Diag.Error($"Localization file {name} unreadable", ex);
        }
    }

    /// <summary>Format provider of <see cref="F"/> and <see cref="P"/>: dates in <see cref="DateCulture"/>, the rest in the current culture.</summary>
    private sealed class Formatter : IFormatProvider, ICustomFormatter
    {
        public static readonly Formatter Instance = new();

        public object? GetFormat(Type? formatType) => formatType == typeof(ICustomFormatter) ? this : CultureInfo.CurrentCulture.GetFormat(formatType);

        public string Format(string? format, object? arg, IFormatProvider? formatProvider) => arg switch
        {
            DateTime or DateTimeOffset => ((IFormattable)arg).ToString(format, DateCulture),
            IFormattable formattable => formattable.ToString(format, CultureInfo.CurrentCulture),
            _ => arg?.ToString() ?? string.Empty,
        };
    }
}
