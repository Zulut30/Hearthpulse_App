using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Updates;

namespace Hearthpulse.Tests;

/// <summary>
/// The release signature: a build with a key installs only files whose line in a SHA256SUMS.txt signed by that key
/// matches GitHub's digest; every other case (no .sig, a changed byte, a missing line, another hash, another key)
/// downloads and installs nothing. A build without a key goes by the digest alone.
///
/// Fixtures/updates/signed-release.json was made with openssl 3.5 and two one-off keys that were then deleted:
/// <code>
/// sha256sum Hearthpulse-* | sed 's/ \*/  /' > SHA256SUMS.txt
/// openssl genpkey -algorithm ed25519 -out k1.pem; openssl genpkey -algorithm ed25519 -out k2.pem
/// openssl pkey -in k1.pem -pubout -out k1.pub; openssl pkey -in k2.pem -pubout -out k2.pub
/// openssl pkeyutl -sign -inkey k1.pem -rawin -in SHA256SUMS.txt -out s1.bin; base64 -w0 s1.bin   # signature
/// openssl pkeyutl -sign -inkey k2.pem -rawin -in SHA256SUMS.txt -out s2.bin; base64 -w0 s2.bin   # signatureByOtherKey
/// openssl pkeyutl -verify -pubin -inkey k1.pub -rawin -in SHA256SUMS.txt -sigfile s1.bin
/// </code>
/// </summary>
[Collection(nameof(UpdateStagingCollection))]
public sealed class UpdateSignatureTests
{
    private const string Version = "99.0.0";
    private const string SetupName = "Hearthpulse-Setup-99.0.0-win-x64.exe";
    private const string DeltaName = "Hearthpulse-Delta-0.14.0-99.0.0-win-x64.zip";

    private static readonly Fixture Signed = Fixture.Load();

    [Fact]
    public void The_key_file_reads_every_ed25519_block_and_ignores_the_rest()
    {
        Assert.Empty(UpdateSignature.ParseKeys("# no key yet\n# another comment\n"));
        var both = UpdateSignature.ParseKeys("# keys\n" + Signed.PublicKey + Signed.OtherPublicKey);
        Assert.Equal(2, both.Count);
        Assert.All(both, k => Assert.Equal(32, k.Length));

        // An RSA-sized block or garbage is never trusted.
        Assert.Empty(UpdateSignature.ParseKeys("-----BEGIN PUBLIC KEY-----\nMIIBIjANBgkq\n-----END PUBLIC KEY-----\n"));
        Assert.Empty(UpdateSignature.ParseKeys("-----BEGIN PUBLIC KEY-----\n!!!\n-----END PUBLIC KEY-----\n"));
    }

    [Fact]
    public void The_shipped_key_file_parses()
    {
        // The repository's file is comments only until the owner adds the key; either way it must parse.
        var path = Path.Combine(FindRepo(), "src", "Hearthpulse.Core", "Updates", "update-signing-key.pub");
        Assert.Equal(UpdateSignature.ParseKeys(File.ReadAllText(path)).Count, UpdateSignature.EmbeddedKeys.Count);
    }

    [Fact]
    public void The_sums_list_every_file_by_name()
    {
        var sums = UpdateSignature.ParseSums(Signed.Sums);
        Assert.Equal(Signed.Sha(SetupName), sums[SetupName]);
        Assert.Equal(Signed.Sha(DeltaName), sums[DeltaName]);
        Assert.Equal(4, sums.Count);
        // A binary-mode sha256sum ("hash *name") and upper-case hex read the same.
        Assert.Equal(new string('a', 64), UpdateSignature.ParseSums(new string('A', 64) + " *ab\r\n")["ab"]);
    }

    [Fact]
    public async Task A_release_signed_by_the_build_s_key_downloads_and_is_logged_as_verified()
    {
        var server = Signed.Server();
        var events = new UpdateTests.Events();
        var options = UpdateTests.Options(UpdateTests.Context(keys: Signed.Keys));
        await using (UpdateTests.Loop(server, events, new UpdateTrigger(), options, automatic: true))
        {
            await events.WaitAsync(e => e.Contains("staged " + Version));
        }

        Assert.Contains($"Update {Version}: release signature verified.", TestDataFolder.ReadLog());
        Assert.Equal(1, server.Count("/dl/" + SetupName));
        var staged = events.Infos.Single();
        Assert.Equal(Encoding.UTF8.GetBytes(Signed.Assets[SetupName]), File.ReadAllBytes(UpdateService.StagedPath(staged)));
    }

    [Fact]
    public async Task A_release_without_a_signature_is_not_downloaded_by_a_build_with_a_key()
    {
        var server = Signed.Server(signature: null);
        var events = new UpdateTests.Events();
        var options = UpdateTests.Options(UpdateTests.Context(keys: Signed.Keys));
        await using (UpdateTests.Loop(server, events, new UpdateTrigger(), options, automatic: true))
        {
            await events.WaitAsync(e => e.Any(x => x.StartsWith("failed", StringComparison.Ordinal)));
        }

        Assert.Equal(Loc.F("Core.Update.Unsigned", Version), events.Texts[0]);
        Assert.DoesNotContain(events.Log, x => x.StartsWith("staged", StringComparison.Ordinal));
        Assert.Equal(0, server.Count("/dl/" + SetupName));
        Assert.Contains($"Update {Version} not installed: the release has no SHA256SUMS.txt.sig.", TestDataFolder.ReadLog());
    }

    public static TheoryData<string> BadCases() => new(["changed sums byte", "changed signature byte", "other key", "not listed", "other hash", "no sums", "not base64"]);

    [Theory]
    [MemberData(nameof(BadCases))]
    public async Task Every_way_the_signature_can_be_wrong_refuses_the_update(string bad)
    {
        var sums = Signed.Sums;
        var signature = Signed.Signature;
        var info = Signed.Info();
        switch (bad)
        {
            case "changed sums byte":
                sums = "f" + sums[1..];
                break;
            case "changed signature byte":
                var bytes = Convert.FromBase64String(signature);
                bytes[10] ^= 1;
                signature = Convert.ToBase64String(bytes);
                break;
            case "other key":
                signature = Signed.SignatureByOtherKey;
                break;
            case "not listed":
                info = info with { AssetName = "Hearthpulse-Setup-99.0.0-win-arm64.exe" };
                break;
            case "other hash":
                info = info with { Sha256 = new string('0', 64) };
                break;
            case "no sums":
                info = info with { ChecksumsUrl = null };
                break;
            case "not base64":
                signature = "not a signature";
                break;
        }

        var server = Signed.Server(sums: sums, signature: signature);
        using var http = new HttpClient(server);
        Assert.Null(await UpdateSignature.VerifyAsync(http, info, Signed.Keys));
        Assert.Equal(0, server.Count("/dl/" + SetupName));
    }

    [Fact]
    public async Task A_delta_whose_line_does_not_match_is_dropped_and_the_setup_kept()
    {
        using var http = new HttpClient(Signed.Server());
        var good = Signed.Info() with { Delta = new DeltaAsset(DeltaName, UpdateServer.Url(DeltaName), Signed.Sha(DeltaName)) };
        Assert.Equal(good, await UpdateSignature.VerifyAsync(http, good, Signed.Keys));

        var wrong = good with { Delta = good.Delta! with { Sha256 = new string('1', 64) } };
        var verified = await UpdateSignature.VerifyAsync(http, wrong, Signed.Keys);
        Assert.NotNull(verified);
        Assert.Null(verified.Delta);
        Assert.Equal(SetupName, verified.AssetName);
    }

    [Fact]
    public async Task A_build_with_two_keys_accepts_a_signature_by_either()
    {
        using var http = new HttpClient(Signed.Server(signature: Signed.SignatureByOtherKey));
        var keys = UpdateSignature.ParseKeys(Signed.PublicKey + Signed.OtherPublicKey);
        Assert.NotNull(await UpdateSignature.VerifyAsync(http, Signed.Info(), keys));
    }

    [Fact]
    public async Task A_build_without_a_key_installs_by_the_digest_and_says_so()
    {
        var server = Signed.Server(signature: null);
        using var http = new HttpClient(server);
        var info = Signed.Info();
        Assert.Same(info, await UpdateSignature.VerifyAsync(http, info, []));
        Assert.Empty(server.Requests);
        Assert.Contains("release signature not checked: this build carries no update key", TestDataFolder.ReadLog());
    }

    [Fact]
    public async Task The_repair_path_needs_the_signature_too()
    {
        // A folder a failed delta left half-updated: the full setup of the newest release, still only when signed.
        var server = Signed.Server(signature: null);
        var events = new UpdateTests.Events();
        var options = UpdateTests.Options(UpdateTests.Context(keys: Signed.Keys));
        Directory.CreateDirectory(options.AppDirectory + ".update-old");
        try
        {
            await using (UpdateTests.Loop(server, events, new UpdateTrigger(), options, automatic: true))
            {
                await events.WaitAsync(e => e.Any(x => x.StartsWith("failed", StringComparison.Ordinal)));
            }

            Assert.Equal(Loc.F("Core.Update.Unsigned", Version), events.Texts[0]);
            Assert.Equal(0, server.Count("/dl/" + SetupName));
        }
        finally
        {
            Directory.Delete(options.AppDirectory + ".update-old");
        }
    }

    [Fact]
    public async Task A_sums_file_past_the_cap_is_refused_even_without_a_length()
    {
        // A chunked reply has no Content-Length; the cap must hold while reading.
        var info = Signed.Info();
        using var http = new HttpClient(new Unsized(300 * 1024));
        Assert.Null(await UpdateSignature.VerifyAsync(http, info, Signed.Keys));
        Assert.Contains($"Update {Version} not installed: SHA256SUMS.txt could not be downloaded.", TestDataFolder.ReadLog());
    }

    /// <summary>Answers every request with <paramref name="size"/> bytes and no length, as a chunked reply does.</summary>
    private sealed class Unsized(int size) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new NoLength(size) });
    }

    private sealed class NoLength(int size) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(new byte[size]).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hearthpulse.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Hearthpulse.slnx");
    }

    private sealed record Fixture(string PublicKey, string OtherPublicKey, string Sums, string Signature, string SignatureByOtherKey, Dictionary<string, string> Assets)
    {
        public IReadOnlyList<byte[]> Keys => UpdateSignature.ParseKeys(PublicKey);

        public static Fixture Load()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "updates", "signed-release.json")));
            var root = doc.RootElement;
            return new Fixture(
                root.GetProperty("publicKey").GetString()!,
                root.GetProperty("otherPublicKey").GetString()!,
                root.GetProperty("sums").GetString()!,
                root.GetProperty("signature").GetString()!,
                root.GetProperty("signatureByOtherKey").GetString()!,
                root.GetProperty("assets").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!));
        }

        public string Sha(string name) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Assets[name])));

        /// <summary>The setup of 99.0.0 as the check gives it: GitHub's digest and the two signature files.</summary>
        public UpdateInfo Info() => new(Version, SetupName, UpdateServer.Url(SetupName), Sha(SetupName))
        {
            ChecksumsUrl = UpdateServer.Url(UpdateSignature.ChecksumsName),
            SignatureUrl = UpdateServer.Url(UpdateSignature.SignatureName),
        };

        /// <summary>The release with its files; <paramref name="signature"/> null leaves the .sig out.</summary>
        public UpdateServer Server(string? sums = null, string? signature = "")
        {
            signature = signature == string.Empty ? Signature : signature;
            sums ??= Sums;
            var files = Assets.Select(a => (a.Key, (byte[]?)Encoding.UTF8.GetBytes(a.Value))).ToList();
            files.Add((UpdateSignature.ChecksumsName, Encoding.UTF8.GetBytes(sums)));
            if (signature is not null)
            {
                files.Add((UpdateSignature.SignatureName, Encoding.UTF8.GetBytes(signature + "\n")));
            }

            var server = new UpdateServer().Latest(UpdateServer.Release(Version, files, "Signed release"));
            foreach (var (name, content) in files)
            {
                server.File(name, content!);
            }

            return server;
        }
    }
}
