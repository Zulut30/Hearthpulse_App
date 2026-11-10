#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Overlay;
#else
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Overlay;
#endif
using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Hosting;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Updates;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

public sealed partial class MainViewModel
{
    private bool _tribeRows;

    /// <summary>The "Comps" tab's name: "Tribes" while the pool's tribes stand in for the service's comps.</summary>
    public string CompTabLabel => _tribeRows ? Loc.T("Main.Bg.Tribes") : Loc.T("Main.Bg.Comps");

    public string CompCaption => _tribeRows ? Loc.T("Main.Bg.CompCaptionTribes") : Loc.T("Main.Bg.CompCaptionComps");

    /// <summary>
    /// The card database, the community stats or the language changed: the tier tabs are built again from the site's
    /// lists (MainViewModel.BgSite.cs). Of <see cref="BattlegroundsMeta"/> the tabs read only the pool's tribes, the
    /// stand-in of the Comps tab; My stats and the overlay keep reading it whole.
    /// </summary>
    private void RebuildMeta() => RebuildBgTabs();
}
