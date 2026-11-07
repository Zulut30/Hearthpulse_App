using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Hearthpulse.UI.Overlay;

/// <summary>Combat odds: the overlay's "odds" panel and the OBS window "Hearthpulse — Combat odds" (bound to <see cref="OverlayViewModel"/>).</summary>
public partial class OddsPanel : UserControl
{
    public OddsPanel() => InitializeComponent();
}

/// <summary>
/// A share (0..1) to a star column, so the odds bar splits whatever width the card has, as the WPF bar's star columns
/// (WPF binds <see cref="GridLength"/> shares straight away; the Avalonia view model keeps plain numbers).
/// </summary>
public sealed class StarShare : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new GridLength(value is double share && share > 0 ? share : 0, GridUnitType.Star);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
