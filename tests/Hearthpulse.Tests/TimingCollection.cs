namespace Hearthpulse.Tests;

/// <summary>
/// Tests that assert on wall-clock time (<c>[Trait("Category", "Timing")]</c>). They run one at a time after the
/// parallel collections: in a full parallel run, or with another build on the machine, they measured the load instead
/// of the code. CI also runs them without coverage instrumentation (hearthpulse-testing).
/// </summary>
[CollectionDefinition(nameof(TimingCollection), DisableParallelization = true)]
public sealed class TimingCollection;
