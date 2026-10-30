# Review output format

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-interface/review-format.md` at
> `592aedc`, MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes:
> Russian report headings, B/M/m/n with ids, both-UI locations, the project's verdict words.

This is the format for a review `better-interface` orchestrates. Write the report in Russian, identifiers in English.
Domain skills reviewing alone use the same table without the `Domain` column.

## Охват

State the surface, the files of both UIs, the states covered, the project documents found in recon and any boundary.
Then show coverage:

| Domain | Evidence inspected | Result |
| --- | --- | --- |
| Accessibility | Views, styles, states, tests or PNGs | Findings count or `Clear` |

Include every domain listed under **Use domain skills as the sources of truth**. `Clear` means inspected with no
actionable finding; `Not reviewed` must say why.

## Находки

One table, ordered by severity, then by reach. Ids continue the project's numbering: `B1`, `M1`, `m1`, `n1`.

| Id | Domain | Location | Before | After | Why |
| --- | --- | --- | --- | --- | --- |
| M1 | Accessibility | `src/Hearthpulse.App/Main/MainWindow.xaml:842`, `src/Hearthpulse.UI/Main/MainView.axaml:790` | `<Button Style="{StaticResource IconButton}" Content="&#xF01B4;" />` | Add `AutomationProperties.Name="{i:Loc <Area>.Delete}"` with the same key in both views | An icon-only control has no accessible name |

- **Id** carries the severity from **Rank by player impact**.
- **Domain** is the owning skill without the `better-` prefix.
- **Location** is `path:line` in each UI, or the PNG and element when the evidence is a render.
- **Before** and **After** show the current code and an actionable replacement in the project's idiom.
- **Why** names the violated principle and the player impact.

With no findings, omit the table and write «Замечаний по интерфейсу нет».

## Проверка

List each check: the exact command or render (UIs, theme, language, data copy) and the result. Separate checks that
passed from checks marked **Not verified**, with the reason (no kept log reaches the moment, Hearthstone running,
Windows-only suite on another OS).

## Вердикт

End with one of two:

- `нельзя сливать до правки B…/M…`: one or more `B` or `M` findings remain.
- `можно сливать`: no `B` or `M` findings remain. `m` and `n` stay in the table as work to do.

The verdict covers only the domains the coverage table shows as inspected. Name every `Not reviewed` domain in the
verdict line.

## Change-scoped reviews

When `interface-review` hands the review back, use its **Review output format**. It adds the scope block, a `Status`
column and the pre-existing section to the sections above.
