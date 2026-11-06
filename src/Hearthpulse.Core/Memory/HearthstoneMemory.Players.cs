using System.Globalization;

namespace Hearthpulse.Core.Memory;

public sealed partial class HearthstoneMemory
{
    /// <summary>
    /// Card id of the game card under the mouse (in hand, on the board, hero, hero power, weapon...), or null when the
    /// mouse is over no card or the game is not in a match. Read from the game's own input handling
    /// (<c>InputManager.m_mousedOverCard</c>), so it follows what the client highlights at any resolution. While a card
    /// is dragged this is the card under the cursor (a target), not the dragged one. Hidden cards (opponent's hand) have
    /// no id and give null.
    /// </summary>
    public string? GetHoveredCardId() => Safe(quiet: true, read: rt =>
    {
        var input = rt.FindClass("InputManager")?.GetStatic("s_instance") ?? default;
        var card = input.IsNull ? default : input["m_mousedOverCard"];
        if (card.IsNull)
        {
            return null;
        }

        var id = card["m_entity"].String("m_cardIdInternal");
        return string.IsNullOrEmpty(id) ? null : id;
    });

    /// <summary>Ranked medals for both players of the current game (only valid in-game).</summary>
    public (MatchPlayerInfo? Local, MatchPlayerInfo? Opponent)? GetMatchPlayers() => Safe<(MatchPlayerInfo?, MatchPlayerInfo?)?>(rt =>
    {
        var state = GameStateInstance(rt);
        var mapField = Field(state.Class, "playerMap", ["m_playerMap"]);
        var playerMap = mapField is null ? default : state.Object(mapField);
        if (playerMap.IsNull)
        {
            return null;
        }

        MatchPlayerInfo? local = null, opponent = null;
        foreach (var player in playerMap.MapValues())
        {
            var playerClass = player.Class;
            if (playerClass?.Name != "Player")
            {
                continue;
            }

            var sideField = Field(playerClass, "side", ["m_side"], integer: true);
            var info = ReadPlayer(player);
            switch (sideField is null ? -1 : player.Int(sideField))
            {
                case 1: local = info; break;
                case 2: opponent = info; break;
            }
        }

        return local is null && opponent is null ? null : (local, opponent);
    });

    private MatchPlayerInfo ReadPlayer(MonoObject player)
    {
        RankInfo? standard = null, wild = null, classic = null, twist = null;
        var medalInfoField = Field(player.Class, "medalInfo", ["m_medalInfo"]);
        var medalInfo = medalInfoField is null ? default : player.Object(medalInfoField);
        var medalsField = Field(medalInfo.Class, "currentMedals", ["m_currMedalInfo"]);
        var medals = medalsField is null ? default : medalInfo.Object(medalsField);
        foreach (var medal in medals.MapValues())
        {
            var medalClass = medal.Class;
            var formatField = Field(medalClass, "format", ["format"], integer: true);
            var leagueField = Field(medalClass, "league", ["leagueId"], integer: true);
            var starField = Field(medalClass, "stars", ["starLevel"], integer: true);
            var legendField = Field(medalClass, "legend", ["legendIndex"], integer: true);
            var seasonField = Field(medalClass, "season", ["seasonId"], integer: true);
            var format = formatField is null ? 0 : medal.Int(formatField);
            var rank = new RankInfo(
                FormatName(format),
                leagueField is null ? 0 : medal.Int(leagueField),
                starField is null ? 0 : medal.Int(starField),
                legendField is null ? 0 : medal.Int(legendField),
                seasonField is null ? 0 : medal.Int(seasonField));
            switch (format)
            {
                case 1: wild = rank; break;
                case 2: standard = rank; break;
                case 3: classic = rank; break;
                case 4: twist = rank; break;
            }
        }

        var nameField = Field(player.Class, "name", ["m_name"]);
        return new MatchPlayerInfo(nameField is null ? null : player.String(nameField), standard, wild, classic, twist);
    }

    private static string FormatName(int format) => format switch
    {
        1 => "Wild",
        2 => "Standard",
        3 => "Classic",
        4 => "Twist",
        _ => "Unknown",
    };
}
