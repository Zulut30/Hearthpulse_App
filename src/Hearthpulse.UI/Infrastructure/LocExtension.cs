using Avalonia.Data;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.UI.Infrastructure;

/// <summary>
/// <c>{i:Loc Settings.Title}</c>: the string <paramref name="key"/> in the interface language, updated live when the
/// language changes. Keys: docs/localization.md.
/// </summary>
/// <remarks>
/// A one-way binding to <see cref="Loc.Instance"/>'s indexer, as in WPF: Avalonia listens to its <c>Item[]</c>
/// notifications through a weak subscription, so controls built from templates (overlay rows, deck page rows) are not
/// kept alive by the static <see cref="Loc.Instance"/> after they leave the tree.
/// </remarks>
public sealed class LocExtension(string key)
{
    public LocExtension()
        : this(string.Empty)
    {
    }

    /// <summary>The string's key, e.g. "Settings.Title".</summary>
    public string Key { get; set; } = key;

    public BindingBase ProvideValue(IServiceProvider serviceProvider) =>
        new ReflectionBinding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
}
