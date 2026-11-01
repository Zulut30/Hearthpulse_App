# Симулятор боя BG: покрытие и точность

Как устроен симулятор и как добавить эффект — `src/Hearthpulse.Core/Battlegrounds/Effects/README.md`. Здесь — что он
покрывает, как это мерить и что осталось.

## Метрика

`tools/Hearthpulse.Bench simcover --logs <папка>` (копия данных в `HEARTHPULSE_DATA`):

- **Реальные бои.** Каждая BG-игра из `Power*.log` (на любой глубине) проигрывается через `BattlegroundsTracker` с
  базой карт enUS — тем же путём, что в приложении. Печатаются: доля боёв без «not simulated», точность исхода
  (`BattlegroundsStats.SimAccuracy`, как на странице BG), попадание урона, Brier по шансам победы/ничьей/поражения
  (0 — идеально, 0,67 — «всегда 1/3») и по каждой причине «not simulated» — карта, сторона, зачарования.
- **Популярность.** Существа из кэша API (`cache/api/bg-minions-all.json`, `popularity` — доля досок с существом):
  какие из combat-relevant без эффекта и вероятность, что доска (и бой — две доски) без них.

История `combat_odds` в `history.db` хранит те же числа по боям, но пересчитать её новым реестром нельзя (доски не
сохраняются), а у владельца в ней 2 BG-игры. Поэтому меряем на логах.

## Замер (01.11.2026, патч 37.0, логи владельца: 2 BG-игры, 22 боя)

| | До | После |
|---|---|---|
| Бои без «not simulated» | 15 / 22 = 68 % | **21 / 22 = 95 %** |
| Исход угадан (`SimAccuracy`) | 22 / 22 | 22 / 22 |
| Урон в диапазоне | 100 % | 100 % |
| Brier | 0,036 | **0,024** |
| Существа по популярности: доска без неучтённых | ~100 % | 99 % (двое «not in table», ниже) |

Причины «до»: все 7 — Тёмные дары (Offensive/Defensive Sacrifice, Fresh Perspective, Charisma). «После»: тринкет
Blood Golem Sticker (1 бой). Выборка мала: число будет точнее, когда владелец сыграет больше BG-игр с сохранёнными логами.

## Что покрыто

- **Существа пула**: все 302 размечены в `CombatRelevance.g.cs`; у всех `Relevant` есть эффект. По тексту enUS
  эвристика ещё помечает Tide Oracle Morgl (0,56 % досок) и Fishbait — их нет в таблице (вне пула патча таблицы).
- **Тёмные дары** (`BG36_MidGameEffect_000*`, 47 вариантов) — таблица `PlayerCardTable`:
  - в бою, есть эффект: Offensive Sacrifice, Defensive Sacrifice, Persisting Horror, Toreth's Blessing, Golemancy;
  - в бою, нет эффекта: Invulnerability («Immune while attacking» — нужна правка движка);
  - выданный хрип/раж только в таверне (не помечают бой): Charisma, Mystic Essence, Fresh Perspective, Demonology,
    Consanguinity;
  - остальные — «Начало боя» (разрешились до захвата досок), ключевые слова, характеристики или таверна.
- **Силы героя** (герои из списка API, по пик-рейту): есть эффект — Stormpike Strength (37 %), Frostwolf Fervor (36 %),
  Sprout It Out! (31 %), Glory of Combat (9 %); нет — Broodmother (14 %), Tentacular (9 %): их сила растёт по ходу игры,
  а величину из лога ещё не сверяли.
- **Тринкеты** (данных о популярности нет — все с боевым текстом): есть эффект — Ceremonial Sword, Lionfish Portrait,
  Faerie Dragon Scale, Feral Talisman (обе версии), Reinforced Shield, Slamma Sticker, Bassgill Portrait, Blingtron's
  Sunglasses, Mechagon Adapter, Alliance Keychain (обе), Deathwhisper Sticker, Deathtouch Apple, Unholy Sanctum (обе),
  Deathly Phylactery, S'Thara Sticker, Bird Feeder (обе), Gilnean Thorned Rose, Staff of the Scourge, Beetle Band
  (обе), Cloud Serpent Horn; нет — Tiger Carving, Belcher Portrait (нужны хуки «союзник получил урон / потерял
  Ядовитость»), Blood Golem Sticker, Scrapsmith Portrait, Blood Amulet (кровавые самоцветы), All-Purpose Kibble,
  Dazzling Dagger, Hammer of Twilight (растущая величина), Battle Horn, Eternal Portrait (BG36), Evil Experiment,
  Soul Fermenter.

Карта из таблицы с `Relevant` без эффекта теперь честно делает бой «not simulated» (раньше силы героя и тринкеты не
считались вовсе).

## Допущения (не сверены с логами)

- Stormpike/Frostwolf: копия одна за бой, с текущими характеристиками и ключевыми словами существа; с местом на старте
  боя копия уже на захваченной доске; до 7-го хода сила молчит (ход берётся из лога).
- Persisting Horror: перерождается с атакой и максимальным здоровьем на момент смерти и ключевыми словами с начала боя.
- «N раз за бой» считается с начала симулируемого боя (срабатывания до захвата досок не учтены).

## Правило «not simulated» для зачарований

Существо с выданным хрипом/Avenge/Rally (метка есть, в карте нет) не помечается, если хотя бы одно его зачарование
имеет эффект или известно как «только таверна». Помечается всегда, если на нём зачарование из таблицы с `Relevant` без
эффекта (`CombatRelevance.IsUnsimulated`).
