using System.Globalization;
using System.Text.RegularExpressions;
using Hearthpulse.Core.Logs;

namespace Hearthpulse.Core.Power;

public sealed partial class PowerParser
{
    private void HandleChoices(string body)
    {
        if (Game is not { } game)
        {
            return;
        }

        var header = ChoicesHeaderRegex().Match(body);
        if (header.Success)
        {
            FlushChoices();
            var player = header.Groups["player"].Value;
            game.SetLocalPlayerByName(player);
            _choiceType = header.Groups["type"].Value;
            _choiceIsLocal = game.LocalPlayer?.Name == player;
            _choiceSource = null;
            return;
        }

        if (_choiceType is not null && body.StartsWith("Source=", StringComparison.Ordinal))
        {
            _choiceSource = Resolve(body["Source=".Length..].Trim());
            return;
        }

        var option = ChoiceEntityRegex().Match(body);
        if (option.Success && _choiceType is not null && Resolve(option.Groups["entity"].Value) is { } entity)
        {
            _choiceOptions.Add(entity);
        }
    }

    private void FlushChoices()
    {
        if (_choiceType is null || Game is not { } game)
        {
            _choiceType = null;
            _choiceOptions.Clear();
            return;
        }

        var type = _choiceType;
        var options = _choiceOptions.ToArray();
        var local = _choiceIsLocal;
        var source = _choiceSource;
        _choiceType = null;
        _choiceSource = null;
        _choiceOptions.Clear();
        if (local)
        {
            foreach (var listener in _listeners) listener.OnChoices(game, type, source, options);
        }
    }

    private void HandleChosen(string body)
    {
        if (Game is not { } game)
        {
            return;
        }

        var header = ChosenHeaderRegex().Match(body);
        if (header.Success)
        {
            _chosenIsLocal = game.LocalPlayer?.Name == header.Groups["player"].Value;
            if (_chosenIsLocal)
            {
                foreach (var listener in _listeners) listener.OnChoiceMade(game);
            }

            return;
        }

        // The picked cards follow the header, one Entities[n]= line each.
        var picked = ChoiceEntityRegex().Match(body);
        if (_chosenIsLocal && picked.Success && Resolve(picked.Groups["entity"].Value) is { } entity)
        {
            foreach (var listener in _listeners) listener.OnChosen(game, entity);
        }
    }
}
