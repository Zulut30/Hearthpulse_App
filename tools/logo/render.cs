#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:property Nullable=enable
// Renders the HearthpulseLogo DrawingImage of a WPF resource dictionary to PNGs (see README.md).
// dotnet run tools/logo/render.cs -- <Logo.xaml> <out.png:width:height:logoHeight:centreX:centreY> ...
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var thread = new Thread(() =>
{
    using var stream = File.OpenRead(args[0]);
    var dictionary = (ResourceDictionary)XamlReader.Load(stream);
    var logo = ((DrawingImage)dictionary["HearthpulseLogo"]).Drawing;
    foreach (var job in args.Skip(1))
    {
        // the last five fields are numbers; the path before them may hold a drive colon
        var p = job.Split(':');
        var path = string.Join(':', p[..^5]);
        double D(int i) => double.Parse(p[p.Length - 5 + i - 1], CultureInfo.InvariantCulture);
        var scale = D(3) / logo.Bounds.Height;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(D(4) - logo.Bounds.Width * scale / 2, D(5) - D(3) / 2));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawDrawing(logo);
        }

        var bitmap = new RenderTargetBitmap((int)D(1), (int)D(2), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
