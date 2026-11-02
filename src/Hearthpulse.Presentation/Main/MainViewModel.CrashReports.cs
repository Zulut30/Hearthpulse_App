#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

public sealed partial class MainViewModel
{
    /// <summary>
    /// Settings → Data: send a report of every new kind of crash without asking (<see cref="Core.AppSettings.SendCrashReports"/>,
    /// off by default; docs/crash-reports.md). Off, a report goes only from the error window's "Send report".
    /// </summary>
    public bool CrashReports
    {
        get => Settings.SendCrashReports;
        set
        {
            if (Settings.SendCrashReports == value)
            {
                return;
            }

            Settings.SendCrashReports = value;
            Raise();
            CrashReportsChanged?.Invoke();
        }
    }

    /// <summary>The crash report switch changed: the host saves the settings.</summary>
    public event Action? CrashReportsChanged;
}
