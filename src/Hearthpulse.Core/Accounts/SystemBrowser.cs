using System.ComponentModel;
using System.Diagnostics;

namespace Hearthpulse.Core.Accounts;

/// <summary>Opens a URL in the player's default browser.</summary>
public static class SystemBrowser
{
    public static bool Open(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        try
        {
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
            {
                start = new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true };
            }
            else
            {
                start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
                start.ArgumentList.Add(url.AbsoluteUri);
            }

            using var process = Process.Start(start);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Diag.Error("account: could not open the browser", ex);
            return false;
        }
    }
}
