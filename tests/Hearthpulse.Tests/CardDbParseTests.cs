using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Cards;
using Xunit.Abstractions;

namespace Hearthpulse.Tests;

/// <summary>
/// The streaming card parser must read exactly what the former <see cref="JsonDocument"/> parser read. That parser is
/// kept here as the reference and both run over the same inputs.
/// </summary>
public sealed class CardDbParseTests(ITestOutputHelper output)
{
    [Fact]
    public void Battlegrounds_fixture_cards_match_the_reference_parser()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "BattlegroundsGame", "cards.json.gz");
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var bytes = new MemoryStream();
        gz.CopyTo(bytes);

        AssertSame(bytes.ToArray(), expectedAtLeast: 40);
    }

    [Fact]
    public void Odd_values_read_as_empty_like_the_reference_parser()
    {
        const string json = """
            [
              {"id": "A1", "dbfId": 1, "name": "Plain", "text": "<b>Hi</b>", "cost": 3, "attack": 2, "health": 4, "armor": 0,
               "type": "MINION", "rarity": "RARE", "cardClass": "MAGE", "set": "CORE", "race": "BEAST",
               "mechanics": ["BATTLECRY", "TAUNT"], "collectible": true},
              {"id": "A2", "races": ["DRAGON", "MECHANICAL"], "race": "BEAST", "runeCost": {"blood": 1, "frost": 0, "unholy": 2}},
              {"id": "A3", "cost": "3", "attack": 2.5, "health": 99999999999, "name": 7, "text": null, "collectible": "yes",
               "mechanics": ["X", 1, null, {"a": [1]}, ["nested"], "Y"], "races": [], "race": "MURLOC",
               "runeCost": {"blood": "1", "frost": 2.5, "unholy": 1}, "extra": {"deep": [{"x": 1}]}, "techLevel": 3,
               "isBattlegroundsPoolMinion": true, "isBattlegroundsPoolSpell": false, "battlegroundsHero": 1,
               "isBattlegroundsDuosExclusive": true, "battlegroundsPremiumDbfId": 5, "battlegroundsNormalDbfId": 6,
               "battlegroundsSkinParentId": 7},
              {"dbfId": 9, "name": "no id"},
              {"id": "", "name": "empty id"},
              {"id": 12},
              "not a card",
              [1, 2],
              {"id": "A4", "name": "dup", "name": "last wins", "mechanics": "TAUNT", "runeCost": [1], "races": {"a": 1}},
              {"id": "A5", "name": "escaped property", "set": "CORE"}
            ]
            """;

        AssertSame(Encoding.UTF8.GetBytes(json), expectedAtLeast: 5);
        var cards = CardDb.ParseList(Encoding.UTF8.GetBytes(json));
        Assert.Equal(["DRAGON", "MECHANICAL"], cards.Single(c => c.Id == "A2").Races);
        Assert.Equal(["BLOOD", "UNHOLY"], cards.Single(c => c.Id == "A2").Runes);
        Assert.Equal(["MURLOC"], cards.Single(c => c.Id == "A3").Races);
        Assert.Equal("escaped property", cards.Single(c => c.Id == "A5").Name);
    }

    [Fact]
    public void A_byte_order_mark_is_allowed()
    {
        var json = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("""[{"id": "A1", "name": "With BOM"}]""")).ToArray();

        var card = Assert.Single(CardDb.Parse("enUS", new MemoryStream(json)).Get("A1") is { } c ? [c] : Array.Empty<Card>());
        Assert.Equal("With BOM", card.Name);
    }

    [Theory]
    [InlineData("""{"id": "A1"}""")]
    [InlineData("""[{"id": "A1"}""")]
    [InlineData("""[{"id": "A1", "name": "cut""")]
    [InlineData("""[{"id": "A1"}] trailing""")]
    [InlineData("")]
    public void A_body_that_is_not_a_card_array_throws_JsonException(string json)
    {
        Assert.ThrowsAny<JsonException>(() => CardDb.ParseList(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Repeated_short_values_share_one_string()
    {
        var cards = CardDb.ParseList("""[{"id": "A1", "type": "MINION", "mechanics": ["TAUNT"]}, {"id": "A2", "type": "MINION", "mechanics": ["TAUNT"]}]"""u8);

        Assert.Same(cards[0].Type, cards[1].Type);
        Assert.Same(cards[0].Mechanics[0], cards[1].Mechanics[0]);
    }

    /// <summary>
    /// The card data of a real install (read only), when this machine has one: tens of thousands of cards per locale.
    /// </summary>
    [Fact]
    public void Cached_card_data_of_this_machine_matches_the_reference_parser()
    {
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hearthpulse", "cache");
        var files = Directory.Exists(cache) ? Directory.GetFiles(cache, "cards.*.json") : [];
        foreach (var file in files)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch (IOException)
            {
                continue;
            }

            var count = AssertSame(bytes, expectedAtLeast: 1);
            output.WriteLine($"{Path.GetFileName(file)}: {count} cards identical");
        }
    }

    [Fact]
    public void A_card_larger_than_the_read_buffer_is_read_whole()
    {
        var text = new string('x', 200_000);
        var json = Encoding.UTF8.GetBytes($$"""[{"id": "A1", "name": "first"}, {"id": "BIG", "text": "{{text}}", "cost": 5}, {"id": "A3"}]""");

        AssertSame(json, expectedAtLeast: 3);
        Assert.Equal(text, CardDb.ParseList(new MemoryStream(json)).Single(c => c.Id == "BIG").Text);
    }

    [Fact]
    public void A_stream_that_hands_out_a_few_bytes_at_a_time_reads_the_same()
    {
        var json = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("""
            [{"id": "A1", "name": "Ünïcödé ✓", "mechanics": ["TAUNT"], "runeCost": {"blood": 2}, "cost": 12345},
             "skip", [1, [2, {"x": 3}]], {"id": "A2", "races": ["BEAST"], "extra": {"a": [1, 2, {"b": "c"}]}}]
            """)).ToArray();

        var expected = Reference(json.AsSpan(3).ToArray()).Select(Describe).ToList();
        Assert.Equal(expected, CardDb.ParseList(new TrickleStream(json)).Select(Describe).ToList());
    }

    private static int AssertSame(byte[] json, int expectedAtLeast)
    {
        var expected = Reference(json).Select(Describe).ToList();
        Assert.True(expected.Count >= expectedAtLeast, $"only {expected.Count} cards");
        foreach (var actual in new[]
                 {
                     CardDb.ParseList(json).Select(Describe).ToList(),
                     CardDb.ParseList(new MemoryStream(json)).Select(Describe).ToList(),
                     CardDb.ParseList(new TrickleStream(json)).Select(Describe).ToList(),
                 })
        {
            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i], actual[i]);
            }
        }

        return expected.Count;
    }

    /// <summary>A stream without a length that returns 1 to 7 bytes per read, so tokens split across reads.</summary>
    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, 1 + (_position % 7)), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string Describe(Card card) => string.Join("|", typeof(Card).GetProperties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p =>
        p.GetValue(card) switch
        {
            IEnumerable<string> list => $"{p.Name}=[{string.Join(",", list)}]",
            var value => $"{p.Name}={value}",
        }));

    /// <summary>
    /// The parser as it was before the streaming reader (main 734ed85). One difference: it threw on an array item that is
    /// not an object; the guard in <c>Str</c> skips it, as the streaming parser does.
    /// </summary>
    private static List<Card> Reference(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var cards = new List<Card>(doc.RootElement.GetArrayLength());
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var id = Str(e, "id");
            if (id.Length == 0)
            {
                continue;
            }

            cards.Add(new Card
            {
                Id = id,
                DbfId = Int(e, "dbfId"),
                Name = Str(e, "name"),
                Text = Str(e, "text"),
                Cost = Int(e, "cost"),
                Attack = Int(e, "attack"),
                Health = Int(e, "health"),
                Armor = Int(e, "armor"),
                Type = Str(e, "type"),
                Rarity = Str(e, "rarity"),
                CardClass = Str(e, "cardClass"),
                Set = Str(e, "set"),
                Races = StrArray(e, "races") is { Count: > 0 } races ? races : Str(e, "race") is { Length: > 0 } race ? [race] : Array.Empty<string>(),
                Mechanics = StrArray(e, "mechanics"),
                Runes = CardDb.RunesOf(e),
                Collectible = Bool(e, "collectible"),
                TechLevel = Int(e, "techLevel"),
                IsBattlegroundsPoolMinion = Bool(e, "isBattlegroundsPoolMinion"),
                IsBattlegroundsPoolSpell = Bool(e, "isBattlegroundsPoolSpell"),
                IsBattlegroundsHero = Bool(e, "battlegroundsHero"),
                IsBattlegroundsDuosExclusive = Bool(e, "isBattlegroundsDuosExclusive"),
                BattlegroundsPremiumDbfId = Int(e, "battlegroundsPremiumDbfId"),
                BattlegroundsNormalDbfId = Int(e, "battlegroundsNormalDbfId"),
                BattlegroundsSkinParentId = Int(e, "battlegroundsSkinParentId"),
            });
        }

        return cards;

        static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        static int Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

        static bool Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        static IReadOnlyList<string> StrArray(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var list = new List<string>(v.GetArrayLength());
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                {
                    list.Add(s);
                }
            }

            return list;
        }
    }
}
