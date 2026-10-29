using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace NotchWin;

/// <summary>
/// Размытый фон верхней панели. Панель — AppBar, развёрнутые окна начинаются под ней, поэтому за ней
/// всегда обои. Мы сами размываем верхнюю полосу обоев: такой фон не зависит от DWM и не пропадает
/// при перелистывании рабочих столов, в отличие от системного размытия.
/// </summary>
internal static class WallpaperBackdrop
{
    // Затемнение поверх размытых обоев: подобрано под прежнее системное размытие панели.
    // Затемнение берётся из настроек (по умолчанию 55%, это прежние 0x8C).
    private static Color Tint => Color.FromArgb((byte)Math.Round(Math.Clamp(AppSettings.Current.BarDim, 0, 100) * 2.55), 0x26, 0x26, 0x26);
    private const int PadBelowPx = 48;  // захватываем немного обоев под панелью, чтобы размытие было «глубже»
    private const int Downscale = 4;    // размываем в уменьшенном виде: быстро и мягко

    /// <summary>Ключ текущих обоев (путь, время изменения, стиль): по нему понимаем, что пора перерисовать.</summary>
    public static string CurrentKey()
    {
        var path = WallpaperPath();
        var time = path != null && File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        return $"{path}|{time}|{Style()}|{SolidColor()}|{AppSettings.Current.BarDim}";
    }

    /// <summary>Кисть для фона панели размером monitorW×barH пикселей. null — обои прочитать не вышло.</summary>
    public static Brush? Create(int monitorW, int monitorH, int barH)
    {
        if (monitorW <= 0 || monitorH <= 0 || barH <= 0) return null;
        var path = WallpaperPath();
        if (path == null || !File.Exists(path))
            return new SolidColorBrush(Blend(SolidColor(), Tint));

        BitmapSource source;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = Math.Min(monitorW, 2560);
            bmp.EndInit();
            bmp.Freeze();
            source = bmp;
        }
        catch { return null; }

        // Какой кусок картинки оказался вверху экрана. «Растянуть» — отдельно по осям, остальное считаем «заполнить».
        double iw = source.PixelWidth, ih = source.PixelHeight;
        var stripH = barH + PadBelowPx;
        double sx, sy, offX, offY;
        if (Style() == 2)
        {
            sx = monitorW / iw; sy = monitorH / ih; offX = 0; offY = 0;
        }
        else
        {
            sx = sy = Math.Max(monitorW / iw, monitorH / ih);
            offX = (iw * sx - monitorW) / 2;
            offY = (ih * sy - monitorH) / 2;
        }
        var rect = new Int32Rect(
            (int)Math.Clamp(offX / sx, 0, iw - 1), (int)Math.Clamp(offY / sy, 0, ih - 1), 0, 0);
        rect.Width = (int)Math.Clamp(monitorW / sx, 1, iw - rect.X);
        rect.Height = (int)Math.Clamp(stripH / sy, 1, ih - rect.Y);
        var crop = new CroppedBitmap(source, rect);

        // Рисуем полосу уменьшенной, с размытием и затемнением. Картинку растягиваем чуть шире краёв,
        // чтобы размытие не давало прозрачной каймы.
        var w = Math.Max(1, monitorW / Downscale);
        var h = Math.Max(1, stripH / Downscale);
        const double margin = 12;
        var canvas = new Canvas { Width = w, Height = h, ClipToBounds = true };
        var image = new Image
        {
            Source = crop, Stretch = Stretch.Fill, Width = w + 2 * margin, Height = h + 2 * margin,
            Effect = new BlurEffect { Radius = 9, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality },
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(image, -margin);
        Canvas.SetTop(image, -margin);
        canvas.Children.Add(image);
        canvas.Children.Add(new Rectangle { Width = w, Height = h, Fill = new SolidColorBrush(Tint) });
        canvas.Measure(new Size(w, h));
        canvas.Arrange(new Rect(0, 0, w, h));
        canvas.UpdateLayout();

        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(canvas);
        target.Freeze();

        // Показываем только ту часть, что под самой панелью.
        var brush = new ImageBrush(target)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewbox = new Rect(0, 0, 1, (double)barH / stripH),
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        brush.Freeze();
        return brush;
    }

    private static string? WallpaperPath()
    {
        var sb = new StringBuilder(520);
        if (!SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, sb.Capacity, sb, 0)) return null;
        var path = sb.ToString();
        return path.Length == 0 ? null : path;
    }

    private static int Style()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return int.TryParse(key?.GetValue("WallpaperStyle") as string, out var v) ? v : 10;
        }
        catch { return 10; }
    }

    private static Color SolidColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
            var parts = (key?.GetValue("Background") as string)?.Split(' ');
            if (parts is { Length: 3 })
                return Color.FromRgb(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
        }
        catch { }
        return Colors.Black;
    }

    private static Color Blend(Color under, Color over)
    {
        var a = over.A / 255.0;
        byte Mix(byte u, byte o) => (byte)Math.Round(o * a + u * (1 - a));
        return Color.FromRgb(Mix(under.R, over.R), Mix(under.G, over.G), Mix(under.B, over.B));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(int action, int param, StringBuilder value, int winIni);
}
