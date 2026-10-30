---
name: better-writing
description: Use when writing or reviewing Hearthpulse interface text in Russian and English - labels, buttons, settings toggles, tooltips, errors, empty states, confirmations, toasts - as Loc keys in both string files, in the product's voice, with a way forward for the player. Triggers - "тексты", "формулировка", "перевод", "пустое состояние", "текст ошибки", "copy", "wording", "Loc key".
---

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-writing/SKILL.md` at `592aedc`,
> MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes: i18n libraries
> and ICU replaced by `Loc` and the ru/en string files; Russian conventions taken from the existing strings;
> Hearthstone terms from the game client.

# Writing

This skill writes and reviews interface text: labels, buttons, toggles, tooltips, errors, empty states,
confirmations and status lines, in Russian and English. It matches the voice the existing strings set and flags text
that misleads the player or leaves them without a way forward.

A deliberate voice is not a defect. Raise a departure from plain language only when it creates inconsistency,
ambiguity, a translation problem or a tone the stakes do not support. Rewording that merely suits your taste is not a
finding.

How text renders (case, trimming, quotes and dashes on screen) belongs to `better-typography`. Accessible names and
announcements belong to `better-accessibility`. Room for the longer Russian string belongs to `better-layout`. Game
terms come from `hearthstone-context`.

## Owner's rules

These are the owner's rules and CLAUDE.md; a session brief may narrow them (for example, no render):

- **Everything moves and resizes.** Every overlay element sits in a `MovablePanel` that is a direct child of the
  overlay `Surface`, so the player can drag, resize, fade and reset it (`hearthpulse-overlay-panel`). Main-window
  content follows the window from its minimum size up (WPF `MinWidth="1040"`, Avalonia compact below 900 px).
- **All seven themes.** Tavern, Frost, Fel, Arcane, Ember, Tide, Shadow: colours only from tokens
  (`hearthpulse-design`), and a look at WPF under at least one non-default preset.
- **Render, but never while the game runs.** Renders go through `hearthpulse-visual-check` on a copy of the data
  folder. First check that `Hearthstone.exe` is not running: `Get-Process Hearthstone -ErrorAction SilentlyContinue`
  prints nothing. If it runs, skip the render and mark those checks `Not verified: Hearthstone running`.
- **Both UIs.** The WPF app and the Avalonia preview get the same change with the same Loc keys in ru and en; the
  only allowed gaps are the ones `hearthpulse-design` lists.
- **During a game, only what the player can see.** The exceptions are the match page after the game
  (`docs/match-replay.md`) and BG opponents' MMR (`docs/bg-opponent-mmr.md`). Text counts too: a tooltip or a counter
  label may not name a card the player has not seen.

## Every string is a Loc key in both languages

Text lives in `src/Hearthpulse.Core/Localization/Strings/{en,ru}/<area>.json`, used as `{i:Loc Key}` in XAML and
`Loc.T`, `Loc.F`, `Loc.P` in code (`docs/localization.md`). A new string gets both entries in the same commit, under
a dotted PascalCase key named for its meaning (`Settings.Overlay.Enabled`). Text written as element content, a
hardcoded English `ToolTip` or a `StringFormat` with words in it is a finding.

## Inventory the existing strings first

Before writing or reviewing, read the strings around the change:

1. Search both language files of the area and the neighbouring keys of the view.
2. List the noun used for each object and the verb for each action: «колода» / "deck", «Объединить» / "Merge",
   «Сбросить» / "Reset".
3. Note the case and punctuation the area uses.

New text uses the terms on that list. A synonym is a finding only where the same thing is named two ways.

## One voice, one vocabulary

The product has one voice and the existing strings set it. If it is «Объединить» on the button, it is not «Слить» in
the toast. Tone flexes with the stakes:

| Context | Tone |
| --- | --- |
| Empty states, first launch, success | Warm, can be light |
| Routine actions, settings | Neutral, minimal |
| Errors, destructive confirmations | Calm, plain, no jokes |
| Account, data loss, privacy | Serious, explicit |

## Game terms as the game says them

Mechanics, modes and ranks use the term the Hearthstone client uses in that language: the Russian client's word in
ru, the English client's in en (`hearthstone-context`). Card, hero and deck names come from card data in the card
locale and are never translated by hand. Never invent a translation for a term the game already names.

## Address the player directly

Russian addresses the player as «вы», lowercase, with the plural imperative in instructions: «Проверьте подключение».
English uses "you" sparingly. In errors, avoid "we": «Не удалось загрузить…» / "Couldn't load…". Never mix
perspectives in one flow, such as «Мои колоды» beside «Ваши настройки».

## Plain words over clever ones

Words a tired player gets on the first pass, and nothing that does no work. No idioms or puns that will not survive
the other language. Match the input: «нажмите» / "click" in the main window; the overlay is used with a mouse over the
game.

## Build strings whole

Never assemble a sentence from fragments around a value: word order and case endings change between the languages.
Write one string with placeholders and use `Loc.F`; for a count use `Loc.P` with the plural forms, English
`"one|other"`, Russian `"one|few|many"`. Dates go through `Loc.F`/`Loc.P`, which use `Loc.DateCulture`. Recipes are in
[patterns.md](patterns.md#plurals-and-placeholders).

## Buttons say the action

A button label is the action: Russian takes the infinitive («Сохранить», «Объединить», «Сбросить всё»), English the
imperative ("Save", "Merge", "Reset all"). Never «ОК» / "OK" where a verb fits. An icon-only button's accessible name
follows the same rule; the property is `better-accessibility`'s.

## Links say where they go

A link names its destination: «Открыть hearthpulse.net» / "Open hearthpulse.net", never «здесь» / "here". Two
"Learn more" links on one page each say about what.

## One capitalization policy

Sentence case everywhere, in both languages (`hearthpulse-design`): «Расположение панелей», "Arrange overlay panels".
"Save Changes" beside "Discard changes" reads as sloppiness.

## Toggles describe the ON state

A `Switch` label says what happens when it is on: «Показывать шансы в бою» / "Show combat odds". «Не показывать…» /
"Don't show…" turns the toggle into a double negative. Point straight at a referenced setting with a button that opens
it, not a path to follow.

## Errors say how to fix, next to where it broke

An error is an instruction, placed beside what failed:

| Bad | Good |
| --- | --- |
| «Ошибка» / "Error" | «Нет связи с hearthpulse.net. Проверьте подключение и попробуйте снова.» / "Can't reach hearthpulse.net. Check your connection and try again." |
| «Неверная папка» / "Invalid folder" | «В этой папке нет Hearthstone. Укажите папку, где лежит Hearthstone.exe.» / "Hearthstone isn't in this folder. Pick the folder with Hearthstone.exe." |
| «Упс! Что-то пошло не так» / "Oops! Something went wrong" | «Не удалось сохранить настройки. Попробуйте снова.» / "Couldn't save settings. Try again." |

No blame, no «упс» / "oops", no exclamation marks. When the same error keeps firing, change the interaction so it
cannot happen.

## Undo beats confirmation

Prefer undo when the action can be reversed and players do it often, as the deck merge does: act at once and offer
undo in the result («Колода «…» объединена с этой. Отменить»).

Confirm before acting when the action cannot be reversed or destroys many items: clearing history, resetting every
panel, signing out with unsynced data. The confirmation repeats the consequence:

- The title names the action and the object: «Удалить 12 игр?», never «Вы уверены?».
- The body says what is lost, with counts.
- The buttons are verb plus object, «Удалить игры» and «Отмена», never «Да» / «Нет».

Templates are in [patterns.md](patterns.md#destructive-flows). The destructive treatment's colour is `better-colors`'.

## Empty states point forward

An empty state says what this place is and how it fills, with one next step where there is one. The project's own
examples set the pattern: «Игр пока нет. Сыгранные игры появятся здесь автоматически.» A filtered empty state names
the cause and the way out: «Под эти фильтры архетипов нет. Уменьшите минимум игр или выберите другой класс.» Never
park lasting information in an empty state; it vanishes once data exists.

Status copy for loading, syncing and success is in [patterns.md](patterns.md#status-copy).

## Placeholders show an example

A `Watermark` / `PlaceholderText` shows a realistic example in the accepted format, never an instruction. Whether the
field also needs a visible label is `better-accessibility`'s.

## Before you finish

| Detection pattern | Fix |
| --- | --- |
| A key present in one language file only | Add the other in the same commit |
| Text as element content, a literal `Text="…"`, `ToolTip="…"` or `Content="…"` with words | A Loc key |
| `StringFormat` with words, or `+ " " +` around a value | One string with `{0}` and `Loc.F`/`Loc.P` |
| `count == 1 ? … : …` for a plural | `Loc.P` with the language's forms |
| «Ошибка», «Упс», "Oops", "Something went wrong" or `!` in an error | Say what failed and the next step |
| «Вы уверены?» / "Are you sure" | Name the action and the object |
| «ОК», «Да», «Нет» on a dialog button | Verb plus object |
| «успешно» / "successfully" | Cut it: «Настройки сохранены» |
| «Пожалуйста» / "Please" in a routine instruction | Cut it |
| A toggle label starting «Не…», "Don't", "Disable" or "Hide" | Describe the ON state |
| A Hearthstone term that differs from the client's word | The client's term (`hearthstone-context`) |
| Two words for one object across a flow | The word the area already uses |

## Reporting

**Severity.** Use `better-interface`'s scale. An error that names no way to recover and a destructive action with
neither confirmation nor undo are escalation triggers (`M`; `B` when saved player data is lost). A user-facing string
missing a Loc key in either language is `M`. Text that misleads the player is `M`; broken voice, terminology or case
consistency is `m`; isolated wording polish is `n`.

**Verification.** Read both language files as well as the views. Check every label against the action it invokes,
every error for a stated fix, every term against the inventory and the game's terms. Render a ru and an en data copy
when length or line breaks matter (`hearthpulse-visual-check`, with the game closed). Strings that come from the API
or card data are `Not verified` unless you saw them.

**Format.** The table in `better-interface`'s `review-format.md` without the `Domain` column, grouped under the
principle each finding violates, one row per root cause; `Location` names the key and its line in both files. With
nothing to report, say «Замечаний по текстам нет» and list the checks.
