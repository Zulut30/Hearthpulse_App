using System.Windows.Media;
using Hearthpulse.Core;

namespace Hearthpulse.App.Infrastructure;

/// <summary>
/// The app's text font: Segoe UI Variable where Windows has it, else Segoe UI. An isolated run (visual baselines) takes
/// Segoe UI alone, which every Windows has, so its frames do not depend on the PC's fonts.
/// </summary>
public static class TextFont
{
    public const string Families = "Segoe UI Variable Text, Segoe UI";

    public static string Name { get; } = Isolation.On ? "Segoe UI" : Families;

    public static FontFamily Family { get; } = new(Name);
}
