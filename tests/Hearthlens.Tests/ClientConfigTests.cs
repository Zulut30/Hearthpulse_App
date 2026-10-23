using Hearthlens.Core.Hearthstone;
using Xunit;

namespace Hearthlens.Tests;

public sealed class ClientConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hearthlens-client-config-" + Guid.NewGuid().ToString("N"));

    public ClientConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string FilePath => Path.Combine(_dir, "client.config");

    [Fact]
    public void MissingFileMeansTheTenMegabyteLimit()
    {
        Assert.Equal(ClientConfig.DefaultLimitKilobytes, ClientConfig.LogSizeLimit(_dir));
        Assert.Null(ClientConfig.LogSizeLimit(null));
    }

    [Fact]
    public void EnsureCreatesTheFileWithoutALimit()
    {
        Assert.True(ClientConfig.Ensure(_dir));

        Assert.Equal(-1, ClientConfig.LogSizeLimit(_dir));
        Assert.Contains("[Log]", File.ReadAllText(FilePath));
        Assert.False(ClientConfig.Ensure(_dir));
    }

    [Fact]
    public void EnsureReplacesTheLimitAndKeepsOtherSettings()
    {
        File.WriteAllText(FilePath, "[Aurora]\r\nEnv.Override=1\r\n[Log]\r\nFileSizeLimit.Int=10000\r\nOther=2\r\n[Gfx]\r\nVSync=0\r\n");

        Assert.True(ClientConfig.Ensure(_dir));

        var text = File.ReadAllText(FilePath);
        Assert.Equal(-1, ClientConfig.LogSizeLimit(_dir));
        Assert.Contains("Env.Override=1\r\n", text);
        Assert.Contains("FileSizeLimit.Int=-1\r\nOther=2\r\n[Gfx]\r\nVSync=0\r\n", text);
        Assert.DoesNotContain("10000", text);
    }

    [Fact]
    public void EnsureAddsTheSettingToAnExistingLogSection()
    {
        File.WriteAllText(FilePath, "[Log]\nOther=2\n[Gfx]\nFileSizeLimit.Int=5\n");

        Assert.True(ClientConfig.Ensure(_dir));

        Assert.Equal("[Log]\nFileSizeLimit.Int=-1\nOther=2\n[Gfx]\nFileSizeLimit.Int=5\n", File.ReadAllText(FilePath));
        Assert.Equal(-1, ClientConfig.LogSizeLimit(_dir));
    }
}
