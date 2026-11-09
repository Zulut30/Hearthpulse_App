using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.SourceCapture.Processing;

internal sealed class SourceCaptureState(SourceCaptureLimits limits)
{
    private readonly List<SourceCaptureEvent> _events = [];
    private readonly Stack<string> _blocks = [];
    private readonly Dictionary<int, (string Options, string? Picked)> _choices = [];
    private readonly Dictionary<int, (string Options, int? Picked)> _draft = [];
    private readonly Dictionary<int, bool> _refs = [];
    private string _captureKind = "", _mode = "", _lastKind = "";
    private SourceCaptureEvent? _end;
    private long _derivedBytes;
    private int _unsupported, _gaps, _plays, _buys, _sells, _turns, _shop, _resources;

    internal void Add(SourceCaptureEvent e)
    {
        if (_events.Count >= limits.MaxRecords) throw new SourceCaptureException("limit_exceeded");
        SourceCaptureSchema.Require(e.Seq == _events.Count && _end is null);
        SourceCaptureSchema.Require(e.Seq == 0 ? e.Kind == "start" : e.Kind != "start");
        SourceCaptureSchema.Require(e.Turn >= _turns);
        _turns = e.Turn;
        _lastKind = e.Kind;
        var d = e.Data;
        string derivedKind = e.Kind;
        if (e.Entity > 0 && e.Kind != "entity" && _refs.TryGetValue(e.Entity, out bool hidden) && hidden)
            SourceCaptureSchema.Require(e.Side == "opponent" && e.Card is null &&
                ((e.Kind == "tag" && d.GetProperty("tag").GetString() == GameTag.ZonePosition) ||
                 (e.Kind == "action" && d.GetProperty("action").GetString() is "draw" or "discard" &&
                  d.GetProperty("cost").ValueKind == JsonValueKind.Null)));
        if (e.Card is not null && e.Entity > 0 && e.Kind != "entity") ObserveRef(e.Entity, e.Side, "PLAY");
        if (e.Kind == "tag" && e.Side == "opponent" && e.Entity > 0 &&
            d.GetProperty("tag").GetString() != GameTag.ZonePosition)
            SourceCaptureSchema.Require(_refs.TryGetValue(e.Entity, out bool concealed) && !concealed);
        switch (e.Kind)
        {
            case "start":
                _captureKind = d.GetProperty("captureKind").GetString()!;
                _mode = d.GetProperty("mode").GetString()!;
                SourceCaptureSchema.Require(_captureKind != "draft" || _mode == "arena");
                break;
            case "end":
                _end = e;
                SourceCaptureSchema.Require(_captureKind != "draft" ||
                    (d.GetProperty("result").ValueKind == JsonValueKind.Null && d.GetProperty("placement").ValueKind == JsonValueKind.Null));
                break;
            case "entity":
                ObserveRef(e.Entity, e.Side, d.GetProperty("zone").GetString()!);
                if (e.Side == "shop") derivedKind = "shop";
                break;
            case "snapshot":
                bool shop = false;
                foreach (var c in d.GetProperty("cards").EnumerateArray())
                {
                    string side = c.GetProperty("side").GetString()!;
                    ObserveRef(c.GetProperty("entity").GetInt32(), side, c.GetProperty("zone").GetString()!);
                    shop |= side == "shop";
                }
                // An empty/general board snapshot cannot prove an empty shop was observed.
                if (shop) { _shop++; derivedKind = "shop"; }
                break;
            case "tag":
                string tag = d.GetProperty("tag").GetString()!;
                if (tag == GameTag.Turn)
                {
                    SourceCaptureSchema.Require(d.GetProperty("value").GetInt32() == e.Turn);
                    derivedKind = "turn";
                }
                else if (tag is "RESOURCES" or "RESOURCES_USED" or "TEMP_RESOURCES" or "OVERLOAD_LOCKED" or "OVERLOAD_OWED")
                { _resources++; derivedKind = "resources"; }
                break;
            case "action":
                derivedKind = d.GetProperty("action").GetString()!;
                // Counts describe the local player's explicit actions, never both streams or inferred blocks.
                if (e.Side == "local")
                {
                    if (derivedKind == "play") _plays++;
                    if (derivedKind == "buy") _buys++;
                    if (derivedKind == "sell") _sells++;
                }
                if (derivedKind == "unknown") { _unsupported++; AddGaps(1); }
                break;
            case "choice":
                int id = d.GetProperty("choiceId").GetInt32();
                string options = d.GetProperty("options").GetRawText() + d.GetProperty("sourceCard").GetRawText();
                string? picked = d.GetProperty("resolved").GetBoolean() ? d.GetProperty("picked").GetRawText() : null;
                if (_choices.TryGetValue(id, out var previous))
                    SourceCaptureSchema.Require(previous.Options == options && (previous.Picked is null || previous.Picked == picked));
                _choices[id] = (options, picked);
                break;
            case "draft_choice":
                SourceCaptureSchema.Require(_captureKind == "draft");
                int pick = d.GetProperty("pick").GetInt32();
                string offered = d.GetProperty("options").GetRawText();
                int? selected = SourceCaptureSchema.NullableNumber(d.GetProperty("picked"));
                if (_draft.TryGetValue(pick, out var old))
                    SourceCaptureSchema.Require(old.Options == offered && (old.Picked is null || old.Picked == selected));
                _draft[pick] = (offered, selected);
                break;
            case "block_start":
                if (_blocks.Count >= 64) throw new SourceCaptureException("limit_exceeded");
                _blocks.Push(d.GetProperty("block").GetString()!);
                if (_blocks.Peek() == "OTHER") { _unsupported++; AddGaps(1); }
                break;
            case "block_end":
                if (_blocks.Count == 0 || _blocks.Peek() != d.GetProperty("block").GetString())
                {
                    SourceCaptureSchema.Require(_gaps > 0);
                    _blocks.Clear();
                    AddGaps(1);
                }
                else _blocks.Pop();
                break;
            case "gap":
                AddGaps(d.GetProperty("count").GetInt32());
                if (d.GetProperty("reason").GetString() == "unsupported") _unsupported++;
                break;
            default: _unsupported++; AddGaps(1); derivedKind = "unsupported"; break;
        }
        _derivedBytes += Encoding.UTF8.GetByteCount(d.GetRawText()) + 256;
        if (_derivedBytes > limits.MaxDerivedBytes) throw new SourceCaptureException("limit_exceeded");
        _events.Add(e with { Kind = derivedKind });
    }

    private void AddGaps(int count)
    {
        if (_gaps > int.MaxValue - count) throw new SourceCaptureException("limit_exceeded");
        _gaps += count;
    }

    private void ObserveRef(int id, string side, string zone)
    {
        bool hidden = SourceCaptureSchema.Hidden(side, zone);
        if (_refs.TryGetValue(id, out bool wasHidden))
            SourceCaptureSchema.Require(wasHidden == hidden); // producer must allocate a fresh ref across conceal/reveal
        else if (_refs.Count >= limits.MaxRecords) throw new SourceCaptureException("limit_exceeded");
        _refs[id] = hidden;
    }

    internal SourceCaptureResult Finish(string hash)
    {
        SourceCaptureSchema.Require(_events.Count > 0 && (_end is not null || _lastKind == "gap"));
        if (_blocks.Count > 0) AddGaps(1);
        bool unresolved = _choices.Values.Any(x => x.Picked is null) || _draft.Values.Any(x => x.Picked is null);
        bool complete = _end is not null && _end.Data.GetProperty("status").GetString() == "complete" &&
            _end.Data.GetProperty("reason").GetString() == "normal" && _gaps == 0 && !unresolved;
        string? result = complete && _captureKind == "game" ? _end!.Data.GetProperty("result").GetString() : null;
        int? placement = complete && _captureKind == "game" ? SourceCaptureSchema.NullableNumber(_end!.Data.GetProperty("placement")) : null;
        bool hasOutcome = _mode switch
        {
            "bg" => placement is >= 1 and <= 8,
            "duos" => placement is >= 1 and <= 4,
            _ => result is "W" or "L" or "T",
        };
        var summary = new SourceCaptureSummary(_plays, _buys, _sells, _choices.Count, _turns, _shop, _resources,
            _draft.Count, _captureKind, _mode, complete ? "complete" : "incomplete", _end is not null,
            complete && _captureKind == "game" && hasOutcome, result, placement);
        return new(1, SourceCaptureParser.ParserVersion, hash, complete ? "parsed" : "partial", _events.Count,
            _unsupported, _gaps, _events.AsReadOnly(), summary);
    }
}
