using Avalonia;
using Avalonia.Data;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.UI.Infrastructure;

/// <summary>
/// <c>{i:Loc Settings.Title}</c>: the string <paramref name="key"/> in the interface language, updated live when the
/// language changes. Keys: docs/localization.md.
/// </summary>
public sealed class LocExtension(string key)
{
    public LocExtension()
        : this(string.Empty)
    {
    }

    /// <summary>The string's key, e.g. "Settings.Title".</summary>
    public string Key { get; set; } = key;

    public BindingBase ProvideValue(IServiceProvider serviceProvider) => new LocText(Key).ToBinding();

    /// <summary>The string's current text, then the new text after each language change.</summary>
    private sealed class LocText(string key) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer)
        {
            void Push() => observer.OnNext(Loc.T(key));
            Loc.Instance.LanguageChanged += Push;
            Push();
            return new Unsubscribe(() => Loc.Instance.LanguageChanged -= Push);
        }
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
