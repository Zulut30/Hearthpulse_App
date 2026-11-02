using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Discord;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
#if HEARTHPULSE_WPF
using Hearthpulse.App.Overlay;
#else
using Hearthpulse.UI.Overlay;
#endif

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// Settings → Stream → Discord status (docs/discord.md): the switch, the link's status line, and what Discord shows,
/// built from every live snapshot. Off by default; without a registered app id the switch is disabled.
/// </summary>
public sealed partial class MainViewModel
{
    private DiscordPresence? _discord;
    private SynchronizationContext? _discordContext;
    private readonly DiscordGameClock _discordClock = new();
    private bool _discordShown;

    /// <summary>The presence (tests set one with a fake Discord before the first use).</summary>
    internal DiscordPresence Discord
    {
        get
        {
            if (_discord is null)
            {
                _discordContext = SynchronizationContext.Current;
                _discord = new DiscordPresence();
                _discord.StateChanged += _ => PostDiscordState();
            }

            return _discord;
        }
        set
        {
            _discord = value;
            _discordContext = SynchronizationContext.Current;
            value.StateChanged += _ => PostDiscordState();
        }
    }

    /// <summary>The switch: friends on Discord see the mode, classes, turn or place.</summary>
    public bool DiscordStatus
    {
        get => Settings.DiscordStatus && DiscordAvailable;
        set
        {
            if (value == Settings.DiscordStatus)
            {
                return;
            }

            Settings.DiscordStatus = value;
            ChangedStream();
        }
    }

    /// <summary>This build has a Discord app id: the switch can be turned on.</summary>
    public bool DiscordAvailable => Discord.Configured;

    /// <summary>The link to the Discord app, for the status line's dot.</summary>
    public bool DiscordConnected => DiscordStatus && Discord.State == DiscordState.Connected;

    /// <summary>The status line under the switch; empty while it is off.</summary>
    public string DiscordStatusText => !DiscordAvailable
        ? Loc.T("Discord.Status.NotConfigured")
        : !DiscordStatus
            ? string.Empty
            : Loc.T(Discord.State == DiscordState.Connected ? "Discord.Status.Connected" : "Discord.Status.Waiting");

    /// <summary>The status line is there (off with an app id: no line).</summary>
    public bool DiscordStatusShown => DiscordStatusText.Length > 0;

    /// <summary>What Discord shows for <paramref name="s"/> (null clears it): only what the player sees on the live bar,
    /// with no names, BattleTags or ratings, so streamer mode does not change it.</summary>
    internal static DiscordActivity? DiscordActivityFor(LiveSnapshot s, CardDb cards, DateTimeOffset? started)
    {
        if (!s.HearthstoneRunning)
        {
            return null;
        }

        var (details, state, timed) = (s.Battlegrounds, s.Constructed, s.ArenaDraft) switch
        {
            ({ } bg, _, _) => (
                bg.IsDuos ? Loc.T("Main.Mode.Battlegrounds") + " · " + Loc.T("Main.Mode.Duos") : Loc.T("Main.Mode.Battlegrounds"),
                bg.IsHeroSelection ? Loc.T("Main.Live.ChoosingHero") : DiscordBgLine(bg, cards),
                true),
            (null, { } c, _) => (DiscordGameTitle(s.GameType, c.FormatType), DiscordVersusLine(c), true),
            (null, null, { }) => (Loc.T("Main.Mode.Arena"), Loc.T("Discord.Presence.Drafting"), false),
            _ when s.InGame => (Mode(s.GameType) is { Length: > 0 } mode ? mode : Loc.T("Discord.Presence.InGame"), null, true),
            _ => (Loc.T("Discord.Presence.Menu"), null, false),
        };

        return new DiscordActivity(details, state, timed ? started : null)
        {
            LargeImage = DiscordPresence.LogoKey.Length > 0 ? DiscordPresence.LogoKey : null,
            LargeText = Loc.T("Discord.Presence.LogoText"),
        };
    }

    private static string DiscordGameTitle(string? gameType, string? format)
    {
        var mode = Mode(gameType) is { Length: > 0 } m ? m : Loc.T("Discord.Presence.InGame");
        // Arena, Brawl and Practice have no format worth naming.
        var named = gameType is "GT_RANKED" or "GT_CASUAL" or "GT_VS_FRIEND" && format is { Length: > 0 }
            ? Loc.Or("Match.Format." + format, string.Empty)
            : string.Empty;
        return named.Length > 0 ? mode + " · " + named : mode;
    }

    private static string DiscordVersusLine(ConstructedView c)
    {
        var versus = Loc.F("Discord.Presence.Versus", OverlayViewModel.ClassName(c.PlayerClass), OverlayViewModel.ClassName(c.OpponentClass));
        return c.Turn > 0 ? versus + " · " + Loc.F("Discord.Presence.Turn", c.Turn) : versus;
    }

    private static string DiscordBgLine(BattlegroundsView bg, CardDb cards)
    {
        var parts = new List<string>(3);
        if (cards.NameOf(bg.HeroCardId) is { Length: > 0 } hero && !string.Equals(hero, bg.HeroCardId, StringComparison.Ordinal))
        {
            parts.Add(hero);
        }

        if (bg.Turn > 0)
        {
            parts.Add(Loc.F("Discord.Presence.Turn", bg.Turn));
        }

        if (bg.Place > 0)
        {
            parts.Add(Loc.F("Discord.Presence.Place", bg.Place));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Hands the presence what it should show now (from <see cref="Apply"/> and the switch).</summary>
    private void ShowDiscord(LiveSnapshot? s)
    {
        // Off and nothing shown: no work per snapshot. The first call after switching off still clears the status.
        var on = DiscordStatus;
        if (!on && !_discordShown)
        {
            return;
        }

        _discordShown = on;
        var started = _discordClock.Started(s, DateTimeOffset.UtcNow);
        Discord.Show(on, on && s is not null ? DiscordActivityFor(s, _cards, started) : null);
    }

    private void PostDiscordState()
    {
        if (_discordContext is { } context)
        {
            context.Post(_ => RaiseDiscord(), null);
        }
        else
        {
            RaiseDiscord();
        }
    }

    private void RaiseDiscord()
    {
        Raise(nameof(DiscordStatus));
        Raise(nameof(DiscordAvailable));
        Raise(nameof(DiscordConnected));
        Raise(nameof(DiscordStatusText));
        Raise(nameof(DiscordStatusShown));
    }
}

/// <summary>When the current game began, for Discord's "elapsed" timer: the first snapshot of a game, kept until it ends.</summary>
internal sealed class DiscordGameClock
{
    private DateTimeOffset? _started;

    public DateTimeOffset? Started(LiveSnapshot? s, DateTimeOffset now)
    {
        var playing = s is { InGame: true } || s?.Constructed is not null || s?.Battlegrounds is not null;
        _started = playing ? _started ?? now : null;
        return _started;
    }
}
