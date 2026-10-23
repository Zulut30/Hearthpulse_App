using System.Windows.Data;
using System.Windows.Markup;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.App.Infrastructure;

/// <summary>
/// <c>{i:Loc Settings.Title}</c>: the string <paramref name="key"/> in the interface language, updated live when the
/// language changes (a one-way binding to <see cref="Loc.Instance"/>'s indexer). Keys: docs/localization.md.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public LocExtension()
        : this(string.Empty)
    {
    }

    /// <summary>The string's key, e.g. "Settings.Title".</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
