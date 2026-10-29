using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NotchWin.Services;

/// <summary>Корзина Windows: сколько в ней лежит, удаление файлов в неё, открытие.</summary>
internal static class RecycleBin
{
    public static (long Items, long Bytes) Query()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64NumItems, info.i64Size) : (0, 0);
    }

    /// <summary>Отправляет файлы и папки в корзину (их можно восстановить).</summary>
    public static bool Delete(IEnumerable<string> paths)
    {
        var list = string.Join('\0', paths) + "\0\0";
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 3, // FO_DELETE
            pFrom = list,
            fFlags = 0x40 | 0x10 | 0x4, // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    /// <summary>Очищает корзину без системного окна подтверждения (подтверждение спрашиваем сами).</summary>
    public static void Empty()
    {
        try { SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2); } // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI
        catch { }
    }

    public static void Open()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }); }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
}
