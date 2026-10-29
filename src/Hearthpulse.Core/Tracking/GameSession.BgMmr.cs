using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// Opponents' ratings in Battlegrounds (docs/bg-opponent-mmr.md): the lobby names the client shows on its
/// leaderboard, read from memory, matched against the region's public Blizzard leaderboard. Off by default. The names
/// and ratings live only in the live view: never in the history, the logs or anything sent (L-M2).
/// </summary>
public sealed partial class GameSession
{
    private BgLeaderboardSource? _bgLeaderboards;
    private bool _bgMmrEnabled;

    /// <summary>The lobby's names by player id, for the game of <see cref="_lobbyNamesGeneration"/>; null until read.</summary>
    private IReadOnlyDictionary<int, string>? _lobbyNames;

    private int _lobbyNamesGeneration = -1;

    /// <summary>The last view had an opponent without a name: the next memory scans read the names again.</summary>
    private bool _lobbyNamesMissing;

    /// <summary>The public leaderboards the opponents are matched against; the host passes it once at start-up.</summary>
    public void SetBgLeaderboards(BgLeaderboardSource? source)
    {
        lock (_pipelineGate)
        {
            if (_bgLeaderboards is { } previous)
            {
                previous.Changed -= MarkDirty;
            }

            _bgLeaderboards = source;
            if (source is not null)
            {
                source.Changed += MarkDirty;
            }

            _dirty = true;
        }
    }

    /// <summary>
    /// The "Opponents' MMR" setting. Off, the lobby names are not read and the view carries no names or ratings; turning
    /// it off forgets the names read so far.
    /// </summary>
    public void SetBgOpponentMmrEnabled(bool enabled)
    {
        lock (_pipelineGate)
        {
            _bgMmrEnabled = enabled;
            if (!enabled)
            {
                _lobbyNames = null;
                _lobbyNamesGeneration = -1;
            }

            _dirty = true;
        }
    }

    /// <summary>
    /// Adds the lobby names a memory scan saw to those of this game: names do not change during a game, and a scan cut
    /// short must not take back a name already shown. Internal for tests (there is no game memory in them).
    /// </summary>
    internal void SetLobbyNames(IReadOnlyDictionary<int, string> names)
    {
        var known = _lobbyNamesGeneration == _gameGeneration ? _lobbyNames : null;
        if (known is not null && names.All(p => known.TryGetValue(p.Key, out var name) && string.Equals(name, p.Value, StringComparison.Ordinal)))
        {
            return;
        }

        var merged = known is null ? new Dictionary<int, string>() : new Dictionary<int, string>(known);
        foreach (var (playerId, name) in names)
        {
            merged[playerId] = name;
        }

        _lobbyNames = merged;
        _lobbyNamesGeneration = _gameGeneration;
        _dirty = true;
    }

    /// <summary>Lobby names are wanted from memory: the setting is on and this game has an opponent without one yet. Internal for tests.</summary>
    internal bool WantLobbyNames =>
        _bgMmrEnabled && _bgLeaderboards is not null && (_lobbyNamesGeneration != _gameGeneration || _lobbyNamesMissing);

    private void ApplyLobbyMemory(MemoryReading reading)
    {
        if (reading.Request.WantLobbyNames && _bgMmrEnabled)
        {
            // A scan that found no names (the map not set up yet, a client that moved it) still counts: the opponents
            // show "unknown" instead of "loading" for good, and the next scans try again while a name is missing.
            SetLobbyNames(reading.LobbyNames ?? new Dictionary<int, string>());
        }
    }

    /// <summary>Each opponent with its name and rating while the setting is on; the view unchanged while it is off.</summary>
    private BattlegroundsView WithOpponentMmr(BattlegroundsView view, GameState game)
    {
        if (!_bgMmrEnabled)
        {
            return view;
        }

        var names = _lobbyNamesGeneration == _gameGeneration ? _lobbyNames : null;
        var region = game.LocalPlayer is { } local ? BgLeaderboardSource.RegionOf(local.AccountHi) : null;
        BgLeaderboard? board = null;
        var loading = false;
        if (region is not null && _bgLeaderboards is { } source)
        {
            board = source.TryGet(BgLeaderboardSource.ModeOf(game.IsDuos), region, out loading);
        }

        var missing = false;
        var opponents = new List<BgOpponent>(view.Opponents.Count);
        foreach (var opponent in view.Opponents)
        {
            var name = names?.GetValueOrDefault(opponent.PlayerId);
            missing |= name is null;
            opponents.Add(opponent with { PlayerName = name, Mmr = MmrOf(name, names is not null, region, board, loading) });
        }

        _lobbyNamesMissing = missing;
        return view with { Opponents = opponents };
    }

    private BgOpponentMmr MmrOf(string? name, bool namesRead, string? region, BgLeaderboard? board, bool loading)
    {
        if (_bgLeaderboards is null || region is null)
        {
            return new BgOpponentMmr(BgMmrStatus.Unknown, null);
        }

        if (!namesRead)
        {
            // The first scan is a few seconds away while the game can be read; without memory there will be none.
            return new BgOpponentMmr(ReadsMemory && _memory.IsAttached ? BgMmrStatus.Loading : BgMmrStatus.Unknown, null);
        }

        if (name is null)
        {
            return new BgOpponentMmr(BgMmrStatus.Unknown, null);
        }

        return board is not null ? board.Match(name)
            : new BgOpponentMmr(loading ? BgMmrStatus.Loading : BgMmrStatus.Unknown, null);
    }
}
