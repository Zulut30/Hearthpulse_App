using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;

namespace Hearthpulse.Core.Replay;

public static partial class ReplayImport
{
    // ---- Battlegrounds ---------------------------------------------------------------------------------------------

    private static string? CleanBattlegrounds(ReplayHeader header, IReadOnlyList<string> body, bool dateOnly)
    {
        // Format 4: the event lines, then the G line; format 3: only the rounds and lobby, as bare JSON.
        var v4 = header.Version >= ReplayFile.Version;
        var count = v4 ? body.Count - 1 : 0;
        if (body.Count == 0 || count > BattlegroundsReplayRecorder.MaxEvents || (!v4 && body.Count != 1)
            || ReplayFile.ReadBattlegrounds(body) is not { } read || CleanData(read) is not { } data)
        {
            return null;
        }

        var events = new List<string>(count);
        var shown = new Dictionary<string, char>(StringComparer.Ordinal);
        bool fight = false, opponentInView = false;
        for (var i = 0; i < count; i++)
        {
            var line = body[i];
            if (!BattlegroundsReplayGrammar.IsLine(line))
            {
                return null;
            }

            var t = line.Split(' ');
            switch (t[0])
            {
                case "F":
                    events.Add(line);
                    // Bob's offer leaves view when the combat starts.
                    LeaveView(events, shown, 'B');
                    fight = opponentInView = true;
                    continue;
                case "f":
                    fight = false;
                    break;
                case "T":
                    // The combat opponent and our Duos partner's table leave view by the next tavern.
                    LeaveView(events, shown, 'O');
                    LeaveView(events, shown, 'P');
                    fight = opponentInView = false;
                    break;
                case "E" when (t[2] == "B" && fight) || (t[2] == "O" && !opponentInView)
                              || (t[2] == "P" && (!fight || header.Version < ReplayFile.PartnerVersion)):
                    // The partner's table only in a fight of a version 5 file: never their tavern.
                    continue;
                case "E":
                    shown[t[1]] = t[2][0];
                    break;
                case "X" or "Y" when !shown.Remove(t[1]):
                    continue;
                case "V" or "Z" when t[1] != "L" && !shown.ContainsKey(t[1]):
                    continue;
            }

            events.Add(line);
        }

        var summary = header.Summary is { } given ? CleanSummary(given, dateOnly) : RebuildBattlegroundsSummary(data);
        if (summary is null)
        {
            return null;
        }

        var clean = new ReplayHeader { Kind = ReplayFile.Battlegrounds, Summary = summary, Truncated = header.Truncated };
        return ReplayFile.WriteBattlegrounds(clean, data, events.Count > 0 ? events : null);
    }

    /// <summary>Every card of <paramref name="side"/> still shown leaves view (<c>X</c>).</summary>
    private static void LeaveView(List<string> events, Dictionary<string, char> shown, char side)
    {
        foreach (var id in shown.Where(s => s.Value == side).Select(s => s.Key).ToList())
        {
            shown.Remove(id);
            events.Add("X " + id);
        }
    }

    private static ReplaySummary RebuildBattlegroundsSummary(BattlegroundsReplayData data)
    {
        var local = data.Lobby.FirstOrDefault(h => h.Local);
        var last = data.Rounds.Count > 0 ? data.Rounds[^1] : null;
        return new ReplaySummary
        {
            Mode = "Battlegrounds",
            // An old file does not say when it was played: no date (the stored name starts with "game-").
            StartedAt = DateTime.MinValue,
            HeroCardId = local?.HeroCardId ?? string.Empty,
            Placement = local?.Place ?? 0,
            Turns = last?.Turn ?? 0,
            TavernTier = last?.Tier ?? 0,
        };
    }

    /// <summary>The rounds and lobby from their known fields, checked; null when a card id or a list breaks the limits.</summary>
    private static BattlegroundsReplayData? CleanData(BattlegroundsReplayData data)
    {
        var rounds = Safe(data.Rounds);
        var lobby = Safe(data.Lobby);
        if (rounds.Count > 200 || lobby.Count > 8)
        {
            return null;
        }

        var clean = new List<BattlegroundsRound>(rounds.Count);
        foreach (var round in rounds)
        {
            if (round is null)
            {
                return null;
            }

            var board = Minions(round.Board);
            var opponentBoard = Minions(round.OpponentBoard);
            var shop = Safe(round.Shop);
            if (board is null || opponentBoard is null || shop.Count > 1000 || !shop.All(IsCard)
                || (round.OpponentHero is { } hero && !IsCard(hero)) || round.Result is not (null or "Win" or "Loss" or "Tie"))
            {
                return null;
            }

            clean.Add(new BattlegroundsRound
            {
                Turn = Math.Clamp(round.Turn, 0, 999),
                Tier = Math.Clamp(round.Tier, 0, 7),
                Health = Math.Clamp(round.Health, 0, 9999),
                Board = board,
                OpponentHero = round.OpponentHero,
                OpponentBoard = opponentBoard,
                Shop = [.. shop],
                Result = round.Result,
                DamageTaken = Math.Clamp(round.DamageTaken, 0, 9999),
                DamageDealt = Math.Clamp(round.DamageDealt, 0, 9999),
            });
        }

        if (lobby.Any(h => h is null || !IsCard(h.HeroCardId)))
        {
            return null;
        }

        return new BattlegroundsReplayData
        {
            Rounds = clean,
            Lobby = lobby.Select(h => new BattlegroundsLobbyHero(h.HeroCardId, Math.Clamp(h.Place, 0, 8), h.Local)).ToList(),
        };
    }

    private static List<ReplayMinion>? Minions(IReadOnlyList<ReplayMinion>? minions)
    {
        var list = Safe(minions);
        return list.Count > 14 || list.Any(m => m is null || !IsCard(m.CardId))
            ? null
            : list.Select(m => new ReplayMinion(m.CardId, Stat(m.Attack), Stat(m.Health), m.Golden)).ToList();
    }
}
