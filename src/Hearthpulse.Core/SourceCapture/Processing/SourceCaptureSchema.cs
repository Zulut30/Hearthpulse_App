using System.Text.Json;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.SourceCapture.Processing;

// The allowlist belongs to the wire version, not to the much wider raw Power.log schema.
internal static class SourceCaptureSchema
{
    private static readonly HashSet<string> SafeTags = new(StringComparer.Ordinal)
    {
        GameTag.Turn, GameTag.Step, "RESOURCES", "RESOURCES_USED", "TEMP_RESOURCES", "OVERLOAD_LOCKED",
        "OVERLOAD_OWED", GameTag.Damage, GameTag.Health, GameTag.Atk, GameTag.Armor, "DURABILITY", GameTag.Cost,
        "TECH_LEVEL", GameTag.Premium, GameTag.ZonePosition, GameTag.Taunt, GameTag.DivineShield,
        GameTag.Windfury, GameTag.Reborn, "STEALTH", "FROZEN", GameTag.Poisonous, GameTag.Venomous,
    };

    internal static void Require(bool condition)
    {
        if (!condition) throw new SourceCaptureException("invalid_source");
    }

    internal static void Fields(JsonElement value, params string[] names)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in value.EnumerateObject())
            Require(names.Contains(p.Name, StringComparer.Ordinal) && seen.Add(p.Name));
        Require(seen.Count == names.Length);
    }

    internal static int Number(JsonElement value, int min = 0, int max = 1_000_000)
    {
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _));
        int n = value.GetInt32();
        Require(n >= min && n <= max);
        return n;
    }

    internal static int? NullableNumber(JsonElement value, int min = 0, int max = 1_000_000) =>
        value.ValueKind == JsonValueKind.Null ? null : Number(value, min, max);

    internal static string Text(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.String);
        return value.GetString()!;
    }

    internal static string OneOf(JsonElement value, params string[] allowed)
    {
        string s = Text(value);
        Require(allowed.Contains(s, StringComparer.Ordinal));
        return s;
    }

    internal static bool Boolean(JsonElement value)
    {
        Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False);
        return value.GetBoolean();
    }

    internal static string? Card(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        string s = Text(value);
        Require(s.Length is >= 1 and <= 80 && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'));
        return s;
    }

    internal static string Side(JsonElement value) => OneOf(value, "none", "local", "opponent", "shop", "partner_public");

    internal static string Zone(JsonElement value) =>
        OneOf(value, "HAND", "PLAY", "DECK", "GRAVEYARD", "SETASIDE", "SHOP", "UNKNOWN");

    internal static bool Hidden(string side, string zone) =>
        side == "opponent" && zone is not ("PLAY" or "GRAVEYARD");

    internal static void Tags(JsonElement tags, bool hidden)
    {
        Require(tags.ValueKind == JsonValueKind.Object);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in tags.EnumerateObject())
        {
            Require(SafeTags.Contains(p.Name) && seen.Add(p.Name) && (!hidden || p.Name == GameTag.ZonePosition));
            Number(p.Value, -1_000_000);
        }
    }

    internal static void Visibility(string side, string zone, string? card)
    {
        Require(side != "none");
        Require(side != "shop" || zone == "SHOP");
        Require(side != "partner_public" || zone is "PLAY" or "GRAVEYARD");
        Require(side != "opponent" || zone != "SHOP");
        Require(!Hidden(side, zone) || card is null);
    }

    private static void Cards(JsonElement list, int max)
    {
        Require(list.ValueKind == JsonValueKind.Array && list.GetArrayLength() <= max);
        foreach (var c in list.EnumerateArray()) Require(Card(c) is not null);
    }

    internal static void Validate(SourceCaptureEvent e)
    {
        var d = e.Data;
        switch (e.Kind)
        {
            case "start":
                Fields(d, "captureKind", "mode", "day", "build");
                OneOf(d.GetProperty("captureKind"), "game", "draft");
                OneOf(d.GetProperty("mode"), "ranked", "casual", "arena", "brawl", "bg", "duos");
                Number(d.GetProperty("day"), 0, 100_000);
                NullableNumber(d.GetProperty("build"), 1, int.MaxValue);
                Require(e.Side == "none" && e.Entity == 0 && e.Target == 0 && e.Card is null && e.Turn == 0);
                break;
            case "end":
                Fields(d, "status", "result", "placement", "reason");
                OneOf(d.GetProperty("status"), "complete", "incomplete");
                if (d.GetProperty("result").ValueKind != JsonValueKind.Null)
                    OneOf(d.GetProperty("result"), "W", "L", "T");
                NullableNumber(d.GetProperty("placement"), 1, 8);
                OneOf(d.GetProperty("reason"), "normal", "abandoned", "rotation", "shutdown", "unsupported", "unknown");
                Neutral(e);
                break;
            case "entity":
                Fields(d, "zone", "position", "tags");
                string zone = Zone(d.GetProperty("zone"));
                Number(d.GetProperty("position"), 0, 1_000);
                Visibility(e.Side, zone, e.Card);
                Tags(d.GetProperty("tags"), Hidden(e.Side, zone));
                Require(e.Entity > 0);
                break;
            case "tag":
                Fields(d, "tag", "value");
                Require(SafeTags.Contains(Text(d.GetProperty("tag"))) && e.Side != "shop");
                Number(d.GetProperty("value"), -1_000_000);
                if (Text(d.GetProperty("tag")) == GameTag.Turn) Number(d.GetProperty("value"), 0, 1_000);
                // No zone accompanies a tag. Only local cards may be named by a standalone tag.
                Require(e.Side == "local" || e.Card is null);
                break;
            case "block_start":
            case "block_end":
                Fields(d, "block");
                OneOf(d.GetProperty("block"), "PLAY", "POWER", "TRIGGER", "ATTACK", "DEATHS", "MOVE_MINION", "OTHER");
                Require(e.Side == "local" || e.Card is null);
                break;
            case "choice":
                Fields(d, "choiceId", "sourceCard", "options", "picked", "resolved");
                Require(e.Side == "local");
                Number(d.GetProperty("choiceId"));
                Card(d.GetProperty("sourceCard"));
                Cards(d.GetProperty("options"), 64);
                Cards(d.GetProperty("picked"), 16);
                Require(d.GetProperty("options").GetArrayLength() > 0);
                bool resolved = Boolean(d.GetProperty("resolved"));
                Require(resolved || d.GetProperty("picked").GetArrayLength() == 0);
                var remaining = d.GetProperty("options").EnumerateArray().Select(Text).ToList();
                foreach (var picked in d.GetProperty("picked").EnumerateArray()) Require(remaining.Remove(Text(picked)));
                break;
            case "action":
                Fields(d, "action", "cost");
                string action = OneOf(d.GetProperty("action"), "play", "buy", "sell", "refresh", "freeze", "upgrade",
                    "move", "hero_power", "receive", "draw", "discard", "unknown");
                NullableNumber(d.GetProperty("cost"), 0, 1_000_000);
                Require(e.Side is "local" or "opponent" or "partner_public");
                Require(e.Side == "local" || e.Card is null || action is "play" or "hero_power" or "move");
                break;
            case "snapshot":
                Fields(d, "gold", "tier", "cards");
                Require(e.Side is "local" or "none");
                NullableNumber(d.GetProperty("gold"));
                NullableNumber(d.GetProperty("tier"), 1, 10);
                var cards = d.GetProperty("cards");
                Require(cards.ValueKind == JsonValueKind.Array && cards.GetArrayLength() <= 256);
                var refs = new HashSet<int>();
                foreach (var c in cards.EnumerateArray())
                {
                    Fields(c, "entity", "card", "side", "zone", "position", "tags");
                    Require(refs.Add(Number(c.GetProperty("entity"), 1)));
                    string side = Side(c.GetProperty("side")), z = Zone(c.GetProperty("zone"));
                    Visibility(side, z, Card(c.GetProperty("card")));
                    Number(c.GetProperty("position"), 0, 1_000);
                    Tags(c.GetProperty("tags"), Hidden(side, z));
                }
                break;
            case "draft_choice":
                Fields(d, "pick", "options", "picked", "underground");
                Require(e.Side == "local");
                Number(d.GetProperty("pick"), 0, 1_000);
                var options = d.GetProperty("options");
                Require(options.ValueKind == JsonValueKind.Array && options.GetArrayLength() is >= 1 and <= 64);
                foreach (var option in options.EnumerateArray())
                {
                    Fields(option, "card", "package");
                    Require(Card(option.GetProperty("card")) is not null);
                    Cards(option.GetProperty("package"), 64);
                }
                NullableNumber(d.GetProperty("picked"), 0, options.GetArrayLength() - 1);
                if (d.GetProperty("underground").ValueKind != JsonValueKind.Null) Boolean(d.GetProperty("underground"));
                break;
            case "gap":
                Fields(d, "reason", "count");
                OneOf(d.GetProperty("reason"), "late_attach", "rotation", "source_missing", "unsupported", "limit", "invalid", "shutdown");
                Number(d.GetProperty("count"), 1);
                Neutral(e);
                break;
            default:
                // A future safe discriminator can be counted, but arbitrary data must never be echoed.
                Require(e.Kind.Length is >= 1 and <= 40 && e.Kind.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'));
                Fields(d);
                Neutral(e);
                break;
        }
    }

    private static void Neutral(SourceCaptureEvent e) =>
        Require(e.Side == "none" && e.Entity == 0 && e.Target == 0 && e.Card is null);
}
