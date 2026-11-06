using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
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

/// <summary>What a share picture can show: the session recap, a deck in one of two layouts, the year in review.</summary>
public enum ShareKind
{
    SessionRecap,
    Deck,
    Year,
}

/// <summary>
/// The "share" window of both UIs (TASK-2026-11-07-growth-app): a picture made of theme tokens, previewed, then copied to
/// the clipboard or saved as PNG the way the turn picture is (R4). No name on the picture unless the player turns the
/// BattleTag on (<see cref="AppSettings.ShareShowBattleTag"/>, kept for the next picture).
/// </summary>
public sealed class ShareViewModel : Observable
{
    private readonly AppSettings _settings;
    private readonly string _battleTag;
    private string _status = string.Empty;
    private bool _statusIsError;
    private bool _cover;

    public ShareViewModel(ShareKind kind, object card, string title, string fileName, AppSettings settings, string? battleTag)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Kind = kind;
        Card = card;
        Title = title;
        FileName = fileName;
        _settings = settings;
        _battleTag = Signature(battleTag);
    }

    public ShareKind Kind { get; }

    /// <summary>The card's model: <see cref="SessionRecapCard"/>, <see cref="DeckPictureCard"/> or <see cref="YearCard"/>.</summary>
    public object Card { get; }

    /// <summary>The card as its type, for typed (compiled) bindings; null for another kind.</summary>
    public SessionRecapCard? Recap => Card as SessionRecapCard;

    public DeckPictureCard? Deck => Card as DeckPictureCard;

    public YearCard? Year => Card as YearCard;

    /// <summary>The window's title.</summary>
    public string Title { get; }

    /// <summary>The file name the save dialog offers, without extension.</summary>
    public string FileName { get; }

    /// <summary>The deck picture's layout: the 16:9 cover, else the vertical list.</summary>
    public bool Cover
    {
        get => _cover;
        set
        {
            if (Set(ref _cover, value))
            {
                Raise(nameof(List));
            }
        }
    }

    /// <summary>The vertical list layout (the radio beside <see cref="Cover"/>).</summary>
    public bool List
    {
        get => !_cover;
        set => Cover = !value;
    }

    /// <summary>Only the deck picture has two layouts.</summary>
    public bool HasLayouts => Kind == ShareKind.Deck;

    /// <summary>The BattleTag switch shows only when there is a BattleTag to show.</summary>
    public bool CanSign => _battleTag.Length > 0;

    public bool ShowBattleTag
    {
        get => _settings.ShareShowBattleTag && CanSign;
        set
        {
            if (value == _settings.ShareShowBattleTag)
            {
                return;
            }

            _settings.ShareShowBattleTag = value;
            _settings.Save();
            Raise(nameof(ShowBattleTag));
            Raise(nameof(BattleTag));
        }
    }

    /// <summary>The name in the picture's footer: the BattleTag without its number, or empty.</summary>
    public string BattleTag => ShowBattleTag ? _battleTag : string.Empty;

    /// <summary>"Copied", "Saved: …" or what went wrong.</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>The status says what went wrong (the negative colour).</summary>
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }

    /// <summary>The copy ended: <paramref name="ok"/> false when the clipboard refused the picture.</summary>
    public void Copied(bool ok) => Say(ok ? Loc.T("Growth.Share.Copied") : Loc.T("Growth.Share.CopyFailed"), !ok);

    /// <summary>The save ended: the path, or null when drawing or writing failed. A cancelled dialog says nothing.</summary>
    public void Saved(string? path) => Say(path is null ? Loc.T("Growth.Share.SaveFailed") : Loc.F("Growth.Share.Saved", path), path is null);

    /// <summary>"Name#1234" → "Name"; blank → empty.</summary>
    internal static string Signature(string? battleTag)
    {
        var name = battleTag?.Trim() ?? string.Empty;
        var hash = name.IndexOf('#', StringComparison.Ordinal);
        return hash >= 0 ? name[..hash].Trim() : name;
    }

    private void Say(string text, bool error)
    {
        StatusIsError = error;
        Status = text;
    }
}
