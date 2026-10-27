using System.Net.Http;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Hearthstone;

namespace Hearthpulse.Core;

/// <summary>
/// Start-up work that needs no window: finding the game, its client language and the card database in the locale the
/// settings ask for. Hosts start it before they build their windows (about a second of UI-thread work) and await it
/// afterwards, so the card data is parsed while the windows are built instead of after their first frame.
/// </summary>
public sealed class StartupPrefetch
{
    private readonly HttpClient _http;
    private readonly Task<(string? Locale, CardDb Cards)> _cards;

    private StartupPrefetch(AppSettings settings, HttpClient http)
    {
        _http = http;
        // Read here, on the caller's thread: the settings object belongs to the UI.
        var configured = settings.HearthstonePath;
        var locale = settings.Locale;
        var language = settings.Language;
        Game = Task.Run(() =>
        {
            var install = HearthstoneInstall.FindInstallDirectory(configured);
            return (install, HearthstoneInstall.DetectLocale(install));
        });
        _cards = LoadCardsAsync(locale, language);
    }

    /// <summary>The install folder (null when not found) and the text locale of its client.</summary>
    public Task<(string? InstallDirectory, string ClientLocale)> Game { get; }

    /// <summary>Starts finding the game and loading the cards for <paramref name="settings"/> on the thread pool.</summary>
    public static StartupPrefetch Start(AppSettings settings, HttpClient http) => new(settings, http);

    /// <summary>
    /// The card database in <paramref name="locale"/>: the one loaded ahead when the settings still ask for that locale,
    /// else a fresh load. Never throws, like <see cref="CardDb.LoadAsync"/>.
    /// </summary>
    public async Task<CardDb> CardsAsync(string locale)
    {
        var (loaded, cards) = await _cards;
        return loaded == locale ? cards : await CardDb.LoadAsync(locale, _http);
    }

    private async Task<(string?, CardDb)> LoadCardsAsync(string locale, string language)
    {
        string client;
        try
        {
            (_, client) = await Game.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The host awaits Game and reports the failure; the cards are then loaded by CardsAsync, if at all.
            return (null, CardDb.Empty);
        }

        // The same rule as AppSettings.CardLocale once the host has set ClientLocale.
        var cardLocale = new AppSettings { Locale = locale, Language = language, ClientLocale = client }.CardLocale();
        return (cardLocale, await CardDb.LoadAsync(cardLocale, _http).ConfigureAwait(false));
    }
}
