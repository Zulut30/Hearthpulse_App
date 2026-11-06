import type { DeckFormat } from './protocol';

export type Lang = 'ru' | 'en';

/** Twitch passes `locale=ru-RU&language=ru` in the iframe URL; onContext.language may change it later. */
export function langFrom(value: string | null | undefined): Lang {
  return value && value.toLowerCase().startsWith('ru') ? 'ru' : 'en';
}

export function initialLang(): Lang {
  const params = new URLSearchParams(location.search);
  return langFrom(params.get('locale') ?? params.get('language') ?? navigator.language);
}

/** HearthstoneJSON render locale for the card preview. */
export function renderLocale(lang: Lang): string {
  return lang === 'ru' ? 'ruRU' : 'enUS';
}

const ru = {
  title: 'Колода стримера',
  notInGame: 'Стример сейчас не в игре',
  notInGameNote: 'Колода появится, когда начнётся игра.',
  loading: 'Загрузка колоды…',
  unnamedDeck: 'Колода без названия',
  cardsLeft: (n: number) => `Осталось в колоде: ${n}`,
  collapse: 'Свернуть колоду',
  expand: 'Показать колоду',
  copiesLeft: (left: number, total: number) => `осталось ${left} из ${total}`,
  format: { standard: 'Стандарт', wild: 'Вольный', twist: 'Твист', classic: 'Классика', casual: 'Обычный', other: '' } as Record<DeckFormat, string>,
  configTitle: 'Как подключить Hearthpulse',
  configIntro: 'Расширение показывает зрителям оставшиеся карты вашей колоды в играх с собранной колодой (Стандарт, Вольный и др.). Руку, порядок колоды и карты соперника оно не показывает.',
  configSteps: [
    'Установите приложение Hearthpulse на компьютер, где идёт игра (hearthpulse.net).',
    'Войдите в аккаунт Hearthpulse в приложении.',
    'На сайте hearthpulse.net откройте Аккаунт → Twitch и привяжите свой канал.',
    'В приложении включите Настройки → Стрим → «Twitch-расширение». Если в OBS включена задержка трансляции, укажите её там же.',
    'Активируйте расширение на канале как оверлей или панель. Колода появится у зрителей, когда начнётся игра.',
  ],
  configNote: 'Зрителям ничего устанавливать не нужно. Карты и их изображения — HearthstoneJSON.',
  disclaimer: 'Hearthpulse не связан с Blizzard Entertainment и Twitch. Арт карт: HearthstoneJSON, © Blizzard',
};

const en: typeof ru = {
  title: "Streamer's deck",
  notInGame: 'The streamer is not in a game right now',
  notInGameNote: 'The deck shows up when a game starts.',
  loading: 'Loading the deck…',
  unnamedDeck: 'Unnamed deck',
  cardsLeft: (n: number) => `Cards left in deck: ${n}`,
  collapse: 'Collapse the deck',
  expand: 'Show the deck',
  copiesLeft: (left: number, total: number) => `${left} of ${total} left`,
  format: { standard: 'Standard', wild: 'Wild', twist: 'Twist', classic: 'Classic', casual: 'Casual', other: '' },
  configTitle: 'How to connect Hearthpulse',
  configIntro: "The extension shows viewers the cards left in your deck in Constructed games (Standard, Wild and others). It never shows your hand, the deck's order or the opponent's cards.",
  configSteps: [
    'Install the Hearthpulse app on the computer you play on (hearthpulse.net).',
    'Sign in to your Hearthpulse account in the app.',
    'On hearthpulse.net open Account → Twitch and link your channel.',
    'In the app turn on Settings → Stream → "Twitch extension". If your OBS stream has a delay, set it there too.',
    'Activate the extension on your channel as an overlay or a panel. Viewers see the deck once a game starts.',
  ],
  configNote: 'Viewers do not need to install anything. Card data and images: HearthstoneJSON.',
  disclaimer: 'Hearthpulse is not affiliated with Blizzard Entertainment or Twitch. Card art: HearthstoneJSON, © Blizzard',
};

export type Strings = typeof ru;

export function strings(lang: Lang): Strings {
  return lang === 'ru' ? ru : en;
}
