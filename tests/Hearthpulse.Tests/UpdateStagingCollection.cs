namespace Hearthpulse.Tests;

/// <summary>
/// Full and delta updater tests share AppPaths.Root/updates. A full download intentionally removes the other staged
/// files, so their download-and-assert sequences must not overlap between these two test classes.
/// </summary>
[CollectionDefinition(nameof(UpdateStagingCollection))]
public sealed class UpdateStagingCollection;
