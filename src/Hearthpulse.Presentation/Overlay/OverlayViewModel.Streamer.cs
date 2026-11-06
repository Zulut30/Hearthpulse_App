using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// Streamer mode and the OBS capture windows (docs/streaming.md). The windows show this model's panels, so they can never
/// show more than the overlay; streamer mode only takes away (opponent names, BattleTags and ratings), and it does so here,
/// where the texts are built, for the overlay and the windows alike.
/// <para>
/// Contract for the Battlegrounds opponents' MMR (TASK-2026-10-29-bg-lobby-panel): a player's name and MMR on a lobby row,
/// a leaderboard badge or the hover popup are built only while <see cref="StreamerMode"/> is off; with it on the hero
/// stays and the MMR column folds away (no "—"). Reading the MMR is not affected (docs/bg-opponent-mmr.md).
/// </para>
/// </summary>
public sealed partial class OverlayViewModel
{
    private bool _streamerMode;
    private StreamWindowState _deckWindowState;
    private StreamWindowState _lobbyWindowState;
    private StreamWindowState _oddsWindowState;

    /// <summary><see cref="AppSettings.StreamerMode"/> as of the last update.</summary>
    public bool StreamerMode { get => _streamerMode; private set => Set(ref _streamerMode, value); }

    /// <summary>The "Hearthpulse — Deck" window: the deck in any game but Battlegrounds once it is known.</summary>
    public StreamWindowState DeckWindowState { get => _deckWindowState; private set => Set(ref _deckWindowState, value); }

    /// <summary>The "Hearthpulse — Lobby" window: the lobby list once the hero is picked and opponents are seen.</summary>
    public StreamWindowState LobbyWindowState { get => _lobbyWindowState; private set => Set(ref _lobbyWindowState, value); }

    /// <summary>The "Hearthpulse — Combat odds" window: the odds while a fight plays out.</summary>
    public StreamWindowState OddsWindowState { get => _oddsWindowState; private set => Set(ref _oddsWindowState, value); }

    /// <summary>The state of the window with this id ("deck", "lobby", "odds").</summary>
    public StreamWindowState StreamState(string id) => id switch
    {
        "deck" => DeckWindowState,
        "lobby" => LobbyWindowState,
        "session" => SessionWindowState,
        _ => OddsWindowState,
    };

    /// <summary>The property that changes with <see cref="StreamState"/> of this id.</summary>
    public static string StreamStateProperty(string id) => id switch
    {
        "deck" => nameof(DeckWindowState),
        "lobby" => nameof(LobbyWindowState),
        "session" => nameof(SessionWindowState),
        _ => nameof(OddsWindowState),
    };

    /// <summary>
    /// The windows' states follow the game alone, not the overlay's switches: a window works with the overlay off, its
    /// panel hidden or the overlay put away. Only "whether", never "what": the panels' data is the overlay's.
    /// </summary>
    private void UpdateStreamer(LiveSnapshot snapshot, AppSettings settings)
    {
        StreamerMode = settings.StreamerMode;
        var constructed = snapshot.InGame ? snapshot.Constructed : null;
        var bg = snapshot.InGame ? snapshot.Battlegrounds : null;
        DeckWindowState = constructed is null || bg is not null ? StreamWindowState.Idle
            : constructed.DeckKnown ? StreamWindowState.Shown : StreamWindowState.Waiting;
        LobbyWindowState = bg is null ? StreamWindowState.Idle
            : bg is { IsHeroSelection: false, Opponents.Count: > 0 } ? StreamWindowState.Shown : StreamWindowState.Waiting;
        OddsWindowState = bg is null ? StreamWindowState.Idle
            : bg is { InCombat: true, CombatOdds: not null } ? StreamWindowState.Shown : StreamWindowState.Waiting;
        UpdateStreamSession(snapshot);
    }

    /// <summary>
    /// The opponent panel's header. Streamer mode: the class (or "Opponent" before it is known) instead of the name or
    /// BattleTag, and "Opponent" instead of "class · rank", so the line stays and the header keeps its height.
    /// </summary>
    private (string Title, string Subtitle) OpponentHeader(ConstructedView view)
    {
        var className = ClassName(view.OpponentClass);
        if (StreamerMode)
        {
            var opponent = Loc.T("Overlay.Common.Opponent");
            return (className.Length > 0 ? className : opponent, opponent);
        }

        return (string.IsNullOrEmpty(view.OpponentName) ? className : view.OpponentName!,
            string.Join(" · ", new[] { className, view.OpponentRank?.Display }.Where(s => !string.IsNullOrEmpty(s))));
    }
}
