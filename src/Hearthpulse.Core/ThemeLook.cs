namespace Hearthpulse.Core;

/// <summary>
/// The shape of the interface on top of its colours: corner style and density (Settings, Appearance). Both theme files
/// define the same radius and padding tokens (<c>RadiusSmall</c>, <c>RadiusMedium</c>, <c>RadiusLarge</c>,
/// <c>CardPadding</c>); a look replaces their values, the pill radius stays round.
/// </summary>
public sealed record ThemeLook(bool SharpCorners, bool Dense)
{
    public const string Rounded = "Rounded";
    public const string Sharp = "Sharp";
    public const string Comfortable = "Comfortable";
    public const string Compact = "Compact";

    public static ThemeLook Default { get; } = new(false, false);

    public static ThemeLook FromSettings(AppSettings settings) =>
        new(settings.CornerStyle == Sharp, settings.Density == Compact);

    public bool IsDefault => !SharpCorners && !Dense;

    /// <summary>The value of a shape token under this look, or null when the token keeps its built-in value.</summary>
    public double? Token(string key) => key switch
    {
        "RadiusSmall" when SharpCorners => 2,
        "RadiusMedium" when SharpCorners => 2,
        "RadiusLarge" when SharpCorners => 3,
        "CardPadding" when Dense => 16,
        _ => null,
    };

    /// <summary>The shape tokens a look can change.</summary>
    public static IReadOnlyList<string> Tokens { get; } = ["RadiusSmall", "RadiusMedium", "RadiusLarge", "CardPadding"];
}
