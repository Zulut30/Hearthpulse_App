using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Share;
#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Share;
#else
namespace Hearthpulse.UI.Main.Share;
#endif

/// <summary>
/// "YouTube timestamps" from History (proposal C2): the games of the last session, today or yesterday (with History's
/// mode filter), timed from the recording's start: by default the first game's start minus the lead-in
/// (<see cref="AppSettings.TimestampLeadInSeconds"/>), or a time the player types.
/// </summary>
public sealed class TimestampsViewModel : Observable
{
    public const string Session = "Session";
    public const string Today = "Today";
    public const string Yesterday = "Yesterday";

    private readonly IReadOnlyList<GameRecord> _games;
    private readonly AppSettings _settings;
    private readonly Func<string, string> _heroName;
    private readonly DateTime _now;
    private string _range = Session;
    private string _startText = string.Empty;
    private string _leadInText;
    private DateTime? _start;
    private string _text = string.Empty;
    private string _error = string.Empty;
    private string _status = string.Empty;

    /// <param name="games">History's games (its mode filter applied), any order.</param>
    public TimestampsViewModel(IReadOnlyList<GameRecord> games, AppSettings settings, Func<string, string> heroName, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(settings);
        _games = games.Where(g => !g.IsAbandoned).OrderBy(g => g.StartedAt).ToList();
        _settings = settings;
        _heroName = heroName;
        _now = now;
        _leadInText = settings.TimestampLeadInSeconds.ToString(CultureInfo.InvariantCulture);
        FromFirst = new RelayCommand(StartFromFirstGame);
        StartFromFirstGame();
    }

    /// <summary>Sets the recording start to the lead-in before the range's first game.</summary>
    public RelayCommand FromFirst { get; }

    public bool RangeSession { get => _range == Session; set => Pick(value, Session); }

    public bool RangeToday { get => _range == Today; set => Pick(value, Today); }

    public bool RangeYesterday { get => _range == Yesterday; set => Pick(value, Yesterday); }

    /// <summary>The recording's start as the player reads and types it: "14:05:30".</summary>
    public string StartText
    {
        get => _startText;
        set
        {
            if (!Set(ref _startText, value))
            {
                return;
            }

            if (TryTime(value, out var time))
            {
                _start = Anchor(time);
                Error = string.Empty;
            }
            else
            {
                Error = Loc.T("Growth.Timestamps.BadTime");
            }

            Build();
        }
    }

    /// <summary>The lead-in in seconds; a valid number is kept for the next time and moves the start with it.</summary>
    public string LeadInText
    {
        get => _leadInText;
        set
        {
            if (!Set(ref _leadInText, value))
            {
                return;
            }

            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds <= YouTubeTimestamps.MaxLeadInSeconds)
            {
                _settings.TimestampLeadInSeconds = seconds;
                _settings.Save();
                StartFromFirstGame();
            }
        }
    }

    /// <summary>The text for the description; empty when no game of the range starts after the recording's start.</summary>
    public string Text { get => _text; private set => Set(ref _text, value); }

    public bool HasText => _text.Length > 0;

    /// <summary>"Type the time as 14:05:30" while the start does not read as a time.</summary>
    public string Error { get => _error; private set => Set(ref _error, value); }

    /// <summary>"Copied" after the copy.</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>The range's games, oldest first.</summary>
    internal IReadOnlyList<GameRecord> RangeGames()
    {
        var today = _now.Date;
        return _range switch
        {
            Today => _games.Where(g => g.StartedAt.Date == today).ToList(),
            Yesterday => _games.Where(g => g.StartedAt.Date == today.AddDays(-1)).ToList(),
            _ => SessionRecap.LastSession(_games, TimeSpan.FromHours(_settings.LadderSessionBreakHours)) is { } s
                ? _games.Where(g => g.StartedAt >= s.Since && g.StartedAt <= s.Until).ToList()
                : [],
        };
    }

    /// <summary>The copy ended.</summary>
    public void Copied(bool ok) => Status = Loc.T(ok ? "Growth.Timestamps.Copied" : "Growth.Share.CopyFailed");

    /// <summary>"14:05:30", "14:05" or "9:5:3".</summary>
    internal static bool TryTime(string? text, out TimeSpan time)
    {
        var parts = (text ?? string.Empty).Trim().Split(':');
        time = default;
        if (parts.Length is < 2 or > 3 || !parts.All(p => p.Length is > 0 and <= 2 && p.All(char.IsAsciiDigit)))
        {
            return false;
        }

        var numbers = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToList();
        var (h, m, s) = (numbers[0], numbers[1], numbers.Count > 2 ? numbers[2] : 0);
        if (h > 23 || m > 59 || s > 59)
        {
            return false;
        }

        time = new TimeSpan(h, m, s);
        return true;
    }

    /// <summary>The time of day on the day of the range's first game, the nearest such moment to it (a stream past midnight).</summary>
    private DateTime Anchor(TimeSpan time)
    {
        var first = RangeGames().Select(g => g.StartedAt).DefaultIfEmpty(_now).First();
        var start = first.Date + time;
        if (start - first > TimeSpan.FromHours(12))
        {
            start = start.AddDays(-1);
        }
        else if (first - start > TimeSpan.FromHours(12))
        {
            start = start.AddDays(1);
        }

        return start;
    }

    private void StartFromFirstGame()
    {
        var start = YouTubeTimestamps.DefaultStart(RangeGames(), _settings.TimestampLeadInSeconds);
        _start = start;
        Error = string.Empty;
        // Through the field, not its setter: the start is already exact (a typed time keeps only the time of day).
        _startText = start.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Raise(nameof(StartText));
        Build();
    }

    private void Pick(bool chosen, string range)
    {
        if (!chosen || _range == range)
        {
            return;
        }

        _range = range;
        Raise(nameof(RangeSession));
        Raise(nameof(RangeToday));
        Raise(nameof(RangeYesterday));
        StartFromFirstGame();
    }

    private void Build()
    {
        Status = string.Empty;
        Text = _start is { } start && Error.Length == 0 ? YouTubeTimestamps.Build(RangeGames(), start, _heroName) : string.Empty;
        Raise(nameof(HasText));
    }
}
