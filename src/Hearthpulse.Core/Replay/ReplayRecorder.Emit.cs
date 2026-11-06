using System.Globalization;
using System.Text;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Replay;

public sealed partial class ReplayRecorder
{
    private void AfterMove(Entity entity)
    {
        // A card played from hand is announced once it has left the hand (face up, or as a secret's card back).
        if (entity.Zone == "HAND")
        {
            return;
        }

        foreach (var block in _blocks)
        {
            if (block is { Type: "PLAY", Emitted: false } && block.Source?.Id == entity.Id)
            {
                EmitPlay(block);
            }
        }
    }

    private void EmitEntity(Entity entity, Shown shown, string? origin)
    {
        var line = new StringBuilder(64);
        line.Append(CultureInfo.InvariantCulture, $"E {shown.Id} {shown.Side} {shown.Zone} {shown.Position} {shown.Card} ");
        line.Append(shown.Hidden ? "-" : Token(entity.CardType));
        if (!shown.Hidden)
        {
            foreach (var (tag, value) in entity.Tags.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                if (CardTags.Contains(tag) && value.Length > 0 && value != "0")
                {
                    line.Append(' ').Append(tag).Append('=').Append(Token(value));
                }
            }
        }
        else if (origin is not null)
        {
            line.Append(" ORIGIN=").Append(origin);
        }

        Emit(line.ToString());
    }

    private void Remove(Shown? shown, int entityId)
    {
        if (shown is null)
        {
            return;
        }

        _shown.Remove(entityId);
        _left[entityId] = (shown.Zone, shown.Side);
        Emit(string.Create(CultureInfo.InvariantCulture, $"X {shown.Id}"));
    }

    private void EmitPlay(Pending block, bool used = false)
    {
        if (block.Source is not { } source || !_shown.TryGetValue(source.Id, out var shown))
        {
            return;
        }

        block.Emitted = true;
        Emit($"{(used ? 'U' : 'P')} {shown.Id} {FaceUpId(block.Target)}");
    }

    private void EmitAttack(Pending block, int defender)
    {
        if (block.Source is not { } source || !_shown.TryGetValue(source.Id, out var attacker))
        {
            return;
        }

        block.Emitted = true;
        Emit($"A {attacker.Id} {FaceUpId(defender)}");
    }

    /// <summary>The replay id of a face-up card, else "-".</summary>
    private string FaceUpId(int entityId) =>
        entityId > 0 && _shown.TryGetValue(entityId, out var shown) && !shown.Hidden
            ? shown.Id.ToString(CultureInfo.InvariantCulture)
            : "-";

    private string FaceUpCard(Entity? entity) =>
        entity is not null && _shown.TryGetValue(entity.Id, out var shown) && !shown.Hidden && shown.Card != "-" ? shown.Card : "-";

    private void Emit(string line)
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

    private sealed class Shown(int id)
    {
        public int Id { get; } = id;

        public string Zone { get; set; } = string.Empty;

        public char Side { get; set; }

        public int Position { get; set; }

        public string Card { get; set; } = "-";

        public bool Hidden { get; set; }
    }

    private sealed class Pending(string type, Entity? source, int target)
    {
        public string Type { get; } = type;

        public Entity? Source { get; } = source;

        public int Target { get; } = target;

        public bool Emitted { get; set; }
    }
}
