namespace Hearthpulse.App.Main;

public sealed partial class MainViewModel
{
    /// <summary>Your most recent hero, shown as faded art behind the page titles.</summary>
    public string? BannerHero => Recent.FirstOrDefault(g => g.HeroCardId.Length > 0)?.HeroCardId;
}
