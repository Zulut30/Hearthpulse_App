namespace Hearthpulse.UI.Main;

public sealed partial record CounterSettingRow
{
    public string AutoKey => Id + "|Auto";
    public string AlwaysKey => Id + "|Always";
    public string OffKey => Id + "|Off";
}
