# Hearthpulse

A Hearthstone tracker: it follows the player's games from the game client, shows an overlay over the game and keeps
the player's history, replays and statistics. Hearthstone's own vocabulary (Discover, tavern tier, the Coin, MMR) is
in the `hearthstone-context` skill, not here.

## Following a game

**Live game**:
The game being played right now, as opposed to a finished one in the history.
_Avoid_: current match, active game

**Snapshot**:
Everything the app knows about the live game at one moment, limited to what the player can see; the overlay and the
main window show snapshots, never the log itself.
_Avoid_: state, game state, live data

**Abandoned game**:
The part of a game played before the client left it (a disconnect or a crash), kept apart until its reconnect replaces
it.
_Avoid_: dropped game, partial game

**Reconnect**:
The rest of a game after the client came back to it; it takes the abandoned part's place in the history.

**Log import**:
Adding the player's past games to the history from the game logs the client kept on disk.
_Avoid_: backfill (in anything the player reads)

**Hidden information**:
Anything about the live game the player cannot see on screen: the opponent's hand, secrets and deck, deck order,
another player's tavern.
_Avoid_: secret info (a Secret is a card)

## On screen

**Overlay**:
The transparent window over the game client that holds the panels.
_Avoid_: HUD, in-game window

**Panel**:
One movable, resizable block of the overlay with its own name and setting (deck tracker, counters, likely deck, card
pool…).
_Avoid_: widget, box, popup

**Main window**:
The app's own window with its pages: decks, history, statistics, settings.
_Avoid_: desktop app, dashboard

**Counter**:
A small in-game number a panel shows for a card mechanic (spells cast, minions died), counted from what the player saw.
_Avoid_: tracker (that is the whole app or a deck list)

**Card pool**:
The set of cards a Discover or random-card effect can give, shown on the card pool panel.
_Avoid_: Discover pool (in code and docs), pool (alone)

**Likely deck**:
The opponent's most likely meta list, chosen from the cards it has played, with the played copies marked.
_Avoid_: predicted deck, opponent decklist

**Theme preset**:
One of the seven named colour sets of the interface (Tavern, Frost, Fel, Arcane, Ember, Tide, Shadow).
_Avoid_: skin, colour scheme

**Design token**:
A named value of the interface (a colour role, a font size, a spacing step) that views use instead of a literal.
_Avoid_: style constant, magic number

## History and replays

**History**:
The player's finished games on this computer, one row per game, the source of the local statistics.
_Avoid_: match log, game list

**Replay**:
The record of one finished game as the player saw it, turn by turn, kept as one file.
_Avoid_: recording, VOD

**Post-game part**:
What the log revealed about hidden cards by the end of a game, kept with the replay and shown only on the match page.
_Avoid_: reveals, spoilers

**Match page**:
The page of one finished game: header, replay, both decks and analysis; the only place the post-game part is shown.
_Avoid_: match review, game details

**Replay library**:
The History page's tabs of replays: Recent (the history's games), Saved (starred, kept forever) and Imported (files
from elsewhere, never part of the history or statistics).
_Avoid_: replay archive

**Web replay**:
A replay the player chose to upload to hearthpulse.net, opened there by its link.
_Avoid_: shared replay (Share makes a local file copy)

## Accounts and data from outside

**Data folder**:
The one folder that holds the app's settings, history, replays and caches on this computer.
_Avoid_: app data, profile folder

**Site account**:
The player's account on hearthpulse.net, linked to the app by a short code confirmed in the browser.
_Avoid_: login, user (alone), Battle.net account

**Profile**:
The player's statistics page on hearthpulse.net, built from the finished games the app sends.
_Avoid_: My statistics (the page's title only)

**Data API**:
The Hearthpulse service the app asks for community numbers: Constructed meta, archetypes, mulligan data.
_Avoid_: backend, server (alone)

**Stats pool**:
The shared collection of games players chose to contribute, and the totals served from it.
_Avoid_: card pool (that is the Discover set), pool (alone)

**Meta**:
The decks and archetypes most played right now, with their numbers, as the data API reports them.

**Archetype**:
A named family of decks of one class that play the same way, as the meta names it.
_Avoid_: deck type, strategy
