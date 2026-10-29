using System.Runtime.InteropServices;

namespace NotchWin;

/// <summary>Мониторы: на каком показывать панель и челку (настройка «Монитор»).</summary>
internal static class Screens
{
    public sealed record Screen(IntPtr Handle, string Device, NativeMethods.RECT Bounds, NativeMethods.RECT Work, bool Primary);

    public static List<Screen> All()
    {
        var list = new List<Screen>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitor, ref info))
                list.Add(new Screen(monitor, info.szDevice, info.rcMonitor, info.rcWork, (info.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        // Слева направо, как мониторы стоят на столе.
        return list.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToList();
    }

    /// <summary>Монитор из настроек, а если его отключили — основной.</summary>
    public static IntPtr Target()
    {
        var wanted = AppSettings.Current.Monitor;
        if (!string.IsNullOrEmpty(wanted))
        {
            var screen = All().FirstOrDefault(s => s.Device == wanted);
            if (screen != null) return screen.Handle;
        }
        return NativeMethods.MonitorFromPoint(new NativeMethods.POINT(), NativeMethods.MONITOR_DEFAULTTOPRIMARY);
    }

    /// <summary>Рабочая область выбранного монитора в DIP (для окна поиска).</summary>
    public static System.Windows.Rect WorkAreaDip()
    {
        var handle = Target();
        var screen = All().FirstOrDefault(s => s.Handle == handle);
        if (screen == null) return System.Windows.SystemParameters.WorkArea;
        var scale = NativeMethods.GetDpiForMonitor(handle, NativeMethods.MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
        var r = screen.Work;
        return new System.Windows.Rect(r.Left / scale, r.Top / scale, (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale);
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public NativeMethods.RECT rcMonitor;
        public NativeMethods.RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
}

/// <summary>Автозапуск через HKCU\...\Run.</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "NotchWin";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe) key.SetValue(Name, $"\"{exe}\"");
            else if (!enabled) key.DeleteValue(Name, false);
        }
        catch { }
    }
}
