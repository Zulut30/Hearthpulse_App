using System.Text.Json;

namespace Hearthpulse.Tests;

/// <summary>docs/analytics-metrics.json: every metric is fully defined and every field it names has a checked source.</summary>
public sealed class AnalyticsCatalogTests
{
    private static readonly string[] Required =
        ["id", "version", "area", "numerator", "denominator", "unit", "fields", "missing", "groupBy", "smallSample", "display", "status"];
    private static readonly string[] Areas = ["results", "bg", "heroes", "shop", "economy", "combat", "builds", "arena", "mulligan"];
    private static readonly string[] FieldStatuses = ["have", "derived", "add", "unavailable"];

    private static JsonDocument Catalog() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoFiles.Root, "docs", "analytics-metrics.json")));

    [Fact]
    public void Catalog_is_version_1_and_every_metric_has_every_required_property()
    {
        using var doc = Catalog();
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metric in root.GetProperty("metrics").EnumerateArray())
        {
            foreach (var name in Required)
                Assert.True(metric.TryGetProperty(name, out _), $"{Text(metric, "id")}: missing {name}");
            string id = Text(metric, "id");
            Assert.True(ids.Add(id), $"duplicate metric {id}");
            Assert.Contains(Text(metric, "area"), Areas);
            Assert.True(metric.GetProperty("version").GetInt32() >= 1);
            Assert.Contains(Text(metric, "status"), new[] { "implemented", "defined", "needs_fields" });
            foreach (var name in new[] { "numerator", "denominator", "unit", "missing", "smallSample", "display" })
                Assert.False(string.IsNullOrWhiteSpace(Text(metric, name)), $"{id}: empty {name}");
            Assert.NotEqual(0, metric.GetProperty("fields").GetArrayLength());
            Assert.NotEqual(0, metric.GetProperty("groupBy").GetArrayLength());
        }
    }

    [Fact]
    public void Every_field_and_group_is_in_the_field_matrix_with_a_status_and_a_source()
    {
        using var doc = Catalog();
        var matrix = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in doc.RootElement.GetProperty("fieldMatrix").EnumerateArray())
        {
            string field = Text(row, "field");
            Assert.Contains(Text(row, "status"), FieldStatuses);
            Assert.False(string.IsNullOrWhiteSpace(Text(row, "source")), $"{field}: no source");
            Assert.True(matrix.TryAdd(field, Text(row, "status")), $"duplicate field {field}");
        }
        foreach (var metric in doc.RootElement.GetProperty("metrics").EnumerateArray())
        {
            string id = Text(metric, "id");
            var fields = metric.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToArray();
            foreach (var field in fields.Concat(metric.GetProperty("groupBy").EnumerateArray().Select(f => f.GetString()!)))
                Assert.True(matrix.ContainsKey(field), $"{id}: {field} is not in fieldMatrix");
            // The status follows the fields: a number cannot be computed from a field nobody records.
            bool available = fields.All(f => matrix[f] is "have" or "derived");
            Assert.True(available == (Text(metric, "status") != "needs_fields"), $"{id}: status {Text(metric, "status")} contradicts its fields");
        }
    }

    [Fact]
    public void All_nine_areas_of_the_plan_have_a_metric()
    {
        using var doc = Catalog();
        Assert.Equal(Areas, doc.RootElement.GetProperty("areas").EnumerateArray().Select(a => Text(a, "id")));
        var used = doc.RootElement.GetProperty("metrics").EnumerateArray().Select(m => Text(m, "area")).ToHashSet(StringComparer.Ordinal);
        Assert.All(Areas, area => Assert.Contains(area, used));
    }

    [Fact]
    public void The_bg_hero_metric_is_the_one_implemented_with_rules_bg_hero_1()
    {
        using var doc = Catalog();
        var metrics = doc.RootElement.GetProperty("metrics").EnumerateArray().ToArray();
        var hero = Assert.Single(metrics, m => Text(m, "id") == "bg.hero.offer_pick_placement");
        Assert.Equal("implemented", Text(hero, "status"));
        Assert.Equal("bg-hero-1", Text(hero, "rulesVersion"));
        Assert.Equal("heroes", Text(hero, "area"));
        Assert.Equal(new[] { "offers", "outcome" }, hero.GetProperty("eligibility").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "start.build" }, hero.GetProperty("groupBy").EnumerateArray().Select(e => e.GetString()));
        Assert.Single(metrics, m => Text(m, "status") == "implemented");
        // An implemented metric names the rules version its numbers were computed with; nothing else claims one.
        Assert.All(metrics.Where(m => m.TryGetProperty("rulesVersion", out _)), m => Assert.Equal("implemented", Text(m, "status")));
    }

    // The pool publishes no number made of fewer than 5 installations (docs/analytics-api.md); the catalog says so per metric.
    [Fact]
    public void Every_small_sample_rule_carries_the_five_installation_threshold()
    {
        using var doc = Catalog();
        var metrics = doc.RootElement.GetProperty("metrics").EnumerateArray().ToDictionary(m => Text(m, "id"), m => Text(m, "smallSample"), StringComparer.Ordinal);
        foreach (var (id, rule) in metrics)
        {
            var resolved = rule.StartsWith("as ", StringComparison.Ordinal) ? metrics[rule[3..]] : rule;
            Assert.Contains("5", resolved, StringComparison.Ordinal);
            Assert.Contains("installations", resolved, StringComparison.Ordinal);
            Assert.Contains("10", resolved, StringComparison.Ordinal);
            Assert.False(resolved.StartsWith("as ", StringComparison.Ordinal), $"{id}: chained reference");
        }
        string hero = metrics["bg.hero.offer_pick_placement"];
        foreach (var part in new[] { "ANALYTICS_MIN_SOURCES", "ANALYTICS_MIN_CELL", "owner", "suppressed", "picked and pickRate", "placement, top4 and first", "dist" })
            Assert.Contains(part, hero, StringComparison.Ordinal);
    }

    [Fact]
    public void The_markdown_page_lists_every_metric_of_the_json()
    {
        using var doc = Catalog();
        string page = File.ReadAllText(Path.Combine(RepoFiles.Root, "docs", "analytics-metrics.md"));
        foreach (var metric in doc.RootElement.GetProperty("metrics").EnumerateArray())
            Assert.Contains("`" + Text(metric, "id") + "`", page, StringComparison.Ordinal);
    }

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString() ?? "";
}
