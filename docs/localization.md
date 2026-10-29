# Interface languages (English, Russian)

Hearthpulse shows its interface in English or Russian. The player picks the language in Settings → General
(`AppSettings.Language`: `"auto"`, `"en"` or `"ru"`). The language switches **live**: every text in XAML and every
text a view model builds updates without a restart. Card names are the exception: card data
(`cache/cards.<locale>.json`, HearthstoneJSON) is loaded once at start, so they switch after a restart, and Settings
says so under the language choice while the loaded cards are in another language.

How "auto" and the card language are decided (`AppSettings.InterfaceLanguage()`, `AppSettings.CardLocale()`; the hosts
set `AppSettings.ClientLocale` from `HearthstoneInstall.DetectLocale` once they found the game):

| `Locale` (cards, settings.json only) | `Language` | Interface | Card data |
|---|---|---|---|
| a locale, e.g. `"deDE"` | any | as below | that locale |
| `"auto"` | `"ru"` / `"en"` (picked in Settings) | that language | `ruRU` / `enUS` |
| `"auto"` | `"auto"` (default) | Russian when the installed client is ruRU or the system is Russian, else English | the installed client's locale, as before the interface had languages (a German client keeps German cards); the interface language's while no client is found |

So an existing install keeps its cards after the update, and picking a language by hand brings that language's cards
(after a restart). `LocalizationTests` covers the table. Russian archetype names come from Hearthpulse's archetype
dictionary `api.blizzcore.ru` (see `hearthpulse-data-sources`) and are used whenever the interface is Russian.

Dates written out by the interface use the interface language's month names (`Loc.DateCulture`, also inside
`Loc.F`/`Loc.P`); numbers keep the system's format like the rest of the UI.

## Where strings live

```
src/Hearthpulse.Core/Localization/
  Loc.cs                       language, lookup, formatting, plural forms
  Strings/en/<area>.json       English: complete, the fallback for every key
  Strings/ru/<area>.json       Russian: the same keys
```

Each file is a flat JSON object of key → text, embedded into `Hearthpulse.Core` (`Hearthpulse.Core.csproj`, logical
name `Hearthpulse.Loc.<lang>.<file>.json`). JSON, not `.resx`: `Directory.Build.props` sets `SatelliteResourceLanguages=en;ru`,
which would drop the satellite assemblies of other languages. Files are split by area (`settings.json`, `main.json`,
`overlay.json`, `stats.json`, `deck.json`, `core.json`, `host.json`, `sources.json`...) so parallel work rarely
touches the same file. Every file is read; a key must be unique across all files of a language.

Keys are dotted PascalCase, area first: `Settings.Overlay.Enabled`, `Main.Overview.RecentGames`,
`Overlay.Mulligan.KeepRate`. Name a key after its meaning, not its English text, and give a string used in two
places with different meanings two keys.

## Using a string

**XAML (both UIs)**: `{i:Loc Key}` - `i` is the `Infrastructure` namespace prefix every view already declares
(`clr-namespace:Hearthpulse.App.Infrastructure` in WPF, `using:Hearthpulse.UI.Infrastructure` in Avalonia).

```xml
<TextBlock Text="{i:Loc Settings.Title}" />
<CheckBox Content="{i:Loc Settings.Overlay.Enabled}" ToolTip="{i:Loc Settings.Overlay.EnabledTip}" />   <!-- WPF -->
<CheckBox Content="{i:Loc Settings.Overlay.Enabled}" ToolTip.Tip="{i:Loc Settings.Overlay.EnabledTip}" /> <!-- Avalonia -->
<Run Text="{i:Loc Settings.Account.Open}" />
<Setter Property="Text" Value="{i:Loc Settings.Overlay.PickHintsOffCaption}" />                       <!-- WPF style trigger -->
```

It is a one-way binding to `Loc.Instance`'s indexer in both UIs, so it updates when the language changes (`Loc` raises
`Item[]` for WPF and `Item` for Avalonia). Both frameworks hold that subscription weakly, so controls from templates
are not kept alive by the static `Loc.Instance`; `LocBindingTests` checks the Avalonia side on a headless platform. Text written as element content (`<TextBlock>Some text</TextBlock>`) must move into `Text="{i:Loc ...}"`.
A `StringFormat` with words in it (`StringFormat='{}{0} games'`) cannot take a key: build that text in the view
model with `Loc.P`/`Loc.F`.

**C#** (`using Hearthpulse.Core.Localization;`):

| Call | For |
|---|---|
| `Loc.T("Key")` | a plain text |
| `Loc.F("Key", a, b)` | `string.Format` with `{0}`, `{1}` placeholders (current culture, like the rest of the UI) |
| `Loc.P("Key", count, more...)` | a count: the text holds the plural forms separated by `|`, `{0}` is the count |
| `Loc.Or("Key", fallback)` | a name defined in code (a counter's title): the key's text when the language has it, else the code's English |
| `Loc.IsRussian`, `Loc.CardLocale` | the rare branch on the language itself |

Plural forms: English `"one|other"` (`"{0} game|{0} games"`), Russian `"one|few|many"`
(`"{0} игра|{0} игры|{0} игр"`; 1, 21 → one; 2-4, 22-24 → few; 0, 5-20, 11-14 → many).

**Live switching in view models**: texts the view model stores (lists of rows, captions, status lines) must be rebuilt
when the language changes. `Loc.Instance.LanguageChanged` fires on the UI thread after the switch:

- `MainViewModel.Language.cs` (both UIs) re-runs `RefreshAsync`, `RebuildMeta` and raises every property. A view model
  partial that builds its own texts adds its rebuild there.
- The overlay view model rebuilds everything on the next snapshot; the hosts push the last snapshot again after a
  switch.
- Getter-only properties that call `Loc` are re-read by the `Raise(string.Empty)` above.

A text built in Core and kept in a record (a meta's `Source`, an advisor's reason) is in the language of the moment
it was made; it changes with the next load. Prefer building texts in the view model from data when it matters.

## What is not translated

- Card, hero, minion and trinket names (from `cards.<locale>.json`) and archetype names (from the API; Russian
  labels are applied by `ArchetypeNames`).
- Proper names: Hearthpulse, HeartPulse, **Hearthpulse data** (the name of the data source, the same in both
  languages), HearthstoneJSON, hsbg.cards, BattleTag, Windows, GitHub, font names.
- `Diag` log lines, exception messages, ids, URLs, file names, settings keys, test-only render captions.
- Language names on the language switch ("English", "Русский"): each in its own language.

Never name HSReplay or HSGuru in anything the player reads; the source is "Hearthpulse data"
(`HearthpulseData.Attribution`). `LocalizationTests.User_facing_texts_name_the_source_Hearthpulse_data` checks the
string tables.

## Adding a string

1. Pick the area file (or add `Strings/en/<area>.json` and `Strings/ru/<area>.json`; both are picked up by name).
2. Add the key to the English file and the Russian file with the same placeholders and, for counts, 2 and 3 plural
   forms.
3. Use it: `{i:Loc Key}` in both UIs' XAML, `Loc.T/F/P` in code.
4. `dotnet test` - `LocalizationTests` fails on a key missing from either language, a key used in `src` that does not
   exist, placeholders that differ between languages, or wrong plural form counts.
5. Render both languages (`hearthpulse-visual-check`, with `"Language": "ru"` / `"en"` in the data copy's
   `settings.json`): Russian is about a third longer than English, so check for clipped buttons, segment bars and
   overlay panels.

## Russian glossary

Keep terms consistent with the Russian Hearthstone client.

| English | Russian |
|---|---|
| Battlegrounds | Поля сражений |
| Constructed | Конструированный режим (short: Конструированный) |
| Arena | Арена |
| Standard / Wild / Classic / Twist | Стандартный / Вольный / Классический / Твист |
| Ranked / Casual | Рейтинговая / Обычная игра |
| Legend | Легенда |
| deck, deck code | колода, код колоды |
| archetype | архетип |
| matchup | матчап |
| mulligan, keep rate | муллиган, процент оставления |
| win rate | винрейт |
| opponent | соперник |
| turn, going first / on the coin | ход, первым / с монеткой |
| hand, board, graveyard | рука, стол, кладбище |
| Discover | Раскопка |
| Excavate | Добыча |
| Battlecry / Deathrattle | Боевой клич / Предсмертный хрип |
| minion, spell, weapon, hero power | существо, заклинание, оружие, сила героя |
| tavern, tavern tier, Bob | таверна, уровень таверны, Боб |
| trinket, anomaly, tribe, comp | безделушка, аномалия, раса, сборка |
| lobby, leaderboard, placement, top 4 | лобби, таблица лидеров, место, топ-4 |
| rating, MMR, rank | рейтинг, MMR, ранг |
| tier list | тир-лист |
| overlay, panel, counter | оверлей, панель, счётчик |
| card pool | пул карт |
| history, overview | история, главная |
