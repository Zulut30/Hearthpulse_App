using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Battlegrounds.Effects;
using Hearthpulse.Core.Cards;
using static Hearthpulse.Bench.Measure;

namespace Hearthpulse.Bench;

/// <summary>
/// The combat simulator's coverage matrix of the current pool (plan S1): every pool minion, the hero power of every hero
/// in the API's hero list, every trinket, Dark Gift and combat enchantment, with one of the six statuses of
/// <see cref="SimCoverage"/> and whether the card's pool membership is confirmed. Writes docs/bg-sim-matrix.md.
/// Inputs from the data folder copy (HEARTHPULSE_DATA): cache/cards.enUS.json (HearthstoneJSON), cache/bgcards.hsbg.json
/// (hsbg.cards: current pool), cache/api/bg-minions-all.json and cache/api/bg-heroes-*.json (Hearthpulse API).
/// </summary>
internal static class SimMatrix
{
    private const string Yes = "да";
    private const string Unconfirmed = "пул не подтверждён";
    private const string OutOfPool = "вне пула";

    private sealed record Row(CoverageKind Kind, string Id, string Name, Coverage Coverage, string Pool);

    public static void Run()
    {
        if (Environment.GetEnvironmentVariable("HEARTHPULSE_DATA") is not { Length: > 0 } data)
        {
            Console.WriteLine("[matrix] HEARTHPULSE_DATA is not set");
            return;
        }

        using var http = Http();
        var cards = CardDb.LoadAsync("enUS", http).GetAwaiter().GetResult();
        var raw = Path.Combine(data, "cache", "cards.enUS.json");
        if (cards.Count == 0 || !File.Exists(raw))
        {
            Console.WriteLine($"[matrix] no card data ({raw})");
            return;
        }

        var (heroPowerDbf, darkGifts) = ReadRawCards(raw);
        var apiMinions = ApiIds(Path.Combine(data, "cache", "api", "bg-minions-all.json"), "data", "card_id");
        var apiHeroes = Directory.EnumerateFiles(Path.Combine(data, "cache", "api"), "bg-heroes-*.json")
            .SelectMany(f => ApiIds(f, "heroes", "id")).ToHashSet(StringComparer.Ordinal);
        var hsbg = cards.BgCards;

        string PoolOf(string id) => hsbg.Count == 0 ? Unconfirmed : hsbg.Get(id) is not null ? Yes : OutOfPool;

        var rows = new List<Row>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(CoverageKind kind, Card card, string pool, string? classifyId = null, string? note = null)
        {
            if (!seen.Add(card.Id))
            {
                return;
            }

            var coverage = SimCoverage.Classify(classifyId ?? card.Id, kind, cards.Get(classifyId ?? card.Id)?.Text ?? card.Text);
            rows.Add(new Row(kind, card.Id, card.Name, note is null ? coverage : coverage with { Note = $"{note}; {coverage.Note}" }, pool));
        }

        // Minions: the card data's pool flag and the API's community list (which also holds current Timewarped minions).
        foreach (var card in cards.All.Where(c => c.Type == "MINION" && c.BattlegroundsNormalDbfId == 0
                                                  && (c.IsBattlegroundsPoolMinion || apiMinions.Contains(c.Id))))
        {
            var confirmed = apiMinions.Contains(card.Id) || hsbg.Get(card.Id) is { IsPoolCard: true };
            Add(CoverageKind.Minion, card, confirmed ? Yes : Unconfirmed);
        }

        // Hero powers of the heroes in the API's list (current patch, any rating band).
        foreach (var hero in apiHeroes.Select(cards.Get).OfType<Card>())
        {
            if (heroPowerDbf.TryGetValue(hero.Id, out var dbf) && cards.Get(dbf) is { } power)
            {
                Add(CoverageKind.HeroPower, power, Yes, note: hero.Name);
            }
        }

        foreach (var card in cards.All.Where(c => c.Type == "BATTLEGROUND_TRINKET"))
        {
            Add(CoverageKind.Trinket, card, PoolOf(card.Id));
        }

        // Dark Gifts: the spell is offered, the enchantment it leaves on the minion is what the simulator sees.
        foreach (var gift in darkGifts.Select(cards.Get).OfType<Card>())
        {
            var enchantment = cards.All
                .Where(c => c.Type == "ENCHANTMENT" && c.Name == gift.Name && c.Id.StartsWith(gift.Id, StringComparison.Ordinal))
                .OrderByDescending(c => CombatRelevance.Find(c.Id) is not null || EffectRegistry.Default.Get(c.Id, golden: false) is not null)
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (enchantment is not null)
            {
                seen.Add(enchantment.Id);
            }

            Add(CoverageKind.DarkGift, gift, PoolOf(gift.Id), enchantment?.Id, enchantment is null ? "зачарование не найдено" : enchantment.Id);
        }

        // Other combat enchantments: table rows and registered effects on enchantments.
        var enchantmentIds = CombatRelevance.Table.Where(r => r.Origin == Origin.Player).Select(r => r.CardId)
            .Concat(EffectRegistry.Default.NormalCardIds)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var card in enchantmentIds.Select(cards.Get).OfType<Card>().Where(c => c.Type == "ENCHANTMENT"))
        {
            Add(CoverageKind.Enchantment, card, Unconfirmed);
        }

        var path = Path.Combine(RepoRoot(), "docs", "bg-sim-matrix.md");
        File.WriteAllText(path, Markdown(rows, cards, hsbg.Count > 0), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine($"[matrix] {rows.Count} rows -> {path}");
        foreach (var group in rows.GroupBy(r => r.Coverage.Status).OrderBy(g => g.Key))
        {
            Console.WriteLine($"[matrix]   {SimCoverage.Code(group.Key),-15} {group.Count()}");
        }
    }

    private static string Markdown(List<Row> rows, CardDb cards, bool hsbgLoaded)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Матрица покрытия симулятора боя BG");
        sb.AppendLine();
        sb.AppendLine("<!-- Generated by `Hearthpulse.Bench simmatrix` (tools/Hearthpulse.Bench/SimMatrix.cs); do not edit by hand. -->");
        sb.AppendLine();
        var poolSource = hsbgLoaded ? "hsbg.cards (кэш)" : "hsbg.cards недоступен — принадлежность тринкетов и даров не подтверждена";
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Таблица релевантности: патч {CombatRelevance.Patch}. Данные карт: HearthstoneJSON enUS ({cards.Count} карт); пул: {poolSource}, список существ и героев API Hearthpulse (кэш).");
        sb.AppendLine();
        sb.AppendLine("Статусы (`SimCoverage`, те же правила, что помечают бой «not simulated»): `no_effect` — текст не меняет бой после " +
                      "снимка; `resolved` — Start of Combat, разрешился до снимка; `implemented` — есть эффект; `missing_effect` — меняет " +
                      "бой, эффекта нет; `missing_input` — эффекту нужна величина, которой нет в снимке; `unknown` — карты нет в таблицах. " +
                      "Пул: «да» — карта есть в текущем пуле (API или hsbg.cards), «вне пула» — hsbg.cards её не знает, " +
                      "«пул не подтверждён» — проверить нельзя. Как читать и обновлять — docs/bg-combat-sim.md.");
        sb.AppendLine();
        sb.AppendLine("## Итоги");
        sb.AppendLine();
        sb.AppendLine("| Вид | " + string.Join(" | ", SimCoverage.Codes) + " | всего |");
        sb.AppendLine("|---|" + string.Concat(Enumerable.Repeat("---:|", SimCoverage.Codes.Count + 1)));
        foreach (var kind in Enum.GetValues<CoverageKind>())
        {
            var ofKind = rows.Where(r => r.Kind == kind).ToList();
            var counts = Enum.GetValues<CoverageStatus>().Select(s => ofKind.Count(r => r.Coverage.Status == s).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {Title(kind)} | {string.Join(" | ", counts)} | {ofKind.Count} |");
        }

        var inPool = rows.Where(r => r.Pool != OutOfPool).ToList();
        var inPoolCounts = string.Join(" | ", Enum.GetValues<CoverageStatus>().Select(s => inPool.Count(r => r.Coverage.Status == s).ToString(CultureInfo.InvariantCulture)));
        sb.AppendLine(CultureInfo.InvariantCulture, $"| **Без «вне пула»** | {inPoolCounts} | {inPool.Count} |");
        foreach (var kind in Enum.GetValues<CoverageKind>())
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {Title(kind)}");
            sb.AppendLine();
            sb.AppendLine("| Id | Название | Статус | Пул | Примечание |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var row in rows.Where(r => r.Kind == kind)
                         .OrderBy(r => r.Coverage.Status).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal))
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| {row.Id} | {Cell(row.Name)} | {SimCoverage.Code(row.Coverage.Status)} | {row.Pool} | {Cell(row.Coverage.Note)} |");
            }
        }

        return sb.ToString().ReplaceLineEndings("\n");
    }

    private static string Title(CoverageKind kind) => kind switch
    {
        CoverageKind.Minion => "Существа",
        CoverageKind.HeroPower => "Силы героя",
        CoverageKind.Trinket => "Тринкеты",
        CoverageKind.DarkGift => "Тёмные дары",
        _ => "Боевые зачарования",
    };

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ');

    /// <summary>Hero card id → hero power dbfId, and the Dark Gift spell ids (fields CardDb does not keep).</summary>
    private static (Dictionary<string, int> HeroPowers, List<string> DarkGifts) ReadRawCards(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var heroPowers = new Dictionary<string, int>(StringComparer.Ordinal);
        var gifts = new List<string>();
        foreach (var card in doc.RootElement.EnumerateArray())
        {
            if (!card.TryGetProperty("id", out var id) || id.GetString() is not { } cardId)
            {
                continue;
            }

            if (card.TryGetProperty("heroPowerDbfId", out var hp) && hp.ValueKind == JsonValueKind.Number)
            {
                heroPowers[cardId] = hp.GetInt32();
            }

            if (card.TryGetProperty("isBattlegroundsDarkGift", out var gift) && gift.ValueKind == JsonValueKind.True
                && card.TryGetProperty("type", out var type) && type.GetString() == "SPELL")
            {
                gifts.Add(cardId);
            }
        }

        return (heroPowers, gifts);
    }

    /// <summary>The ids in a cached API list: <c>{"data": [...]}</c> or <c>{"data": {"heroes": [...]}}</c>.</summary>
    private static HashSet<string> ApiIds(string path, string list, string field)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return ids;
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var data = doc.RootElement.GetProperty("data");
        var items = data.ValueKind == JsonValueKind.Array ? data : data.TryGetProperty(list, out var inner) ? inner : default;
        if (items.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty(field, out var value) && value.GetString() is { Length: > 0 } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hearthpulse.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Hearthpulse.slnx not found above the bench");
    }
}
