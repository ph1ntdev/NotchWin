using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NotchWin.Services;

/// <summary>
/// Значки файлов и приложений (в том числе из Microsoft Store) через оболочку Windows.
/// Грузятся в отдельном фоновом потоке, чтобы поиск не подтормаживал, и кешируются.
/// </summary>
internal static class ShellImage
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly BlockingCollection<(string Key, string Path, int Size, int Flags, Action<ImageSource?> Done)> Queue = new();

    static ShellImage()
    {
        // Обработчики значков оболочки ждут однопоточную квартиру COM (STA).
        var worker = new Thread(() =>
        {
            foreach (var (key, path, size, flags, done) in Queue.GetConsumingEnumerable())
            {
                if (!Cache.TryGetValue(key, out var image))
                {
                    image = Load(path, size, flags);
                    Cache[key] = image;
                }
                done(image);
            }
        }) { IsBackground = true, Name = "ShellImage" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    /// <summary>Отдаёт значок сразу, если он уже в кеше, иначе вызывает done в UI-потоке, когда загрузит.</summary>
    public static ImageSource? Get(string parsingName, Action<ImageSource?> done)
    {
        if (Cache.TryGetValue(parsingName, out var cached)) return cached;
        var dispatcher = Application.Current.Dispatcher;
        // SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK: только значок, без миниатюр (они медленные)
        Queue.Add((parsingName, parsingName, 64, 0x4 | 0x1, img => dispatcher.BeginInvoke(() => done(img))));
        return null;
    }

    /// <summary>
    /// Миниатюра файла, как в Проводнике: картинка, кадр видео, первая страница PDF.
    /// Для файлов без миниатюры — обычный значок. Ключ кеша включает время изменения файла.
    /// </summary>
    public static ImageSource? GetThumbnail(string path, int size, Action<ImageSource?> done)
    {
        long stamp = 0;
        try { stamp = System.IO.File.GetLastWriteTimeUtc(path).Ticks; } catch { }
        var key = $"thumb{size}|{stamp}|{path}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var dispatcher = Application.Current.Dispatcher;
        Queue.Add((key, path, size, 0x1 /* SIIGBF_BIGGERSIZEOK */, img => dispatcher.BeginInvoke(() => done(img))));
        return null;
    }

    private static ImageSource? Load(string parsingName, int size, int flags)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory);
            if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            return ToBitmap(hbm);
        }
        catch { return null; }
        finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
    }

    private static BitmapSource? ToBitmap(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, // минус: строки сверху вниз
            biPlanes = 1, biBitCount = 32,
        };
        var pixels = new byte[w * h * 4];
        var dc = GetDC(IntPtr.Zero);
        try { if (GetDIBits(dc, hbm, 0, (uint)h, pixels, ref header, 0) == 0) return null; }
        finally { ReleaseDC(IntPtr.Zero, dc); }

        // Старые значки без прозрачности приходят с нулевой альфой — делаем их непрозрачными.
        var anyAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
        // место под таблицу цветов, которую GetDIBits может дописать
        private uint _c0, _c1, _c2, _c3;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr obj, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
