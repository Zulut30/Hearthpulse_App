using Hearthpulse.Core;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// The head of a tier as a row of a flat tier list: "Tier S" and its bound ("from 53 % · 3 archetypes"), followed in
/// the list by the tier's own rows (none is a valid tier: <see cref="IsEmpty"/>). The view gives it the height of a data
/// row, because the list panel (PageRowsPanel) takes every row for the same height. The list itself is a
/// <c>SyncList&lt;object&gt;</c> the view binds once; the row template is chosen by the row's type.
/// </summary>
public sealed record TierHeadRow(string Tier, string Title, string Caption, bool IsEmpty) : IImmutableView;
