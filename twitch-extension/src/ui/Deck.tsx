import { useState } from 'preact/hooks';
import { renderLocale, type Lang, type Strings } from '../i18n';
import type { Deck, DeckCard } from '../protocol';

// Card art comes from HearthstoneJSON at view time (Image allowlist), never from the zip (task.md, ADR 0009).
// Ids are checked against [A-Za-z0-9_] in protocol.ts before they reach these URLs.
const ART = 'https://art.hearthstonejson.com/v1';
export const tileUrl = (id: string): string => `${ART}/tiles/${id}.png`;
export const renderUrl = (id: string, lang: Lang): string => `${ART}/render/latest/${renderLocale(lang)}/256x/${id}.png`;

export interface Hover {
  card: DeckCard;
  /** The hovered row's box in viewport pixels. */
  rect: DOMRect;
}

/**
 * One deck row, like the app's DeckPanel tile: mana gem, name over the card's tile art (faded into the row from the
 * left), the copies left on a dark pill when the deck has more than one, and the whole row dimmed when none are left.
 */
function DeckRow({ card, t, onHover }: { card: DeckCard; t: Strings; onHover: (hover: Hover | null) => void }) {
  const [artFailed, setArtFailed] = useState(false);
  const empty = card.left === 0;
  const show = (e: Event) => onHover({ card, rect: (e.currentTarget as HTMLElement).getBoundingClientRect() });
  const label = card.total > 1 ? `${card.name}, ${t.copiesLeft(card.left, card.total)}` : card.name;
  return (
    <li
      class={empty ? 'row empty' : 'row'}
      tabIndex={0}
      aria-label={`${card.cost}, ${label}`}
      onMouseEnter={show}
      onFocus={show}
      onMouseLeave={() => onHover(null)}
      onBlur={() => onHover(null)}
    >
      {!artFailed && <img class="tile" src={tileUrl(card.id)} alt="" draggable={false} onError={() => setArtFailed(true)} />}
      <span class="fade" />
      <span class="gem" aria-hidden="true">
        {card.cost}
      </span>
      <span class="name">{card.name}</span>
      {card.total > 1 && (
        <span class="copies" aria-hidden="true">
          {card.left}
        </span>
      )}
    </li>
  );
}

export function DeckHeader({ deck, t }: { deck: Deck; t: Strings }) {
  const format = t.format[deck.format];
  return (
    <header class="deck-header">
      <div class="deck-title">{deck.name ?? t.unnamedDeck}</div>
      <div class="deck-note">
        {format && <span>{format} · </span>}
        {t.cardsLeft(deck.left)}
      </div>
    </header>
  );
}

export function DeckRows({ deck, t, onHover }: { deck: Deck; t: Strings; onHover: (hover: Hover | null) => void }) {
  return (
    <ul class="rows" aria-label={t.title}>
      {deck.cards.map((card) => (
        <DeckRow key={card.id} card={card} t={t} onHover={onHover} />
      ))}
    </ul>
  );
}

/** The card render beside (overlay) or over (panel) the hovered row, kept inside the viewport. */
export function CardPreview({ hover, lang, side }: { hover: Hover | null; lang: Lang; side: 'left' | 'center' }) {
  if (!hover) return null;
  const width = Math.min(220, Math.round(window.innerHeight * 0.5 * (256 / 388)), window.innerWidth - 16);
  const height = Math.round((width * 388) / 256);
  const middle = hover.rect.top + hover.rect.height / 2 - height / 2;
  const top = Math.max(8, Math.min(middle, window.innerHeight - height - 8));
  const left = side === 'left' ? Math.max(8, hover.rect.left - width - 12) : Math.round((window.innerWidth - width) / 2);
  return (
    <img
      class="preview"
      src={renderUrl(hover.card.id, lang)}
      alt=""
      aria-hidden="true"
      style={{ width: `${width}px`, height: `${height}px`, top: `${top}px`, left: `${left}px` }}
    />
  );
}

/** Legal line (LEGAL-HeartLens-twitch-extension.md, L-m2): no affiliation, where the card art comes from. */
export function Disclaimer({ t }: { t: Strings }) {
  return <footer class="disclaimer">{t.disclaimer}</footer>;
}

export function EmptyState({ t, loading }: { t: Strings; loading: boolean }) {
  return (
    <div class="empty-state" role="status">
      <div class="empty-title">{loading ? t.loading : t.notInGame}</div>
      {!loading && <div class="empty-note">{t.notInGameNote}</div>}
    </div>
  );
}
