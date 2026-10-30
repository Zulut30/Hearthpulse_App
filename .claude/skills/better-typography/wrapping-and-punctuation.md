# Wrapping and punctuation

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-typography/wrapping-and-punctuation.md`
> at `592aedc`, MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes:
> Russian and English character tables; how to write them in JSON and XAML; bidi removed.

Line length, typographic characters in both languages and how to type them.

## Measure

Long text (an explanation in Settings, a tooltip, an empty state) caps at 60-75 characters per line. At the base 13
DIP that is a `MaxWidth` of roughly 480-600; settings content already caps at 780 for the whole column. Tooltips set
their own `MaxWidth` so a long Russian hint wraps instead of crossing the screen.

## Typographic characters

| Instead of | ru | en |
| --- | --- | --- |
| Straight quotes `"…"` | «ёлочки»; „лапки“ inside them | “double”, ‘single’ inside |
| Hyphen for an aside | em dash with spaces: «колода — с этой версии» | em dash with spaces, as the existing strings do: "deck — from this version" |
| Hyphen in a range | en dash without spaces: «3–5 ход» | en dash: "turns 3–5" |
| Three dots | `…` | `…` |
| Space between number and unit | non-breaking: «54 %», «10 с» | non-breaking where a break would split it: "10 s" |
| Apostrophe | ’ | ’ ("don’t") |

The existing strings set the house style: « » in Russian, “ ” in English, spaced em dashes in both. Follow the
area's file when it already decided.

## Writing them

- **JSON string files:** type the character itself (UTF-8). A non-breaking space is ` `.
- **XAML:** the character itself, or an entity: `&#x2014;` em dash, `&#x2013;` en dash, `&#x2026;` ellipsis,
  `&#160;` non-breaking space. User-facing text belongs in a Loc key anyway.
- **Code and identifiers:** deck codes, paths and log lines keep straight characters.

## Wrapping Russian

- Russian words are long; a narrow column breaks them badly. Give a wrapping block at least about 20 characters of
  width at its smallest size.
- Neither framework hyphenates Russian automatically. Do not add soft hyphens to Loc strings; give the text room or
  shorten the string with `better-writing`.
- Keep a short preposition or conjunction with the next word («в игре», «и колода») with a non-breaking space only
  where a break visibly hurts a heading.
