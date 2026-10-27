namespace Hearthpulse.App.Main;

public sealed partial class MainViewModel
{
    partial void ClearLanguagePlatformCache() => _customPreview = null;
}
