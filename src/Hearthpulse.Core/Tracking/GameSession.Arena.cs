using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>Arena draft helper: the draft from Arena.log, the offer from memory, scores from the Hearthpulse API.</summary>
public sealed partial class GameSession
{
    private readonly ArenaDraftTracker _arenaDraft = new();
    private ArenaMeta? _arenaMeta;
    private ArenaDraftOffer? _draftOffer;
    private bool _arenaDraftEnabled;

    /// <summary>The offer that was on screen when the last pick was made: the client keeps it in memory for a moment.</summary>
    private ArenaDraftOffer? _pickedOffer;

    /// <summary>The card of the last pick while no offer was known: the first offer read with it is the stale one.</summary>
    private string? _pickedWithoutOffer;

    /// <summary>The game whose deck was checked against the draft (once per game).</summary>
    private GameState? _arenaCheckedGame;

    /// <summary>
    /// The Decks.log announcement last checked: a game without a new one (Decks.log missing a line) keeps the previous
    /// game's deck, which a redraft since may have changed, so an announcement is used once.
    /// </summary>
    private DeckDefinition? _arenaCheckedDeck;

    private int _picksSeen;

    /// <summary>How many times a game's deck replaced a differing tracked draft; internal for tests.</summary>
    internal int ArenaGameDeckCorrections { get; private set; }

    /// <summary>The draft as the log and the games of the run told it; internal for tests.</summary>
    internal ArenaDraftState ArenaDraftState => _arenaDraft.State;

    /// <summary>ArenaSmith scores and class results; the host passes them after loading.</summary>
    public void SetArenaMeta(ArenaMeta? meta)
    {
        lock (_pipelineGate)
        {
            _arenaMeta = meta;
            _constructed.Arena = meta;
            _dirty = true;
        }
    }

    /// <summary>
    /// The "Arena draft helper" setting: off, the draft screen is not read from memory and the panels stay empty.
    /// Arena.log is still followed, so turning it on mid-draft knows the deck.
    /// </summary>
    public void SetArenaDraftEnabled(bool enabled)
    {
        // Called on the UI thread: the offer and the pick state are the log thread's, so they change under its lock.
        lock (_pipelineGate)
        {
            _arenaDraftEnabled = enabled;
            if (!enabled)
            {
                SetDraftOffer(null);
            }

            _dirty = true;
        }
    }

    /// <summary>The offer as the memory reader saw it; internal for tests (there is no game memory in them).</summary>
    internal void SetDraftOffer(ArenaDraftOffer? offer)
    {
        if (offer is null ? _draftOffer is null : offer.SameAs(_draftOffer))
        {
            return;
        }

        // Beta (docs/arena-draft.md): what the memory read gives is logged, to check it against the screen on a real draft.
        if (offer is not null || _draftOffer is not null)
        {
            Diag.Info(offer is null ? "Arena draft: no offer read from memory" : "Arena draft: offer read from memory: " + Describe(offer));
        }

        if (offer is null) CaptureUnpickedDraftOffer();
        _draftOffer = offer;
        if (offer is not null)
        {
            if (offer.Underground is { } underground)
            {
                // The offer on screen is the current run's: it names that run's Arena (a new run forgets it).
                _arenaDraft.SetVariant(underground);
            }

            if (_pickedWithoutOffer is { } chosen)
            {
                // The pick was logged before the memory read saw its offer: that offer is the stale one.
                _pickedOffer = offer.CardIds.Contains(chosen, StringComparer.Ordinal) ? offer : null;
                _pickedWithoutOffer = null;
            }
            else if (_pickedOffer is not null && !offer.SameAs(_pickedOffer))
            {
                // The next offer is dealt: an identical one later is a new pick again.
                _pickedOffer = null;
            }
        }

        _dirty = true;
    }

    /// <summary>Test helper: an offer of card ids only.</summary>
    internal void SetDraftOffer(IReadOnlyList<string>? cardIds, bool? underground = null) =>
        SetDraftOffer(cardIds is null ? null : new ArenaDraftOffer(cardIds, underground));

    private void HandleArena(string line)
    {
        if (!_arenaDraft.Feed(line, _cards))
        {
            return;
        }

        if (!_catchingUp)
        {
            // Beta: every Arena.log line the tracker recognized, with the state it led to.
            var state = _arenaDraft.State;
            Diag.Info($"Arena draft: recognized \"{line.Trim()}\" -> hero {state.HeroCardId ?? "none"}, hero power {state.HeroPowerCardId ?? "none"}, "
                      + $"deck {state.DeckId}, {state.Cards.Count} cards, mode {state.Mode ?? "unknown"}");
        }

        if (_arenaDraft.PickCount != _picksSeen)
        {
            _picksSeen = _arenaDraft.PickCount;
            OnArenaPick(_arenaDraft.LastChosen);
        }
    }

    /// <summary>
    /// A pick was made. The offer still in memory is the old one until the client deals the next, but the memory read
    /// may already have the next one when the log line arrives: only an offer holding the chosen card is the old one.
    /// </summary>
    private void OnArenaPick(string? chosen)
    {
        if (chosen is null)
        {
            return;
        }

        var index = _draftOffer is null ? -1 : IndexOf(_draftOffer.CardIds, chosen);
        if (!_catchingUp && _sourceCapture.Enabled)
            _sourceCapture.CaptureDraftChoice(index >= 0 ? _draftOffer : null, _arenaDraft.PickCount, index >= 0 ? index : null, DateOnly.FromDateTime(Clock()));
        if (index >= 0)
        {
            _pickedOffer = _draftOffer;
            _pickedWithoutOffer = null;
            if (!_catchingUp && _arenaDraft.AddPackage(_draftOffer!.PackageOf(index)))
            {
                Diag.Info($"Arena draft: {chosen} came with {string.Join(", ", _draftOffer.PackageOf(index))}");
            }
        }
        else
        {
            // While catching up on old lines no offer is known yet, and the live one is no stale offer.
            _pickedOffer = null;
            _pickedWithoutOffer = _draftOffer is null && !_catchingUp ? chosen : null;
        }
    }

    private void ApplyArenaMemory(MemoryReading reading)
    {
        // Off the draft screen the offer is not read; dropping it keeps an old one from showing for a moment on return.
        SetDraftOffer(reading.Request.WantDraftChoices ? reading.DraftChoices : null);
    }

    /// <summary>The draft panels' view while the Arena draft screen is open, else null.</summary>
    private ArenaDraftView? BuildArenaDraft()
    {
        CheckArenaGameDeck();
        if (_scene != "DRAFT" || !_hearthstoneRunning)
        {
            return null;
        }

        if (!_arenaDraftEnabled)
        {
            return null;
        }

        var offer = _draftOffer is not null && _draftOffer.SameAs(_pickedOffer) ? null : _draftOffer;
        try
        {
            return ArenaDraft.Build(_arenaDraft.State, offer, _arenaMeta, _cards);
        }
        catch (Exception ex)
        {
            Diag.Error("Building the Arena draft view failed", ex);
            return null;
        }
    }

    /// <summary>
    /// A game of the run is under way: the deck Decks.log announced for it is the truth about the draft (a redraft's
    /// removals, copies the client's list missed), and its game type names the run's Arena. Checked once per game, only
    /// while the game runs, so a finished game read again after a restart never undoes a later redraft. The deck goes
    /// only to the run of its deck id (<see cref="ArenaDraftTracker.ApplyGameDeck"/>).
    /// </summary>
    private void CheckArenaGameDeck()
    {
        if (_parser?.Game is not { IsComplete: false } game || ReferenceEquals(game, _arenaCheckedGame)
            || MulliganArena.IsUnderground(game) is not { } underground || _gameDeck is not { } deck || ReferenceEquals(deck, _arenaCheckedDeck))
        {
            return;
        }

        _arenaCheckedGame = game;
        _arenaCheckedDeck = deck;
        var ids = new List<string>(deck.CardCount);
        foreach (var (dbfId, copies) in deck.Cards)
        {
            if (_cards.Get(dbfId) is not { } card)
            {
                // Without the card data the deck cannot be named card by card: only its Arena is taken.
                if (deck.DeckId != 0 && deck.DeckId == _arenaDraft.State.DeckId && _arenaDraft.SetVariant(underground))
                {
                    _dirty = true;
                }

                return;
            }

            ids.AddRange(Enumerable.Repeat(card.Id, copies));
        }

        if (_arenaDraft.ApplyGameDeck(deck.DeckId, ids, underground) is not { } check)
        {
            return;
        }

        _dirty = true;
        if (check.Differed)
        {
            // Beta: one line per corrected game, counts only (no deck id), to compare a live redraft with the log.
            ArenaGameDeckCorrections++;
            Diag.Info($"Arena draft: the game's deck differs from the tracked draft ({check.Tracked} cards tracked, {check.Game} in the game); using the game's deck");
        }
    }

    /// <summary>Memory reads for the draft screen are wanted: the screen is open and the helper is on.</summary>
    private bool WantDraftChoices => _scene == "DRAFT" && (_arenaDraftEnabled || _sourceCapture.Enabled);

    private void CaptureUnpickedDraftOffer()
    {
        if (!_catchingUp && _sourceCapture.Enabled && _draftOffer is { } offer && !offer.SameAs(_pickedOffer))
        {
            _sourceCapture.CaptureDraftChoice(offer, _arenaDraft.PickCount + 1, null, DateOnly.FromDateTime(Clock()));
            _pickedOffer = offer;
        }
    }

    private static int IndexOf(IReadOnlyList<string> ids, string id)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            if (string.Equals(ids[i], id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Describe(ArenaDraftOffer offer)
    {
        var cards = string.Join(", ", offer.CardIds.Select((id, i) => offer.PackageOf(i) is { Count: > 0 } package ? $"{id} (+{string.Join(' ', package)})" : id));
        return offer.Underground is { } underground ? $"{cards}; {(underground ? "Underground" : "normal")} Arena" : cards;
    }
}
