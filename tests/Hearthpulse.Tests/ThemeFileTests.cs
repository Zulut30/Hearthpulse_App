using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Xml.Linq;

namespace Hearthpulse.Tests;

/// <summary>
/// The theme files load: two branches can each add an implicit style for the same control in different places of
/// Theme.xaml, git merges them without a conflict, and WPF then refuses the whole dictionary at start-up.
/// </summary>
public sealed class ThemeFileTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string RepoFile(string relative, [CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", relative));

    [Fact]
    public void Wpf_theme_has_one_implicit_style_per_control_and_unique_keys()
    {
        var root = XDocument.Load(RepoFile("src/Hearthpulse.App/Themes/Theme.xaml")).Root!;
        var resources = root.Elements().ToList();

        var implicitStyles = resources
            .Where(e => e.Name.LocalName == "Style" && e.Attribute(X + "Key") is null)
            .Select(e => (string?)e.Attribute("TargetType") ?? "?");
        Assert.Empty(implicitStyles.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key));

        var keys = resources.Select(e => (string?)e.Attribute(X + "Key")).OfType<string>();
        Assert.Empty(keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    [Fact]
    public void Avalonia_theme_has_unique_resource_keys()
    {
        var root = XDocument.Load(RepoFile("src/Hearthpulse.UI/Themes/Theme.axaml")).Root!;
        var keys = root.Descendants().Where(e => e.Parent?.Name.LocalName == "ResourceDictionary")
            .Select(e => (string?)e.Attribute(X + "Key")).OfType<string>();
        Assert.Empty(keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    /// <summary>
    /// Parses Theme.xaml with WPF's XamlReader, as ThemeEngine does at start-up (the test project is cross-platform,
    /// so WPF is loaded from the Windows Desktop runtime next to the running .NET).
    /// </summary>
    [WindowsFact]
    public void Wpf_theme_parses_with_the_xaml_reader()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var xaml = File.ReadAllText(RepoFile("src/Hearthpulse.App/Themes/Theme.xaml"));
        var wpf = new WindowsDesktopContext(WindowsDesktopRuntime());

        Exception? failure = null;
        object? parsed = null;
        var thread = new Thread(() =>
        {
            try
            {
                var reader = wpf.LoadFromAssemblyName(new AssemblyName("PresentationFramework")).GetType("System.Windows.Markup.XamlReader", throwOnError: true)!;
                parsed = reader.GetMethod("Parse", [typeof(string)])!.Invoke(null, [xaml]);
            }
            catch (TargetInvocationException ex)
            {
                failure = ex.InnerException;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.Equal("ResourceDictionary", parsed?.GetType().Name);
    }

    /// <summary>
    /// WPF from the Windows Desktop runtime in a context of its own: the base runtime carries a WindowsBase facade of
    /// another version that the default context would hand to PresentationFramework.
    /// </summary>
    private sealed class WindowsDesktopContext(string folder) : AssemblyLoadContext("wpf-theme-test")
    {
        protected override Assembly? Load(AssemblyName name) =>
            Path.Combine(folder, name.Name + ".dll") is var path && File.Exists(path) ? LoadFromAssemblyPath(path) : null;

        protected override IntPtr LoadUnmanagedDll(string name)
        {
            var path = Path.Combine(folder, name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll");
            return File.Exists(path) ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }

    /// <summary>shared/Microsoft.WindowsDesktop.App/&lt;version&gt; of the .NET running the tests (same major version).</summary>
    private static string WindowsDesktopRuntime()
    {
        var core = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        var shared = core.Parent!.Parent!;
        var major = core.Name.Split('.')[0] + ".";
        var desktop = new DirectoryInfo(Path.Combine(shared.FullName, "Microsoft.WindowsDesktop.App"))
            .GetDirectories(major + "*")
            .OrderByDescending(d => Version.TryParse(d.Name.Split('-')[0], out var v) ? v : new Version())
            .FirstOrDefault();
        return desktop?.FullName ?? throw new InvalidOperationException($"No Windows Desktop runtime {major}x next to {core.FullName}");
    }
}
