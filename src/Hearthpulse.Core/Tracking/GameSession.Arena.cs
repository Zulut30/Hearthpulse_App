using Hearthpulse.Core.Arena;

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

    /// <summary>The run's mode as the last offer on this draft screen said.</summary>
    private bool? _draftUnderground;

    private int _picksSeen;

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

        _draftOffer = offer;
        if (offer is not null)
        {
            _draftUnderground = offer.Underground ?? _draftUnderground;
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
        if (_scene != "DRAFT" || !_hearthstoneRunning)
        {
            _draftUnderground = null;
            return null;
        }

        if (!_arenaDraftEnabled)
        {
            return null;
        }

        var offer = _draftOffer is not null && _draftOffer.SameAs(_pickedOffer) ? null : _draftOffer;
        try
        {
            return ArenaDraft.Build(_arenaDraft.State, offer, _arenaMeta, _cards, _draftUnderground);
        }
        catch (Exception ex)
        {
            Diag.Error("Building the Arena draft view failed", ex);
            return null;
        }
    }

    /// <summary>Memory reads for the draft screen are wanted: the screen is open and the helper is on.</summary>
    private bool WantDraftChoices => _scene == "DRAFT" && _arenaDraftEnabled;

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
        var cards = string.Join(", ", offer.CardIds.Select((id, i) => offer.PackageOf(i) is { Count: > 0 } package ? $"{id} (+{string.Join(" ", package)})" : id));
        return offer.Underground is { } underground ? $"{cards}; {(underground ? "Underground" : "normal")} Arena" : cards;
    }
}
