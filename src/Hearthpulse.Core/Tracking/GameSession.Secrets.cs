using Hearthpulse.Core.Power;
using Hearthpulse.Core.Secrets;

namespace Hearthpulse.Core.Tracking;

/// <summary>The opponent's Secrets: what each one can still be (docs/secrets-helper.md).</summary>
public sealed partial class GameSession
{
    /// <summary>How long Power.log must stay quiet before the last action counts as resolved.</summary>
    private static readonly TimeSpan SecretsQuiet = TimeSpan.FromSeconds(1);

    private readonly SecretTracker _secrets = new();

    /// <summary>Crosses a Secret out of the overlay list by hand, or brings it back (its row key). Any thread.</summary>
    public void ToggleSecret(string key)
    {
        _secrets.Toggle(key);
        _dirty = true;
    }

    /// <summary>Adds the opponent's Secrets to a Constructed view; called while publishing.</summary>
    private ConstructedView WithSecrets(ConstructedView view, GameState game)
    {
        try
        {
            return view with { Secrets = _secrets.BuildView(game, _cards) };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            Diag.Error("Building the Secrets view failed", ex);
            return view;
        }
    }

    /// <summary>On the tick: once the log has been quiet, the player's last action has resolved and is judged.</summary>
    private void TickSecrets(DateTime now)
    {
        if (_secrets.HasPending && now - _lastPowerLinesAt >= SecretsQuiet && _parser?.Game is { } game && _secrets.CommitIfQuiet(game))
        {
            _dirty = true;
        }
    }
}
