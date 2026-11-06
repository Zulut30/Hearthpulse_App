using System.Text;
using Hearthpulse.Core.Hearthstone;

namespace Hearthpulse.Core.Logs;

public interface ILogConsumer
{
    /// <summary>Called when the client starts a new log session (new folder).</summary>
    void OnSessionStarted(string sessionDir, DateTime sessionStart, bool attachedMidSession);

    /// <summary>A batch of new lines from one file. <paramref name="catchingUp"/> is true for history read at startup.</summary>
    void OnLines(string fileName, IReadOnlyList<string> lines, bool catchingUp);

    /// <summary>Called after each polling pass.</summary>
    void OnTick();

    /// <summary>
    /// The watcher is about to replay the current game from its start (hard refresh). The tracked state is rebuilt
    /// from the replayed lines, so it must be dropped here without recording the game as finished.
    /// </summary>
    void OnResync() { }

    /// <summary>
    /// True while nothing is expected in the logs (the game is closed and Power.log is quiet): the watcher then looks
    /// once a second instead of ten times. Lines that do arrive make the consumer say false again.
    /// </summary>
    bool Quiet => false;
}
