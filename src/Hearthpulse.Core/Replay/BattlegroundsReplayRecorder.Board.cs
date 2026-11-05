using System.Globalization;
using System.Text;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Replay;

/// <summary>What each side shows: entities in and out of view, their tags, the heroes and the leaderboard.</summary>
public sealed partial class BattlegroundsReplayRecorder
{
    /// <summary>One of our minions moved outside a block (no MOVE_MINION): a move step, one per run of such lines.</summary>
    private void LooseMove(GameState game, Entity entity)
    {
        // A minion entering play gets its slot after its zone: only a minion leaving a slot it had moves.
        if (_combat || _looseMove || !_shown.TryGetValue(entity.Id, out var shown) || shown is not { Side: 'L', Zone: "PLAY", Type: "MINION" }
            || shown.Position == 0 || shown.Position == entity.ZonePosition || entity.Zone != "PLAY")
        {
            return;
        }

        Marker($"M MOVE {shown.Id}");
        _looseMove = true;
    }

    /// <summary>Brings one entity's replay state up to date with the game, under the rule of what the player sees.</summary>
    private void Sync(GameState game, Entity entity)
    {
        if (entity.Id == game.GameEntityId || entity.CardType is "PLAYER" or "GAME" or "ENCHANTMENT")
        {
            return;
        }

        if (_joinedCombat && entity.Controller == game.OpponentPlayerId && entity.Zone == "PLAY" && entity.GetInt("FROZEN") == 1)
        {
            // Joined during a combat: a frozen card in Bob's play is his offer kept for the next tavern, not the opponent's.
            _offer.Add(entity.Id);
        }

        _shown.TryGetValue(entity.Id, out var shown);
        var side = SideOf(game, entity);
        if (side == '\0')
        {
            if (shown is not null)
            {
                Remove(shown, entity.Id, died: shown.Zone == "PLAY" && entity.Zone == "GRAVEYARD" && shown.Type is "MINION" or "HERO");
            }

            return;
        }

        var zone = entity.Zone;
        var position = entity.ZonePosition;
        if (shown is not null && shown.Side == side && shown.Zone == zone && shown.Card == entity.CardId && shown.Type == entity.CardType)
        {
            if (shown.Position != position)
            {
                shown.Position = position;
                PendingTag(shown.Id, "Z", string.Create(CultureInfo.InvariantCulture, $"Z {shown.Id} {position}"));
            }

            return;
        }

        // Every card shown is face up: one moving (bought, played, transformed) keeps its id.
        shown ??= new Shown(++_nextId);
        shown.Side = side;
        shown.Zone = zone;
        shown.Position = position;
        shown.Card = entity.CardId;
        shown.Type = entity.CardType;
        _shown[entity.Id] = shown;
        _ids[entity.Id] = shown.Id;
        if (side == 'O' && shown.Type == "HERO")
        {
            _opponentHero = entity;
        }

        var line = new StringBuilder(64);
        line.Append(CultureInfo.InvariantCulture, $"E {shown.Id} {side} {zone} {position} {entity.CardId} {Token(entity.CardType)}");
        foreach (var tag in CardTagOrder)
        {
            if (entity.GetString(tag) is { Length: > 0 } value && value != "0")
            {
                line.Append(' ').Append(tag).Append('=').Append(Token(value));
            }
        }

        PendingCard(shown.Id, line.ToString(), full: true);
    }

    /// <summary>
    /// Which side shows the entity now: ours (L), Bob's offer (B), the combat opponent's (O), our Duos partner's board in
    /// combat (P), or none. The partner's tavern is never on our screen, so only their table in combat is written.
    /// </summary>
    private char SideOf(GameState game, Entity entity)
    {
        var card = entity.CardId;
        if (card.Length == 0 || card == PlaceholderHero || TrinketSlots.Contains(card))
        {
            return '\0';
        }

        var zone = entity.Zone;
        var type = entity.CardType;
        if (entity.Controller == game.LocalPlayerId)
        {
            // Duos, HDT's model (TagChangeActions.OnHeroEntityChange, BobsBuddyInvoker): while our partner fights for the
            // team, our player's HERO_ENTITY points at the partner's hero and the partner's minions come in under our
            // controller. A minion first shown then is the partner's (P); one of ours already shown stays ours; nothing
            // else of theirs (hero, hero power, trinkets) is written.
            var seen = _shown.TryGetValue(entity.Id, out var shownNow) ? shownNow.Side : '\0';
            // A minion shown as the partner's stays theirs until the fight ends, whatever our hero entity does meanwhile.
            if (_combat && seen == 'P')
            {
                return zone == "PLAY" && type == "MINION" ? 'P' : '\0';
            }

            if (PartnerOnTable(game) && seen != 'L')
            {
                return zone == "PLAY" && type == "MINION" ? 'P' : '\0';
            }

            return zone switch
            {
                "PLAY" when TableTypes.Contains(type) => 'L',
                "HAND" when CardTypes.Contains(type) => 'L',
                "SECRET" when type == "SPELL" && entity.Has("QUEST") => 'L',
                _ => '\0',
            };
        }

        // A fallback should the partner be a player of their own: only their minions in a fight, never their hero.
        if (_combat && zone == "PLAY" && type == "MINION" && entity.Controller != 0 && entity.Controller == game.PartnerPlayerId)
        {
            return 'P';
        }

        if (entity.Controller != game.OpponentPlayerId || zone != "PLAY")
        {
            return '\0';
        }

        // The innkeeper's slot holds Bob's offer in the tavern and the combat opponent in combat; a hero there is the
        // opponent's only with a leaderboard slot (Bob has none).
        return _combat
            ? TableTypes.Contains(type) && (type != "HERO" || entity.GetInt(GameTag.PlayerId) > 0) && !_offer.Contains(entity.Id) ? 'O' : '\0'
            : CardTypes.Contains(type) ? 'B' : '\0';
    }

    /// <summary>
    /// Our partner's table is the one fighting for the team: in a combat, our player's hero is not the one we had when it
    /// started but a hero of our Duos team (<c>BACON_DUO_TEAM_ID</c>).
    /// </summary>
    private bool PartnerOnTable(GameState game) =>
        _combat && _ownHeroId != 0 && _ownTeam > 0
        && game.LocalHero is { } hero && hero.Id != _ownHeroId && hero.GetInt(GameTag.BaconDuoTeamId) == _ownTeam;

    /// <summary>
    /// Our own hero: the one noted outside combat (<see cref="NoteOwnHero"/>), so a partner's fight, where our player's
    /// hero entity is theirs, never makes their hero ours; before any note, the player's hero.
    /// </summary>
    private Entity? OwnHero(GameState game) =>
        _ownHeroId != 0 && game.Entities.GetValueOrDefault(_ownHeroId) is { } own ? own : game.LocalHero;

    /// <summary>Outside combat only: our player's hero and Duos team now (the tavern is always ours).</summary>
    private void NoteOwnHero(GameState game)
    {
        // Only a hero of our own controller: our player's hero entity may still point at the partner's at the tavern's start.
        if (_combat || game.LocalHero is not { } hero || hero.Controller != game.LocalPlayerId)
        {
            return;
        }

        _ownHeroId = hero.Id;
        _ownTeam = game.TeamOf(game.LocalPlayerId);
    }

    private void Remove(Shown shown, int entityId, bool died)
    {
        _shown.Remove(entityId);
        _died |= died;
        PendingCard(shown.Id, string.Create(CultureInfo.InvariantCulture, $"{(died ? 'Y' : 'X')} {shown.Id}"), full: false);
    }

    private void NoteHero(Entity entity)
    {
        if (entity.CardType == "HERO" && entity.GetInt(GameTag.PlayerId) > 0)
        {
            _heroes.Add(entity.Id);
            _leadersDirty = true;
        }
    }

    /// <summary>
    /// The leaderboard rows that changed: per player slot, the hero the leaderboard shows (the oldest hero entity of the
    /// slot set aside, or our hero; combat copies are newer), its place, health, armor and tavern tier.
    /// </summary>
    private void WriteLeaders(GameState game)
    {
        if (!_leadersDirty)
        {
            return;
        }

        _leadersDirty = false;
        foreach (var (slot, line) in LeaderLines(game))
        {
            _leaders[slot] = line;
            PendingTag(-100 - slot, "L", line);
        }
    }

    /// <summary>
    /// Writes the leaderboard as it is now ahead of a step line that takes the table at once (<c>f</c>, <c>R</c>): a
    /// pending row of the same slot is older and is dropped.
    /// </summary>
    private void WriteLeadersNow(GameState game)
    {
        _leadersDirty = false;
        foreach (var (slot, line) in LeaderLines(game))
        {
            if (_tagLines.Remove((-100 - slot, "L"), out var index))
            {
                _pending[index] = null;
            }

            _leaders[slot] = line;
            Write(line);
        }
    }

    /// <summary>The rows that differ from the ones written.</summary>
    private List<(int Slot, string Line)> LeaderLines(GameState game)
    {
        var local = OwnHero(game);
        var rows = new SortedDictionary<int, Entity>();
        foreach (var id in _heroes)
        {
            if (game.Entities.GetValueOrDefault(id) is not { } hero || hero.CardId.Length == 0 || hero.CardId == PlaceholderHero
                || hero.GetInt(GameTag.PlayerLeaderboardPlace) <= 0 || (hero.Zone != "SETASIDE" && hero != local))
            {
                continue;
            }

            var slot = hero.GetInt(GameTag.PlayerId);
            if (!rows.TryGetValue(slot, out var other) || hero.Id < other.Id)
            {
                rows[slot] = hero;
            }
        }

        var lines = new List<(int Slot, string Line)>();
        foreach (var (slot, hero) in rows)
        {
            var mine = hero == local;
            var tier = hero.GetInt(GameTag.PlayerTechLevel) is > 0 and var t ? t : mine ? game.LocalPlayerEntity?.GetInt(GameTag.PlayerTechLevel) ?? 0 : 0;
            var line = string.Create(CultureInfo.InvariantCulture,
                $"L {slot} {(mine ? 'L' : '-')} {hero.CardId} {hero.GetInt(GameTag.PlayerLeaderboardPlace)} {Math.Max(0, hero.Health)} {hero.GetInt(GameTag.Armor)} {tier}");
            if (_leaders.GetValueOrDefault(slot) != line)
            {
                lines.Add((slot, line));
            }
        }

        return lines;
    }

    private string IdOf(int entityId) =>
        entityId > 0 && _ids.TryGetValue(entityId, out var id) ? id.ToString(CultureInfo.InvariantCulture) : "-";

    private List<string> Ours(params string[] zoneAndTypes)
    {
        var zone = zoneAndTypes[0];
        return _shown.Values
            .Where(s => s.Side == 'L' && s.Zone == zone && (zoneAndTypes.Length == 1 || zoneAndTypes.Skip(1).Contains(s.Type)))
            .OrderBy(s => s.Position).ThenBy(s => s.Id)
            .Select(s => s.Card)
            .ToList();
    }

    private static int Seconds(GameState game) => Math.Max(0, (int)(game.LastActivity - game.StartedAt).TotalSeconds);
}
