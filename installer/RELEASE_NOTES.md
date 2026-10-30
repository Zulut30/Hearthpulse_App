## Hearthpulse 0.14.0

### Что нового
- **Пошаговый повтор игр.** На странице матча — проигрыватель: таймлайн всей игры (бои Battlegrounds раскрашены по исходу), шаг, событие и ход вперёд и назад, автовоспроизведение ×1/×2/×4, клавиши (Space, ←/→, Ctrl+←/→). Battlegrounds теперь записываются по шагам: каждое действие в таверне и каждый удар в бою, стол Боба, ваш стол, рука и таблица лидеров. Старые повторы открываются как раньше.
- **Таверна и раса у портретов Battlegrounds.** У каждого соперника в таблице лидеров видны уровень таверны и его основная раса с числом существ (после боя с ним). Новая компактная панель «Расы и таверны» включается в настройках оверлея.
- **Оверлей двигается и меняет размер прямо в игре.** Удерживайте Ctrl над панелью: тащите — переместить, тяните за уголок или крутите колесо — размер, Shift+колесо — прозрачность, правая кнопка — сброс. Ctrl+Shift+L — режим раскладки. При первом показе оверлея появится подсказка.
- **Удобнее.** Карточка «С чего начать» до первой игры, все горячие клавиши в одном месте, понятные состояния «загрузка / недоступно / пусто» вместо вечной загрузки, баннер «Оверлей выключен», размер и прозрачность оверлея сразу под главным переключателем, единые заголовки панелей, у панели одно имя везде, в компактном режиме убирается арт, а не текст, видимый фокус клавиатуры, более читаемый текст.
- **Новый вид.** Главное окно, страница колоды, «Мои данные» и страница матча: карточки показателей, кривые, сортируемые таблицы, тепловая карта матчапов, тир-листы как на hearthpulse.net. Выбор темы в обоих приложениях, у каждой темы свой характер.
- **Ещё.** Муллиган на Арене по винрейту карт; всплывающий пул показывает карты этой игры; сокровища Раскопки по уровням; кнопки показа и скрытия панелей на живой панели.

### Быстрее и легче
- Главное окно, спрятанное в трей дольше минуты, отдаёт около 50–60 МБ памяти; пока окно открыто, нет пауз на сборку памяти. В покое — около 230 МБ (было около 310 МБ).
- Шаг повтора Battlegrounds в 3 раза быстрее, Constructed — в 5 раз; значки и панели Battlegrounds обновляются в 10 раз дешевле.
- Длинные списки строят только видимые строки, старт быстрее, Power.log читается с меньшими затратами.
- Установщик меньше: без отладочных символов, языки интерфейса — только русский и английский.

### Исправления
- Battlegrounds: исход и урон боя берутся из самого боя, последний бой записывается, итоговое место сохраняется верно, бой, сданный до удара героя, больше не считается ничьей, урон по призраку выбывшего игрока учитывается.
- Класс игрока не сбивается из-за карты героя другого класса в колоде; сдача в муллигане сохраняется в повторе.
- Повреждённая история или занятый файл настроек больше не роняют приложение.
- Avalonia: страница матча больше не падает на вкладке «Колоды», игра, открытая не из «Истории», не закрывается сама, уведомления показываются корректно.
- WPF при старте сообщает, если Hearthstone не найден; мета обновляется раз в 6 часов в обоих приложениях; обновление превью ставится только поверх превью.

### Прочее
- Hearthpulse — закрытое ПО (All rights reserved). Лицензии сторонних компонентов перечислены в THIRD-PARTY-NOTICES.txt рядом с программой.
- Hearthpulse — сторонняя программа, не связанная с Blizzard Entertainment. Используйте её на свой риск.

---

### What's new
- **Step-by-step replays:** a player on the match page with a whole-game timeline (Battlegrounds fights coloured by result), step / event / turn back and forward, ×1/×2/×4 playback and keys. Battlegrounds games are now recorded action by action — every tavern action and every attack, Bob's offer, your board, hand and the leaderboard. Older replays open as before.
- **Battlegrounds leaderboard badges:** each opponent's tavern tier and main tribe with its minion count (after you fought them). New compact "Tribes and taverns" panel, off by default.
- **Move and resize the overlay in a game:** hold Ctrl over a panel — drag to move, drag its corner or scroll to resize, Shift+scroll for opacity, right-click to reset. Ctrl+Shift+L opens layout mode. A one-time tip shows the first time.
- **Easier to use:** a getting-started card, every shortcut in one place, clear loading / unavailable / empty states, an "Overlay is off" banner, overlay size and opacity right under the master switch, one header style and one name per panel, compact mode drops art instead of shrinking text, visible keyboard focus, more readable text.
- **New look** for the main window, deck page, My Stats and the match page; a theme picker in both apps, each theme with its own character. Arena mulligan from card win rates, this game's cards in the pool popup, Excavate treasures by tier, panel show/hide buttons on the live bar.
- **Lighter:** a main window hidden in the tray for a minute gives back about 50–60 MB; no garbage-collection pauses while it is open; about 230 MB at rest. Battlegrounds replay steps 3× faster, Constructed 5×; Battlegrounds badges and panels 10× cheaper. Smaller installer (no debug symbols, en/ru only).
- **Fixes:** Battlegrounds fight results, damage, the last fight and the final place; a fight conceded before any hero attack is no longer a tie; a hero card of another class no longer changes your class; broken history or a busy settings file no longer stop the app; Avalonia match page Decks tab and games opened outside History; the meta refreshes every 6 hours in both apps.
