using System.IO;
using System.Runtime.InteropServices;

namespace NotchWin.Services;

public sealed record DriveState(string Root, string Label, long Free, long Total);

/// <summary>Подключённые флешки и внешние диски: что видно и как безопасно извлечь.</summary>
internal static class DriveService
{
    public static DriveState? Read(string root)
    {
        try
        {
            var d = new DriveInfo(root);
            if (!d.IsReady) return null;
            var label = string.IsNullOrWhiteSpace(d.VolumeLabel)
                ? (d.DriveType == DriveType.Removable ? "USB-накопитель" : "Диск")
                : d.VolumeLabel;
            return new DriveState(d.RootDirectory.FullName, $"{label} ({d.Name.TrimEnd('\\')})", d.AvailableFreeSpace, d.TotalSize);
        }
        catch { return null; }
    }

    /// <summary>Сменные носители, подключённые сейчас (для запуска программы, когда флешка уже вставлена).</summary>
    public static List<DriveState> Removable() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable)
            .Select(d => Read(d.Name)).OfType<DriveState>().ToList();

    public static string Size(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.#} ГБ"
        : $"{bytes / (double)(1L << 20):0} МБ";

    /// <summary>
    /// Безопасное извлечение: закрываем том, отключаем его и просим устройство извлечься.
    /// Не получится, если с диска открыт какой-то файл.
    /// </summary>
    public static bool Eject(string root)
    {
        var letter = root.TrimEnd('\\', ':');
        var handle = CreateFile($@"\\.\{letter}:", 0xC0000000 /* GENERIC_READ | GENERIC_WRITE */, 3 /* share read/write */,
            IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return false;
        try
        {
            var locked = false;
            for (var i = 0; i < 10 && !locked; i++)
            {
                locked = DeviceIoControl(handle, 0x00090018 /* FSCTL_LOCK_VOLUME */, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                if (!locked) Thread.Sleep(100);
            }
            if (!locked) return false;
            DeviceIoControl(handle, 0x00090020 /* FSCTL_DISMOUNT_VOLUME */, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            var allow = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(allow, 0); // PreventMediaRemoval = false
                DeviceIoControl(handle, 0x002D4804 /* IOCTL_STORAGE_MEDIA_REMOVAL */, allow, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
            }
            finally { Marshal.FreeHGlobal(allow); }
            return DeviceIoControl(handle, 0x002D4808 /* IOCTL_STORAGE_EJECT_MEDIA */, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint code, IntPtr inBuffer, int inSize, IntPtr outBuffer, int outSize,
        out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
