#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Secrets;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>One Secret on the Secrets panel: still possible, crossed out by hand, or ruled out by the game.</summary>
/// <param name="Count">"×2" when two of three active Secrets can be this card; empty when one or all of them can.</param>
public sealed record SecretTile(string Key, string CardId, string Name, int Cost, string Count, bool Open, bool Crossed, bool RuledOut, bool Known);

/// <summary>One active Secret: "turn 3 · 3 mana · 4 left" (the class is in the pool line).</summary>
public sealed record SecretSlotLine(string Text);

/// <summary>The opponent's Secrets panel next to their hero.</summary>
public sealed partial class OverlayViewModel
{
    private bool _showSecrets;
    private string _secretsTitle = string.Empty;
    private string _secretsPool = string.Empty;
    private IReadOnlyList<SecretTile> _secretTiles = Array.Empty<SecretTile>();
    private IReadOnlyList<SecretSlotLine> _secretSlots = Array.Empty<SecretSlotLine>();
    private RelayCommand? _toggleSecret;
    private RelayCommand? _toggleSecretsExpanded;
    private bool _secretsExpanded;
    private string _secretsMore = string.Empty;
    private bool _showSecretsMore;
    private SecretsView? _secrets;

    /// <summary>
    /// Possible Secrets shown while the list is folded: the panel sits above the opponent's side of the board and must
    /// not grow onto it (a Wild pool has over twenty rows).
    /// </summary>
    public const int SecretsFoldedRows = 4;

    public bool ShowSecrets { get => _showSecrets; private set => Set(ref _showSecrets, value); }

    /// <summary>"Secrets · 2".</summary>
    public string SecretsTitle { get => _secretsTitle; private set => Set(ref _secretsTitle, value); }

    /// <summary>"Mage · Standard": where the list comes from.</summary>
    public string SecretsPool { get => _secretsPool; private set => Set(ref _secretsPool, value); }

    public IReadOnlyList<SecretTile> SecretTiles { get => _secretTiles; private set => Set(ref _secretTiles, value); }

    public IReadOnlyList<SecretSlotLine> SecretSlots { get => _secretSlots; private set => Set(ref _secretSlots, value); }

    /// <summary>"+5 more" under a folded list, "Fold" under an unfolded one.</summary>
    public string SecretsMore { get => _secretsMore; private set => Set(ref _secretsMore, value); }

    public bool ShowSecretsMore { get => _showSecretsMore; private set => Set(ref _showSecretsMore, value); }

    /// <summary>Click "+N more": show every Secret of the pool (crossed and ruled out ones too), or fold back.</summary>
    public RelayCommand ToggleSecretsExpanded => _toggleSecretsExpanded ??= new RelayCommand(_ =>
    {
        _secretsExpanded = !_secretsExpanded;
        if (_secrets is not null)
        {
            BuildSecretTiles(_secrets);
        }
    });

    /// <summary>The player crossed a Secret out (or back in) on the panel: its key goes to the session.</summary>
    public event Action<string>? SecretToggleRequested;

    /// <summary>Click a Secret: cross it out, or bring it back.</summary>
    public RelayCommand ToggleSecret => _toggleSecret ??= new RelayCommand(p =>
    {
        if (p is string { Length: > 0 } key)
        {
            SecretToggleRequested?.Invoke(key);
        }
    });

    private void UpdateSecrets(ConstructedView? view, AppSettings settings)
    {
        var secrets = view?.Secrets;
        ShowSecrets = settings.ShowSecrets && secrets is { Slots.Count: > 0 };
        if (!ShowSecrets)
        {
            _secretsExpanded = false;
            _secrets = null;
            return;
        }

        SecretsTitle = $"{Loc.T("Overlay.Secrets.Title")} · {secrets!.Slots.Count}";
        SecretsPool = secrets.Pool;
        Replace("secret-slots", secrets.Slots, s => $"{s.EntityId}:{s.CardClass}:{s.PaidCost}:{s.CreatedBy}:{s.KnownCardId}:{s.Possible}", () =>
            SecretSlots = secrets.Slots.Select(s => new SecretSlotLine(SlotText(s))).ToList());
        _secrets = secrets;
        Replace("secret-rows", secrets.Rows, r => $"{secrets.Slots.Count}:{r.Key}:{r.Possible}:{r.Crossed}:{r.Known}", () => BuildSecretTiles(secrets));
    }

    private void BuildSecretTiles(SecretsView secrets)
    {
        // Rows come open first (known, then by class, cost and name); folded, only the first open ones are shown.
        var open = secrets.Rows.Count(r => r.IsOpen);
        var shown = _secretsExpanded ? secrets.Rows : secrets.Rows.Take(Math.Min(open, SecretsFoldedRows)).ToList();
        SecretTiles = shown
            .Select(r => new SecretTile(r.Key, r.CardId, r.Name, r.Cost, CountText(r.Possible, secrets.Slots.Count),
                r.IsOpen, r.Crossed, !r.IsOpen && !r.Crossed, r.Known))
            .ToList();
        var hidden = secrets.Rows.Count - shown.Count;
        ShowSecretsMore = _secretsExpanded || hidden > 0;
        SecretsMore = _secretsExpanded ? Loc.T("Overlay.Secrets.Fold") : Loc.F("Overlay.Secrets.More", hidden);
    }

    /// <summary>"×N" only when it tells something: more than one, but not every active Secret, can be the card.</summary>
    private static string CountText(int possible, int secrets) =>
        possible > 1 && possible < secrets ? Loc.F("Overlay.Secrets.Possible", possible) : string.Empty;

    private static string SlotText(SecretSlot slot)
    {
        var parts = new List<string> { Loc.F("Overlay.Secrets.Turn", slot.Turn) };
        if (slot.PaidCost is { } paid)
        {
            parts.Add(Loc.F("Overlay.Secrets.Paid", paid));
        }

        if (slot.CreatedBy is { Length: > 0 } creator)
        {
            parts.Add(Loc.F("Overlay.Secrets.From", creator));
        }

        parts.Add(slot.KnownCardId is not null ? Loc.T("Overlay.Secrets.Known") : Loc.F("Overlay.Secrets.Left", slot.Possible));
        return string.Join(" · ", parts);
    }
}
