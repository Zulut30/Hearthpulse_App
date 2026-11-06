using System.Reflection;
using Hearthpulse.Core.Localization;
using Xunit.Sdk;

[assembly: Hearthpulse.Presentation.Tests.LanguageListeners]

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// Every test leaves <see cref="Loc.LanguageChanged"/> as it found it: the listeners its view models, windows and
/// charts added are taken off after it. The app has one main view model for its lifetime and never needs this; a test
/// run makes about two hundred, and without it each language switch (many tests set ru or en) made every view model of
/// the earlier tests rebuild its lists on the thread pool. A test that waited for its own rebuild then queued behind
/// hundreds of others, past its timeout on a slow runner (DeckSuggestionsContractTests, CI run 42275333010).
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
public sealed class LanguageListenersAttribute : BeforeAfterTestAttribute
{
    // The compiler's backing field of the field-like event; a rename fails here, not silently.
    private static readonly FieldInfo Field = typeof(Loc).GetField(nameof(Loc.LanguageChanged), BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Loc.LanguageChanged has no backing field to read");

    // Tests run one at a time (CollectionBehavior in ViewModelContractTests.cs).
    private static Delegate[] _before = [];

    public override void Before(MethodInfo methodUnderTest) => _before = Listeners();

    public override void After(MethodInfo methodUnderTest)
    {
        foreach (var added in Listeners().Except(_before))
        {
            Loc.Instance.LanguageChanged -= (Action)added;
        }
    }

    internal static Delegate[] Listeners() => (Field.GetValue(Loc.Instance) as Delegate)?.GetInvocationList() ?? [];
}
