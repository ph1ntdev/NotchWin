using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NotchWin;

/// <summary>Достаёт из обложки яркий "фирменный" цвет для эквалайзера, как это делает iOS.</summary>
internal static class ColorExtractor
{
    public static Color? FromImage(BitmapSource? image)
    {
        if (image == null) return null;
        try
        {
            // Уменьшаем до 24x24 и переводим в BGRA, чтобы быстро пройтись по пикселям.
            var scaled = new TransformedBitmap(image, new ScaleTransform(24.0 / image.PixelWidth, 24.0 / image.PixelHeight));
            var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            var w = bgra.PixelWidth;
            var h = bgra.PixelHeight;
            var pixels = new byte[w * h * 4];
            bgra.CopyPixels(pixels, w * 4, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var pb = pixels[i] / 255.0;
                var pg = pixels[i + 1] / 255.0;
                var pr = pixels[i + 2] / 255.0;
                var max = Math.Max(pr, Math.Max(pg, pb));
                var min = Math.Min(pr, Math.Min(pg, pb));
                var sat = max == 0 ? 0 : (max - min) / max;
                // Насыщенные и не слишком тёмные пиксели важнее серых и чёрных.
                var weight = sat * sat * max + 0.01;
                r += pr * weight; g += pg * weight; b += pb * weight; total += weight;
            }
            if (total <= 0) return null;
            return Brighten(r / total, g / total, b / total);
        }
        catch { return null; }
    }

    /// <summary>Поднимает яркость и насыщенность, чтобы цвет хорошо читался на чёрной челке.</summary>
    private static Color Brighten(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
        }
        if (hue < 0) hue += 360;
        var sat = max == 0 ? 0 : delta / max;
        sat = sat < 0.12 ? sat : Math.Max(sat, 0.55);   // почти серая обложка остаётся светло-серой
        var val = Math.Max(max, 0.9);

        var c = val * sat;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = val - c;
        var (r1, g1, b1) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r1 + m) * 255), (byte)((g1 + m) * 255), (byte)((b1 + m) * 255));
    }
}
