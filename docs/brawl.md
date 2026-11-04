# Tavern Brawl page ("Потасовка")

Main window → Modes → **Brawl**, after Arena (plan stage 4, `TASK-2026-11-02-brawl`), in both UIs; Ctrl+6. It answers
two questions: which class is strong in this week's Tavern Brawl, and how the player's own classes do in it. No game
needs to be running; the overlay does not change.

Code: Core `Stats/BrawlStats.cs` (summing archetypes into classes, the latest Brawl), `Api/HearthpulseData.Brawl.cs`
(the API answer and its cache); shared view model `src/Hearthpulse.Presentation/Main/MainViewModel.Brawl.cs`; views
`Main/Stats/BrawlView.xaml` / `.axaml`; host loading `App.Brawl.cs` / `AppHost.Brawl.cs`.

## Classes (everyone's games)

- Data: our API's `GET v1/hsguru/brawl/meta` (HSGuru, all ranks, this week's Brawl): `total_games` and `items[]`
  {archetype, winrate, popularity, games}, rates in percent. The interface names the source "Hearthpulse data", never
  HSGuru (`docs/localization.md`).
- HSGuru splits the Brawl into archetypes. Some are just a class ("Priest"), some a name with a class ("C'Thun Priest",
  "STD Zoolock"): `ArchetypeNames.ClassOf` gives the class. A class's games are the sum of its archetypes' games, its
  win rate the games-weighted average, its share the sum of their popularity. An archetype whose class can't be told
  (or that comes without a name or a win rate) is left out of the table; its games are counted in the line "Not in the
  table: N games…" so they are not lost silently.
- The card's note says "this week's Brawl"; when the numbers are over 7 days old (the server has not answered since,
  the cache file's time is `BrawlMeta.FetchedAt`), it says when they were fetched instead.
- Rows: class portrait, name, "N games · X % of all games", win rate to one decimal (large samples), best/worst marks
  from 5 games. The Record column stays empty (the community has no W–L, as on the Arena page). Strongest first; the
  column heads sort.
- Source line: "Hearthpulse data · N games".
- Cache: `hsguru-brawl-meta.json` in the API cache, refreshed with the other sets (start-up and every refresh). Offline,
  the last cache is shown; a failed load never clears what is on screen.
- States: loading (no cache yet), unavailable (offline and no cache), no numbers (the answer has no archetypes, or none
  with a class: the line about unclassed games and the source still show).

## Your games in this Tavern Brawl

- "This Brawl" is the scenario of the Tavern Brawl played last (`GT_TAVERNBRAWL` only: friendly and other Brawl modes
  don't pick it): History column `scenario_id` (the game's `ScenarioID` from Power.log, `GameRecord.ScenarioId`) of
  the newest such game, and the unbroken run of games before it with the same scenario. A Brawl that comes back weeks
  later under the same id starts a run of its own; abandoned games don't count. `BrawlStats.LatestBrawl`. A date of
  the week change is not used (Blizzard's changeover moves).
- Games recorded before the column existed have no scenario (NULL) and never count; when only such games exist the
  empty state says so.
- Rows per own class: games, W–L, win rate (whole percent), as in My Stats; most played first. The note above the table
  gives the date of the first game of that scenario, which also shows when the last Brawl played is last week's.
- No "vs everyone" difference: the last Brawl played may be an older one than the community's numbers.
- Brawl = History mode `Brawl` (`GT_TAVERNBRAWL`, `GT_FSG_BRAWL*`); Constructed, Battlegrounds and Arena games never
  count. The table is rebuilt when a game is added to History.

## Hidden information and price

Only results of recorded games (class, win or loss) and the community's weekly totals: nothing from a game in
progress, no opponent cards, deck or name; the opponent's class is not shown. Both cards are free (ADR 0009: third-party
numbers are never limited, the own table is a plain count).

## Not in scope

Brawl decks (`v1/hsguru/brawl/decks`), archetypes inside a class, rank or coin filters, the Brawl's name (the log has
none and the scenario id names nothing), in-game hints on the overlay.

## The week's article (hs-manacost.ru)

The right column starts with "More about the Brawl on hs-manacost.ru" (package B2): the site is the owner's
(Manacost). `Core/Api/ManacostArticles.cs` reads its WordPress REST: posts of category "Потасовка" (id 1036,
`wp-json/wp/v2/posts?categories=1036&per_page=10&_fields=id,date,link,title,excerpt,featured_media`), then the media
of the chosen post (`wp-json/wp/v2/media/{id}`, the `medium_large` size, never the original), downloaded into the API
cache folder (at most 1 MB) so the views show a local file.

- The week's article: the newest post whose title starts with "Потасовка недели" (with a Brawl name, the one sharing
  most words with it; the API gives no name today). Older than 14 days: not this week's, the card hides.
- A second line: the newest deck collection ("колод" in the title), at most 10 days old and not older than the week's
  post.
- Plain text: tags out, HTML entities decoded, the teaser cut to 220 characters; an empty teaser is left out.
- Cache `manacost-brawl.json`, fresh for 6 hours; offline the cached list stands in; 10 s per request; nothing current:
  no card.
- Links and covers only over https on `hs-manacost.ru` (or a subdomain), checked when parsed, when read from the cache
  and again before the browser opens them (`MainViewModel.OpenOnSite`).
- The articles are Russian: an English interface shows "The article is in Russian" on the card.
