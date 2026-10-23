using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Hearthlens.UI;

/// <summary>Theme and fonts shared by every Hearthlens shell; platform shells derive from it.</summary>
public class HearthlensApp : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Windows keeps its native UI font; everywhere else the bundled Inter gives the same look on every system.
        if (OperatingSystem.IsWindows())
        {
            Resources["UiFont"] = new FontFamily("Segoe UI Variable Text, Segoe UI");
        }
    }
}
