using System.Globalization;
using System.Text;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Replay;

/// <summary>Game and player tags, the tavern and combat phases, and the steps (plays, buys, attacks) they write.</summary>
public sealed partial class BattlegroundsReplayRecorder
{
    private void OnEntity(GameState game, Entity entity)
    {
        if (_ended)
        {
            return;
        }

        NoteHero(entity);
        Sync(game, entity);
    }

    private void OnGameTag(GameState game, string tag, string value)
    {
        switch (tag)
        {
            case GameTag.Turn when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var turn) && turn % 2 == 1:
                // An odd game turn is a tavern: a combat still open never got its end signal.
                EndCombat(game);
                Tavern(game);
                break;
            case GameTag.BoardVisualState when value == "2":
            case GameTag.BaconInCombatPhase when value == "1":
                BeginCombat(game);
                break;
            case GameTag.BoardVisualState when value == "1":
            case GameTag.BaconInCombatPhase when value == "0":
                EndCombat(game);
                break;
            case GameTag.Step when value != "MAIN_END":
                // Printed at the log's top level (MAIN_END sometimes inside a block).
                TopLevel(game);
                if (value == "MAIN_START" && _combat)
                {
                    // Both boards are set up: they belong to the combat's first step.
                    Flush();
                }

                break;
            case "PROPOSED_DEFENDER" when _step is { Kind: "ATTACK", Target: 0 } attack
                                          && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var defender):
                attack.Target = defender;
                break;
        }
    }

    private void OnPlayerTag(GameState game, PlayerSlot player, string tag, string value)
    {
        if (player.PlayerId != game.LocalPlayerId)
        {
            return;
        }

        if (PlayerTags.Contains(tag))
        {
            PendingTag(-1, tag, $"V L {tag} {Token(value)}");
            _leadersDirty |= tag == GameTag.PlayerTechLevel;
        }
        else if (tag == GameTag.PlayState && value is "WON" or "LOST" or "TIED" or "CONCEDED")
        {
            Finish(game, conceded: value == "CONCEDED");
        }
    }

    /// <summary>The game is over for the player: the open combat ends, nothing more is written but the place.</summary>
    /// <param name="conceded">
    /// The player conceded: a fight still playing with no hero attack yet has no known end and stays open (no result line).
    /// </param>
    private void Finish(GameState game, bool conceded = false)
    {
        if (_ended)
        {
            return;
        }

        EndStep(game);
        if (!(conceded && _combat && _fightResult is null && _heroAttack is null))
        {
            EndCombat(game);
        }

        Flush();
        _ended = true;
    }

    private static bool Finished(GameState game) =>
        game.LocalPlayerEntity?.GetString(GameTag.PlayState) is "WON" or "LOST" or "TIED" or "CONCEDED";

    private static string ResultLine(GameState game) =>
        string.Create(CultureInfo.InvariantCulture, $"R {game.LocalHero?.GetInt(GameTag.PlayerLeaderboardPlace) ?? 0}");

    private static bool InCombat(GameState game) =>
        game.GameEntity is { } entity && (entity.GetInt(GameTag.BaconInCombatPhase) == 1 || entity.GetInt(GameTag.BoardVisualState) == 2);

    private void Tavern(GameState game)
    {
        if (_tavernTurn == game.BattlegroundsTurn)
        {
            // Already open (recording started on the TURN line itself).
            return;
        }

        _tavernTurn = game.BattlegroundsTurn;
        EndStep(game);
        Marker(string.Create(CultureInfo.InvariantCulture, $"T {game.BattlegroundsTurn} {Seconds(game)}"));
        NoteOwnHero(game);
    }

    private void BeginCombat(GameState game)
    {
        if (_combat || _ended)
        {
            return;
        }

        EndStep(game);
        WriteOpenChoice(game);
        Marker(string.Create(CultureInfo.InvariantCulture, $"F {game.BattlegroundsTurn} {Seconds(game)}"));
        // Our hero as the tavern left it, noted before the fight starts; a recording that starts mid-combat notes it at
        // the next tavern.
        NoteOwnHero(game);
        _combat = true;
        _combatSteps = 0;
        _localHeroAtStart = Durability(OwnHero(game));
        _opponentHero = null;
        _heroAttack = null;
        _fightResult = null;
        _taken = _dealt = 0;

        // Bob's offer is not on screen in combat.
        foreach (var (entityId, shown) in _shown.Where(s => s.Value.Side == 'B').ToList())
        {
            _offer.Add(entityId);
            Remove(shown, entityId, died: false);
        }
    }

    /// <summary>
    /// Ends the open combat: the end line comes before what the game cleared up after the fight, so the combat's last
    /// step shows the boards as the fight left them.
    /// </summary>
    private void EndCombat(GameState game)
    {
        if (!_combat)
        {
            return;
        }

        EndStep(game);
        // Without a hero attack nobody was left standing (a tie), unless our hero lost health some other way.
        var taken = _fightResult is null ? Math.Max(0, _localHeroAtStart - Durability(OwnHero(game))) : _taken;
        var result = _fightResult ?? (taken > 0 ? "LOSS" : "TIE");
        if (_combatSteps == 0)
        {
            // No step since the start (no STEP MAIN_START either): what is pending set the boards up.
            Flush();
        }

        WriteLeadersNow(game);
        Write(string.Create(CultureInfo.InvariantCulture, $"f {result} {taken} {_dealt}"));
        _combat = false;
        Flush();
        // The opponent's table and our Duos partner's leave the screen with the fight.
        foreach (var (entityId, shown) in _shown.Where(s => s.Value.Side is 'O' or 'P').ToList())
        {
            Remove(shown, entityId, died: false);
        }

        _opponentHero = null;
        _joinedCombat = false;
        foreach (var id in _offer.ToList())
        {
            _offer.Remove(id);
            if (game.Entities.GetValueOrDefault(id) is { } kept)
            {
                // A frozen offer the fight left in Bob's play is shown in the next tavern.
                Sync(game, kept);
            }
        }
    }

    private static int Durability(Entity? hero) => hero is null ? 0 : hero.Health + hero.GetInt(GameTag.Armor);

    /// <summary>The step a top-level block is, or null when it is none (its changes belong to the step before).</summary>
    private Step? StepOf(GameState game, string blockType, Entity? source)
    {
        if (_combat && blockType == "DEATHS")
        {
            // The game's own block, the one that names who died.
            return new Step("DEATHS", null, lazy: true);
        }

        if (source is null || source.CardType is "GAME" or "PLAYER" or "ENCHANTMENT")
        {
            return null;
        }

        var ours = source.Controller == game.LocalPlayerId;
        if (_combat)
        {
            var seen = _shown.TryGetValue(source.Id, out var shown) ? shown : null;
            return blockType switch
            {
                "ATTACK" when seen is not null => new Step("ATTACK", source, lazy: false),
                "TRIGGER" or "POWER" when TableTypes.Contains(source.CardType) && (seen is not null || _ids.ContainsKey(source.Id))
                                          && (ours || source.Controller == game.OpponentPlayerId
                                              || (source.Controller != 0 && source.Controller == game.PartnerPlayerId)) => new Step("TRIGGER", source, lazy: true),
                _ => null,
            };
        }

        return (blockType, source.CardId) switch
        {
            ("PLAY", "TB_BaconShop_DragBuy" or "TB_BaconShop_DragBuy_Spell") => new Step("BUY", source, lazy: false),
            ("PLAY", "TB_BaconShop_DragSell") => new Step("SELL", source, lazy: false),
            ("PLAY", "TB_BaconShop_8p_Reroll_Button") => new Step("REFRESH", null, lazy: false),
            ("PLAY", "TB_BaconShopLockAll_Button") => new Step("FREEZE", null, lazy: false),
            ("TRIGGER", "TB_BaconShopLockAll_Button") => new Step("FREEZE", null, lazy: true),
            ("PLAY", var id) when id.StartsWith("TB_BaconShopTechUp", StringComparison.Ordinal) => new Step("UPGRADE", null, lazy: false),
            ("PLAY", _) when ours && source.Zone == "HAND" => new Step("PLAY", source, lazy: false),
            ("PLAY", _) when ours && source.CardType == "HERO_POWER" => new Step("POWER", source, lazy: false),
            // A minion of ours on the table used (an activated power, often aimed at a card in hand).
            ("PLAY", _) when ours && source.Zone == "PLAY" && source.IsMinion => new Step("USE", source, lazy: false),
            ("MOVE_MINION", _) when ours && source.IsMinion => new Step("MOVE", source, lazy: false),
            ("TRIGGER" or "POWER", _) when ours && _shown.ContainsKey(source.Id) && TableTypes.Contains(source.CardType) => new Step("TRIGGER", source, lazy: true),
            _ => null,
        };
    }

    private void WriteStep(GameState game, Step step)
    {
        if (_combat && ++_combatSteps > MaxCombatSteps)
        {
            // The fight goes on in the state lines; only its steps stop.
            Truncated = true;
            return;
        }

        // Bought, sold and aimed cards are the source button's CARD_TARGET; a played card's target is its own.
        var target = step.Kind is "BUY" or "SELL" or "PLAY" or "POWER" or "USE" ? step.Source?.GetInt("CARD_TARGET") ?? 0 : step.Target;
        var line = step.Kind switch
        {
            "BUY" or "SELL" => $"M {step.Kind} {IdOf(target)}",
            "REFRESH" or "FREEZE" or "UPGRADE" or "DEATHS" => $"M {step.Kind} -",
            "PLAY" or "POWER" or "USE" or "ATTACK" => $"M {step.Kind} {IdOf(step.Source?.Id ?? 0)} {IdOf(target)}",
            _ => $"M {step.Kind} {IdOf(step.Source?.Id ?? 0)}",
        };
        WriteLeaders(game);
        Write(line);
        Flush();
    }

    /// <summary>A step line: what is pending belongs to the step before it.</summary>
    private void Marker(string line)
    {
        if (_game is { } game)
        {
            WriteLeaders(game);
        }

        Flush();
        Write(line);
        _looseMove = false;
    }
}
