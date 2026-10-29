# WPF UI (lepoco/wpfui): оценка для Hearthpulse

Источник: https://github.com/lepoco/wpfui @ `ffebacd` (MIT, © 2021-2025 Leszek Pomianowski and WPF UI Contributors).
Прочитаны: README, `.github/copilot-instructions.md`, `docs/documentation/themes.md`, `accent.md`, `getting-started.md`,
`docs/architecture/cross-cutting/theming-and-appearance.md`, ADR-004. Ничего не подключалось и не запускалось.
Задача: `TASK-2026-10-29-skills-import` (2026-10-29).

**Вывод: не подключать.** Библиотека решает задачу «WPF-приложение в стиле Windows 11», а у нас обратная задача —
своё лицо в духе меню Hearthstone и одинаковый вид в WPF и Avalonia.

## Что дала бы

- Готовые контролы Fluent: `FluentWindow` + `TitleBar` (snap layouts), `NavigationView`, `Snackbar`, `ContentDialog`,
  `NumberBox`, `AutoSuggestBox`, `Card`, `InfoBar`, иконки Segoe Fluent (`SymbolIcon`).
- Темы Light / Dark / High Contrast, синхронизацию с темой и акцентом Windows (`SystemThemeWatcher`,
  `ApplicationAccentColorManager`), фон Mica / Acrylic для Windows 11.
- Неявные стили для всех стандартных контролов через `ControlsDictionary`.

## Цена

- **Паритет с Avalonia.** Библиотека только для WPF. Каждый её контрол в `Hearthpulse.App` потребует пары в
  `Hearthpulse.UI` (FluentAvalonia или своя) — правило «каждая функция в обоих UI» превращается в два разных набора
  контролов и расходящийся вид. Сейчас различия WPF/Avalonia — короткий список принятых исключений
  (`hearthpulse-design`).
- **Темы Tavern / Frost / Fel / Arcane / Ember / Tide / Shadow.** Наш `ThemeEngine` перекрашивает hex внутри одного
  `Theme.xaml` через `ThemePalette.Map`, кисти заморожены, окна пересоздаются; `ThemeContrastTests` держат WCAG AA.
  WPF UI меняет словари тем на лету и требует `DynamicResource` везде, акцент берёт из Windows. Совместить — значит
  либо отказаться от своих пресетов, либо перекрашивать ещё и ~20 акцентных ресурсов WPF UI и следить, чтобы его
  неявные стили не перебили наши классы.
- **Токены `hearthpulse-design`.** Правило «никакого hex во view, только ключи» сохраняется, но появляется второй
  словарь ключей (`TextFillColorPrimary`, `ControlFillColorDefault`…) с другой семантикой; дизайнеру и ревьюеру
  придётся помнить оба.
- **Оверлей.** Прозрачные topmost-окна с прозрачностью для мыши; `FluentWindow`, Mica и `TitleBar` к ним не
  применимы, а неявные стили зацепят и оверлей. Плюс нагрузка: `DynamicResource` дороже наших замороженных кистей.
- **Вид.** Fluent — нейтральный «системный» стиль; дерево, кожа и золото придётся рисовать поверх него.

Лицензия MIT проблем не создаёт; при подключении нужна строка в `THIRD-PARTY-NOTICES.txt`.

## Что взять как идею в `hearthpulse-design`

1. **Ключи по роли и состоянию.** У WPF UI семейства `TextFillColor{Primary,Secondary,Tertiary,Disabled}`,
   `ControlFillColor{Default,Secondary,Tertiary,Disabled}`, `ControlStrokeColor…`. У нас `Text / Muted / Faint` и
   `Surface…Surface4` — то же по смыслу; стоит явно записать в `tokens.md` состояния hover / pressed / disabled для
   каждой поверхности, чтобы разработчик не подбирал их сам.
2. **Лестница акцента из одного цвета.** Акцент + Light1–3 / Dark1–3 из одного базового цвета — у нас похожая логика в
   `ThemeSpec` (`AccentLift`, `SurfaceLift`). Можно описать в скилле, какие ступени акцента существуют и где каждая
   используется, вместо разрозненных `Accent` / `AccentDeep` / `AccentContainer`.
3. **Высокая контрастность Windows.** WPF UI переключается на HC-словари по `SystemParameters.HighContrast`. У нас
   такого нет (поиск `HighContrast` по `src/` пуст). Кандидат на отдельную задачу: при включённом HC Windows
   основное окно берёт системные цвета; оверлей оставляем как есть.
4. **Из `copilot-instructions.md`:** «не предполагать, что пакет подключён — сначала смотреть csproj» и «код вокруг
   HWND / WndProc / DWM — сначала искать в репозитории, стандартный WPF-подход может не работать». Обе мысли полезны
   для наших оверлейных окон; можно добавить строкой в `hearthpulse-dev` или `hearthpulse-overlay-panel`.
5. **Архитектурные ADR в `docs/architecture/decisions/`** — тот же приём, что даёт установленный `domain-modeling`
   (`docs/adr/`): записывать, почему тема устроена через перекраску hex, а не через `DynamicResource`, чтобы этот
   вопрос не поднимали снова.

Не берём: тон из `copilot-instructions.md` («однословные ответы», обязательные XML-доки с `<example>` — это правила
публичной библиотеки) и совет ставить MCP-серверы Microsoft Learn / Context7.
