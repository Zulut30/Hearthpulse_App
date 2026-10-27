## Hearthpulse 0.13.0

### Что нового
- **Подсказка при раскопке.** На панели пула у каждого варианта раскопки видно, как часто его берут и какой у него винрейт (Hearthpulse data). Пул показывается только для карт в руке, компактно и с плавным появлением.
- **Помощник драфта Арены.** Оценки карт ArenaSmith для трёх вариантов с выделением лучшего, винрейт классов и доля забегов на 7+ побед при выборе героя и для класса текущего забега.
- **Муллиган.** Полосы над картами стартовой руки, срезы по монете, классу и рангу; результаты против класса соперника берутся из HSGuru, когда других нет.
- **Архетип соперника.** Панель можно закрыть, при наведении показываются его карты.
- **Новый вид оверлея.** Чище и компактнее: меньше лишнего текста, пояснения перенесены во всплывающие подсказки, значки стоимости — настоящий кристалл маны.
- **Расстановка панелей.** Панели прилипают к краям и центру экрана, при изменении размера видно его значение, панели по умолчанию больше не перекрывают друг друга.
- **Главное окно.** Ctrl+1–7 открывают разделы; Esc, Alt+← и боковая кнопка мыши «назад» возвращают со страницы колоды или матча. Короткие подписи в настройках, ползунки масштаба и прозрачности меняются шагом 5 %.

### Быстрее и легче
- Приложение в простое без игры нагружает процессор примерно вдвое меньше; главное окно на старте появляется раньше.
- Оверлей обновляет только изменившиеся строки, а игра ищется без перебора всех процессов каждую секунду.
- Картинки карт обрабатываются с меньшим расходом памяти.

### Исправления
- Колода в оверлее Avalonia больше не встаёт по центру над рукой соперника; счётчики не наезжают на подвал колоды.
- Ползунки в настройках: круглый бегунок, заливка доходит до него.

### Прочее
- Hearthpulse — закрытое ПО (All rights reserved). Лицензии сторонних компонентов перечислены в THIRD-PARTY-NOTICES.txt рядом с программой.
- Hearthpulse — сторонняя программа, не связанная с Blizzard Entertainment. Используйте её на свой риск.

---

### What's new
- **Discover pick hint:** the card pool panel shows how often each Discover option is picked and its win rate (Hearthpulse data). The pool shows for cards in hand only, compact, with a smooth fade-in.
- **Arena draft helper:** ArenaSmith card scores for the three options with the best one marked, class win rates and 7+ share when picking a hero and for the run's class.
- **Mulligan** bars over the opening cards with coin / class / rank slices; results against the opponent's class come from HSGuru when nothing else has them. **Opponent archetype** panel can be closed and previews its cards on hover.
- **Cleaner overlay:** less text, hints moved into tooltips, real mana gems on cost badges. **Layout mode:** panels snap to edges and the centre; default spots no longer overlap.
- **Main window:** Ctrl+1–7 open the pages; Esc, Alt+Left and the mouse Back button leave a deck or match. Short settings labels; scale and opacity sliders step in 5 %.
- **Lighter:** about half the idle CPU without the game, a faster start, leaner card art and overlay updates.
