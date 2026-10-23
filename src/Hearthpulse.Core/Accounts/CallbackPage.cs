namespace Hearthpulse.Core.Accounts;

/// <summary>
/// The page the browser shows after the redirect to the app. Self-contained (no fonts, images or scripts from
/// anywhere) and dressed like hearthpulse.net: wood behind a parchment card with gold trim. Russian, like the site.
/// These colours are the site's, not app theme tokens: the page lives in the browser, outside the app's themes.
/// </summary>
internal static class CallbackPage
{
    public static string Success => Render("Вход выполнен", "Можно вернуться в приложение Hearthpulse. Эту вкладку можно закрыть.");

    public static string Failure => Render("Войти не получилось", "Вернитесь в приложение Hearthpulse — там написано, что случилось, и можно попробовать ещё раз.");

    private static string Render(string title, string text) => $$"""
        <!doctype html>
        <html lang="ru">
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{title}} — Hearthpulse</title>
        <style>
        body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;
          background:#2e160b radial-gradient(ellipse at top,#5f371d,#2e160b 70%);font:16px/1.6 "Segoe UI",Arial,sans-serif;color:#30251c}
        main{max-width:30rem;margin:1rem;padding:2rem 2.25rem;background:#ead6a7;
          background:linear-gradient(#f7e8bf,#ead6a7);border:2px solid #d9ab49;border-radius:12px;
          box-shadow:0 0 0 4px #5f371d,0 18px 40px rgba(0,0,0,.45)}
        p.brand{margin:0 0 .5rem;font-size:.8rem;letter-spacing:.12em;color:#735e49}
        h1{margin:0 0 .75rem;font:700 1.6rem/1.25 Georgia,"Times New Roman",serif;color:#8d171d}
        p{margin:0}
        </style>
        <main><p class="brand">HEARTHPULSE</p><h1>{{title}}</h1><p>{{text}}</p></main>
        </html>
        """;
}
