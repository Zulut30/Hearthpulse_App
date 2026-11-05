using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Tests;

/// <summary>
/// <see cref="GameSession.ApplySettings"/>: what both hosts used to set one by one at start and after a settings change
/// (no game plan comp, the card pool, the Arena helper, BG opponents' MMR, the ladder session break clamped to 1-12 h).
/// </summary>
public sealed class SessionSettingsTests : IDisposable
{
    private readonly string _db = TestDataFolder.Scratch("hp-session-settings") + ".db";
    private readonly HearthstoneMemory _memory = new();
    private readonly GameSession _session;

    public SessionSettingsTests() =>
        _session = new GameSession(new HistoryStore(_db), _memory) { IsHearthstoneRunning = () => false, ReadsMemory = false };

    public void Dispose()
    {
        _memory.Dispose();
        TestDataFolder.TryDelete(_db);
    }

    [Theory]
    [InlineData(true, false, true, 2.0, 2.0)]
    [InlineData(false, true, false, 0.25, 1.0)]
    [InlineData(true, true, true, 30.0, 12.0)]
    public void The_switches_and_the_clamped_ladder_break_reach_the_session(bool pool, bool arena, bool mmr, double hours, double applied)
    {
        _session.ApplySettings(new AppSettings
        {
            ShowCardPool = pool,
            ShowArenaDraft = arena,
            ShowBgOpponentMmr = mmr,
            LadderSessionBreakHours = hours,
        });

        Assert.Equal((pool, arena, mmr, TimeSpan.FromHours(applied)), _session.Steering);
    }

    [Fact]
    public void A_comp_saved_by_an_older_version_is_not_planned_around()
    {
        _session.SetCompMode(CompMode.Auto, null);

        _session.ApplySettings(AppSettings.FromJson("""{"CompMode":"Auto","ChosenCompId":"old-comp"}"""));

        Assert.Equal(CompMode.Manual, _session.Advisor.CompMode);
        Assert.Null(_session.Advisor.PinnedCompId);
    }
}
