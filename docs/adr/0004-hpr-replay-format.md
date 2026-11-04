# Replays are our own versioned text format (HPR), recorded as the player saw the game

A match replay is a gzip of UTF-8 text lines: a format line (`HPR4` now), a JSON header and space-separated event
lines, one file per game ([match-replay.md](../match-replay.md)). We record it ourselves while the game runs (and from
a log import) instead of keeping the Power.log, because the log holds what the player never saw (the opponent's hand,
deck order, names) and is many times larger. The events are filtered when recorded; what the log revealed by the end
goes into a separate post-game part that only the match page reads ([0005](0005-hidden-information.md)).

## Consequences

- The format line is the version. A reader supports a fixed range (`HPR2`-`HPR5`; `HPR5` only adds the Duos
  partner's side `P` and is written only for files that have it); `HPR1` carried deck card ids and is
  deleted on sight; a newer format is "not supported", never guessed at.
- A format change bumps the number, keeps reading older formats where it can, and lets a log import of the same game
  overwrite an older file.
- A file from outside (Import, Saved, Imported) is untrusted: size limits, a strict grammar per line, hidden
  information cleaned and the header rebuilt from known fields only ([replay-library.md](../replay-library.md)).
- No names, BattleTags or account ids in any format.
