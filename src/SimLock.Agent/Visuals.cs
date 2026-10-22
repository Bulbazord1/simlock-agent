using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SimLock.Core.Qr;

namespace SimLock.Agent;

internal static class Visuals
{
    private static readonly Dictionary<string, ImageSource?> LogoCache = new(StringComparer.Ordinal);

    /// <summary>Dibuja un QR como geometría vectorial (nítido en cualquier tamaño), con zona silenciosa de 4 módulos.</summary>
    public static DrawingImage QrImage(string text)
    {
        var q = QrCode.EncodeText(text, QrEcc.Medium);
        const int quiet = 4;
        var total = q.Size + quiet * 2;

        var dark = new StreamGeometry();
        using (var ctx = dark.Open())
        {
            for (var y = 0; y < q.Size; y++)
            {
                // Une módulos oscuros contiguos de la fila en un solo rectángulo.
                var x = 0;
                while (x < q.Size)
                {
                    if (!q[x, y]) { x++; continue; }
                    var start = x;
                    while (x < q.Size && q[x, y]) x++;
                    AddRect(ctx, start + quiet, y + quiet, x - start, 1);
                }
            }
        }
        dark.Freeze();

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, total, total))));
        group.Children.Add(new GeometryDrawing(Brushes.Black, null, dark));
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static void AddRect(StreamGeometryContext ctx, double x, double y, double w, double h)
    {
        ctx.BeginFigure(new Point(x, y), isFilled: true, isClosed: true);
        ctx.LineTo(new Point(x + w, y), false, false);
        ctx.LineTo(new Point(x + w, y + h), false, false);
        ctx.LineTo(new Point(x, y + h), false, false);
    }

    /// <summary>Color de marca del servidor (#RRGGBB); si viene mal, un verde oscuro neutro.</summary>
    public static Color BrandColor(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color c) return c;
        }
        catch (Exception) { }
        return Color.FromRgb(0x0B, 0x3D, 0x2E);
    }

    /// <summary>Texto claro u oscuro según la luminancia del fondo.</summary>
    public static Brush ForegroundFor(Color bg)
    {
        var lum = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255;
        return lum > 0.55 ? new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)) : Brushes.White;
    }

    /// <summary>Logo desde la URL del branding. Se descarga una vez y queda en memoria (no en disco).</summary>
    public static ImageSource? Logo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;
        if (LogoCache.TryGetValue(url, out var cached)) return cached;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            // Si falla la descarga, se reintenta en otro momento.
            bmp.DownloadFailed += (_, _) => LogoCache.Remove(url);
            LogoCache[url] = bmp;
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
