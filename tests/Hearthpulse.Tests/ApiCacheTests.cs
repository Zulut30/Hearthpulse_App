using System.Net;
using Hearthpulse.Core.Api;

namespace Hearthpulse.Tests;

/// <summary>
/// A 404 used to overwrite a good cached answer with "nothing" for six hours. A real answer from the last two days now
/// stands in; an older one, or none, is replaced by the remembered "nothing" as before.
/// </summary>
public sealed class ApiCacheTests
{
    private const string Answer = """{"data":[{"hero_card_id":"TB_BaconShop_HERO_01","avg_final_placement":4.1}]}""";

    [Fact]
    public async Task A_404_keeps_a_good_answer_from_the_last_two_days()
    {
        var (folder, file) = CacheWith(Answer, TimeSpan.FromHours(8));
        var server = new NotFound();

        await new HearthpulseData(new HearthpulseApi(new HttpClient(server), "https://api.test/"), folder).LoadHeroesAsync();

        Assert.Equal(1, server.Requests);
        Assert.Equal(Answer, File.ReadAllText(file));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Theory]
    [InlineData(Answer, 72)]
    [InlineData("""{"data":null}""", 8)]
    public async Task A_404_is_remembered_as_nothing_over_an_old_answer_or_no_answer(string cached, int ageHours)
    {
        var (folder, file) = CacheWith(cached, TimeSpan.FromHours(ageHours));
        var server = new NotFound();

        await new HearthpulseData(new HearthpulseApi(new HttpClient(server), "https://api.test/"), folder).LoadHeroesAsync();

        Assert.Equal(1, server.Requests);
        Assert.Equal("""{"data":null}""", File.ReadAllText(file));
        Assert.True(File.GetLastWriteTimeUtc(file) > DateTime.UtcNow - TimeSpan.FromMinutes(5));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    private static (string Folder, string File) CacheWith(string text, TimeSpan age)
    {
        var folder = Path.Combine(TestDataFolder.Path, "api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "bg-heroes-top_50_percent.json");
        File.WriteAllText(file, text);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - age);
        return (folder, file);
    }

    private sealed class NotFound : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"detail":"Not Found"}""") });
        }
    }
}
