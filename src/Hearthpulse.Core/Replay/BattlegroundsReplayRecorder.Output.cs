using System.Globalization;
using System.Text;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Replay;

/// <summary>The event lines: card and tag lines held until their step is known, then written in order.</summary>
public sealed partial class BattlegroundsReplayRecorder
{
    private bool Pending(string line)
    {
        if (_events.Count + _pending.Count >= MaxEvents)
        {
            Truncated = true;
            return false;
        }

        _pending.Add(line);
        return true;
    }

    /// <summary>
    /// A card line: <c>E</c> (<paramref name="full"/>: every tag and the slot, so it replaces the card's pending lines
    /// before it), <c>X</c> or <c>Y</c>. Later tag lines of the card come after it.
    /// </summary>
    private void PendingCard(int id, string line, bool full)
    {
        if (!_cardPending.TryGetValue(id, out var lines))
        {
            _cardPending[id] = lines = new List<int>();
        }

        if (full)
        {
            foreach (var index in lines)
            {
                _pending[index] = null;
            }

            lines.Clear();
        }

        if (Pending(line))
        {
            _cardLines[id] = _pending.Count - 1;
            lines.Add(_pending.Count - 1);
        }
    }

    /// <summary>A tag line: replaces the card's pending line of the same tag unless a card line came since.</summary>
    private void PendingTag(int id, string tag, string line)
    {
        if (_tagLines.TryGetValue((id, tag), out var index) && index > _cardLines.GetValueOrDefault(id, -1) && _pending[index] is not null)
        {
            _pending[index] = line;
            return;
        }

        if (Pending(line))
        {
            _tagLines[(id, tag)] = _pending.Count - 1;
            if (_cardPending.TryGetValue(id, out var lines))
            {
                lines.Add(_pending.Count - 1);
            }
        }
    }

    private void Flush()
    {
        foreach (var line in _pending)
        {
            if (line is not null)
            {
                Write(line);
            }
        }

        _pending.Clear();
        _tagLines.Clear();
        _cardLines.Clear();
        _cardPending.Clear();
        _died = false;
    }

    private void Write(string line)
    {
        if (_events.Count >= MaxEvents)
        {
            Truncated = true;
            return;
        }

        _events.Add(line);
    }

    /// <summary>A value as one space-free token.</summary>
    private static string Token(string value) => value.Length == 0 ? "-" : value.Replace(' ', '_');
}
