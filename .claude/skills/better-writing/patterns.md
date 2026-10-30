# Copy patterns

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-writing/patterns.md` at
> `592aedc`, MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes:
> templates in Russian and English; ICU and `Intl` replaced by `Loc.F`, `Loc.P` and `Loc.DateCulture`.

Templates for destructive flows and status text, and recipes for plurals, placeholders and formatting.

## Destructive flows

| Situation | ru | en |
| --- | --- | --- |
| Reversible, frequent | «Колода «{0}» объединена с этой» + «Отменить» | "Merged “{0}” into this deck" + "Undo" |
| Irreversible, one item | Заголовок «Удалить игру?», текст «Игра пропадёт из истории и статистики.», кнопки «Удалить игру», «Отмена» | Title "Delete this game?", body "It leaves your history and stats.", buttons "Delete game", "Cancel" |
| Irreversible, many items | «Удалить 12 игр?» / «Это нельзя отменить.» / «Удалить 12 игр», «Отмена» | "Delete 12 games?" / "You can't undo this." / "Delete 12 games", "Cancel" |
| Resets many settings | «Сбросить расположение всех панелей?» / «Удалить расположение», «Отмена» | "Reset every panel's position?" / "Reset positions", "Cancel" |

An undo toast carries an action, so how long it stays is `better-accessibility`'s.

## Status copy

| State | Pattern | ru | en |
| --- | --- | --- | --- |
| In progress | Verb + object, ending in `…` | «Загрузка колод…» | "Loading decks…" |
| Done | Object + past participle, no «успешно» | «Настройки сохранены» | "Settings saved" |
| Failed | What did not happen, then the next step | «Не удалось загрузить мету. Проверьте подключение и попробуйте снова.» | "Couldn't load the meta. Check your connection and try again." |
| Unavailable | Why, and what unlocks it, beside the control | «Войдите в аккаунт, чтобы синхронизировать игры» | "Sign in to sync your games" |
| Partial | Counts for both outcomes | «Загружено 9 из 12 игр. Повторить для 3» | "Uploaded 9 of 12 games. Retry 3" |
| Game not running | What is missing and what starts it | «Hearthstone не запущен. Оверлей появится в игре.» | "Hearthstone isn't running. The overlay shows in a game." |

Keep the in-progress and done strings parallel, so «Сохранение настроек…» resolves to «Настройки сохранены».

## Plurals and placeholders

One string per sentence, with the value inside it so the translation can move it:

`en/widgets.json` and `en/deck.json`:

```json
"Widgets.Ladder.Games": "{0} game|{0} games",
"Deck.Versions.Merged": "Merged “{0}” into this deck"
```

`ru/widgets.json` and `ru/deck.json`:

```json
"Widgets.Ladder.Games": "{0} игра|{0} игры|{0} игр",
"Deck.Versions.Merged": "Колода «{0}» объединена с этой"
```

```csharp
Loc.P("Widgets.Ladder.Games", count);   // picks the form: 1, 21 → one; 2-4, 22-24 → few; else many
Loc.F("Deck.Versions.Merged", name);
```

English takes two forms (`one|other`), Russian three (`one|few|many`). Never `count == 1 ? "" : "s"`. A
`StringFormat` with words in XAML cannot take a key; build the text in the view model.

## Formatting

- Dates written out use the interface language's month names through `Loc.F`/`Loc.P` (`Loc.DateCulture`).
- Numbers keep the system's format, like the rest of the UI. Percentages: «54,3 %» comes from the culture, not from a
  hand-built string.
- Russian quotes are «ёлочки», English “curly”; ranges take an en dash, asides an em dash with spaces in Russian; an
  ellipsis is the single `…` character. `better-typography` owns how they render.
- Never join names with `", "` and `" и "` by hand; build the list in one `Loc.F` string per count or use
  `string.Join` with a localized separator key.
