using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hearthpulse.Core.Battlegrounds.Site;

/// <summary>
/// Which data of the service the app may read (the allow list of sources), cutting the rest from an answer before it
/// is cached, and telling an answer with data from one without.
/// </summary>
internal static partial class BgSiteParser
{
    /// <summary>
    /// The only origins the app shows (docs/bg-site-stats.md, section 4), as the service names them. A new origin is a
    /// new value here after a legal check, never a silent pass.
    /// </summary>
    private static readonly HashSet<string> AllowedSources = new(StringComparer.Ordinal)
    {
        "hsreplay",
        "hsreplay_battlegrounds_heroes",
        "hsreplay_battlegrounds_minions",
    };

    /// <summary>True when <paramref name="source"/> is one of the allowed origins; case and spaces do not matter. Nothing or anything else is not.</summary>
    internal static bool Allowed(string? source) =>
        source is not null && AllowedSources.Contains(string.Concat(source.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant());

    /// <summary>
    /// True when the list, row or answer may be shown: its <paramref name="source"/> is an allowed origin and neither of
    /// the fields that describe where it was taken from names Firestone (also known by its host, zerotoheroes).
    /// </summary>
    internal static bool Usable(string? source, string? upstream = null, string? url = null) =>
        Allowed(source) && !NamesFirestone(upstream) && !NamesFirestone(url);

    private static bool NamesFirestone(string? text) =>
        text is not null && (text.Contains("firestone", StringComparison.OrdinalIgnoreCase) || text.Contains("zerotoheroes", StringComparison.OrdinalIgnoreCase));

    /// <summary>The origin of a heroes answer is its <c>source_id</c>; a <c>site</c> it also names must be allowed too.</summary>
    private static bool HeroesUsable(string? sourceId, string? site) => Allowed(sourceId) && (site is null || Allowed(site));

    /// <summary>
    /// A heroes slice (<c>?mode=&amp;mmr=</c>) names no source of its own: its origin is vouched for by the answer of the
    /// same address without parameters (<see cref="HeroesSource"/>, checked by the client before a slice is asked for).
    /// A slice that does name one must name an allowed one.
    /// </summary>
    private static bool SliceSourceOk(string? sourceId, string? site) => (sourceId is null && site is null) || HeroesUsable(sourceId, site);

    /// <summary>
    /// The allowed source the base heroes answer (<c>/api/bg/heroes</c> without parameters) names, verbatim; null when
    /// it names none or one that is not allowed. Heroes slices are read only under it.
    /// </summary>
    internal static string? HeroesSource(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && HeroesUsable(Str(root, "source_id"), Str(root, "site")) ? Str(root, "source_id") : null;

    /// <summary>
    /// The answer of a part as it is cached: lists and comps the app may not show are cut, so their data is not kept on
    /// disk. Null when the bytes are not a JSON object.
    /// </summary>
    internal static byte[]? Strip(BgSitePart part, ReadOnlySpan<byte> json)
    {
        try
        {
            // Utf8JsonReader does not take a byte order mark.
            if (json.StartsWith("﻿"u8))
            {
                json = json[3..];
            }

            if (JsonNode.Parse(json) is not JsonObject answer)
            {
                Diag.Info($"Battlegrounds site {part.Name}: the answer is not an object");
                return null;
            }

            switch (part.Kind)
            {
                case BgSitePartKind.TierLists when answer["lists"] is JsonObject lists:
                    foreach (var name in lists.Select(l => l.Key).ToList())
                    {
                        if (!Usable(Text(lists[name], "source"), Text(lists[name], "upstreamSource")))
                        {
                            Diag.Info($"Battlegrounds site {part.Name}: list \"{Clean(name, 32)}\" of source \"{Clean(Text(lists[name], "source"), 80)}\" is not used");
                            lists.Remove(name);
                        }
                    }

                    break;
                case BgSitePartKind.TrinketList when !Usable(Text(answer, "source"), Text(answer, "upstreamSource")):
                    Diag.Info($"Battlegrounds site {part.Name}: source \"{Clean(Text(answer, "source"), 80)}\" is not used");
                    answer.Remove("tiers");
                    break;
                case BgSitePartKind.Comps when answer["comps"] is JsonArray comps:
                    var listSource = Text(answer, "source");
                    var refused = 0;
                    for (var i = comps.Count - 1; i >= 0; i--)
                    {
                        if (!Usable(Text(comps[i], "source") ?? listSource, url: Text(comps[i], "sourceUrl")))
                        {
                            comps.RemoveAt(i);
                            refused++;
                        }
                    }

                    if (refused > 0)
                    {
                        Diag.Info($"Battlegrounds site {part.Name}: {refused} comps of a source that is not used dropped");
                    }

                    break;
                case BgSitePartKind.Heroes when !HeroesUsable(Text(answer, "source_id"), Text(answer, "site")):
                case BgSitePartKind.HeroSlice when !SliceSourceOk(Text(answer, "source_id"), Text(answer, "site")):
                    Diag.Info($"Battlegrounds site {part.Name}: source \"{Clean(Text(answer, "source_id"), 80)}\" is not used");
                    answer.Remove("view");
                    break;
                case BgSitePartKind.MinionStats when !Allowed(Text(answer["latest_run"], "source")):
                    Diag.Info($"Battlegrounds site {part.Name}: source \"{Clean(Text(answer["latest_run"], "source"), 80)}\" is not used");
                    answer.Remove("minions");
                    break;
            }

            using var stream = new MemoryStream(json.Length);
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                answer.WriteTo(writer);
            }

            return stream.ToArray();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // A repeated property name surfaces as ArgumentException when the node is first walked.
            Diag.Error($"Battlegrounds site {part.Name}: unreadable answer", ex);
            return null;
        }

        static string? Text(JsonNode? node, string name) =>
            node is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>
    /// True when the (stripped) answer holds at least one record the app would show. An answer without any (an empty
    /// object, a maintenance note, the service's own "blocked", a list refused by source) is a failed load: it must not
    /// replace a good cache file.
    /// </summary>
    internal static bool HasData(BgSitePartKind kind, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        switch (kind)
        {
            case BgSitePartKind.TierLists:
                return root.TryGetProperty("lists", out var lists) && lists.ValueKind == JsonValueKind.Object
                       && lists.EnumerateObject().Any(l => ListHasRows(l.Value));
            case BgSitePartKind.TrinketList:
                return ListHasRows(root);
            case BgSitePartKind.Heroes:
                return !Is(root, "ok", JsonValueKind.False) && HeroesUsable(Str(root, "source_id"), Str(root, "site")) && HasHeroRows(root, typed: false);
            case BgSitePartKind.HeroSlice:
                // The slice has no source to check, so what it does say must be exact: ok, and a heroes view.
                return Is(root, "ok", JsonValueKind.True) && SliceSourceOk(Str(root, "source_id"), Str(root, "site")) && HasHeroRows(root, typed: true);
            case BgSitePartKind.MinionStats:
                return root.TryGetProperty("latest_run", out var run) && Allowed(Str(run, "source"))
                       && root.TryGetProperty("minions", out var minions) && minions.ValueKind == JsonValueKind.Array
                       && minions.EnumerateArray().Any(m => CardId(m, "card_id") is not null);
            case BgSitePartKind.Comps:
                var listSource = Str(root, "source");
                return root.TryGetProperty("comps", out var comps) && comps.ValueKind == JsonValueKind.Array
                       && comps.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.Object && Usable(Str(c, "source") ?? listSource, url: Str(c, "sourceUrl")));
            default:
                return false;
        }

        static bool ListHasRows(JsonElement list) =>
            list.ValueKind == JsonValueKind.Object && Usable(Str(list, "source"), Str(list, "upstreamSource"))
            && list.TryGetProperty("tiers", out var tiers) && tiers.ValueKind == JsonValueKind.Object
            && TierLetters.Any(t => tiers.TryGetProperty(t, out var bucket) && bucket.ValueKind == JsonValueKind.Array && bucket.GetArrayLength() > 0);

        static bool Is(JsonElement item, string name, JsonValueKind kind) => item.TryGetProperty(name, out var value) && value.ValueKind == kind;

        static bool HasHeroRows(JsonElement root, bool typed) =>
            root.TryGetProperty("view", out var view) && view.ValueKind == JsonValueKind.Object && !Is(view, "blocked", JsonValueKind.True)
            && (!typed || string.Equals(Str(view, "type"), "bg_heroes", StringComparison.Ordinal))
            && view.TryGetProperty("heroes", out var heroes) && heroes.ValueKind == JsonValueKind.Array
            && heroes.EnumerateArray().Any(h => CardId(h, "id") is not null);
    }
}
