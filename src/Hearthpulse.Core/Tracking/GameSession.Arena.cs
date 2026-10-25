using Hearthpulse.Core.Arena;

namespace Hearthpulse.Core.Tracking;

/// <summary>Arena draft helper: the draft from Arena.log, the offer from memory, scores from the Hearthpulse API.</summary>
public sealed partial class GameSession
{
    private readonly ArenaDraftTracker _arenaDraft = new();
    private ArenaMeta? _arenaMeta;
    private IReadOnlyList<string>? _draftOffer;

    /// <summary>The offer that was on screen when the last pick was made: the client keeps it in memory for a moment.</summary>
    private IReadOnlyList<string>? _pickedOffer;

    private int _picksSeen;

    /// <summary>ArenaSmith scores and class results; the host passes them after loading.</summary>
    public void SetArenaMeta(ArenaMeta? meta)
    {
        _arenaMeta = meta;
        _dirty = true;
    }

    /// <summary>The offer as the memory reader saw it; internal for tests (there is no game memory in them).</summary>
    internal void SetDraftOffer(IReadOnlyList<string>? offer)
    {
        if (offer is null ? _draftOffer is null : _draftOffer is not null && offer.SequenceEqual(_draftOffer))
        {
            return;
        }

        _draftOffer = offer;
        if (offer is not null && _pickedOffer is not null && !offer.SequenceEqual(_pickedOffer))
        {
            // The next offer is dealt: an identical one later is a new pick again.
            _pickedOffer = null;
        }

        _dirty = true;
    }

    private void HandleArena(string line)
    {
        if (!_arenaDraft.Feed(line))
        {
            return;
        }

        if (_arenaDraft.PickCount != _picksSeen)
        {
            // A pick was made: the offer still in memory is the old one until the client deals the next.
            _picksSeen = _arenaDraft.PickCount;
            _pickedOffer = _draftOffer;
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
            return null;
        }

        var offer = _draftOffer is not null && _pickedOffer is not null && _draftOffer.SequenceEqual(_pickedOffer) ? null : _draftOffer;
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
}
