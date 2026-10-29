using System.IO;
using Microsoft.Win32;

namespace NotchWin;

/// <summary>
/// Прячет часы на панели задач Windows, пока программа запущена, и возвращает их при выходе.
/// Исходное значение сохраняется в файл, чтобы вернуть его даже после аварийного завершения.
/// </summary>
internal static class TaskbarTweaks
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ClockValue = "ShowSystrayDateTimeValueName"; // Windows 11 22H2+ (сборка 22621.2361 и новее)

    private static readonly string RestoreFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotchWin", "taskbar-restore.txt");

    private static bool _applied;

    public static void HideTrayClock()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AdvancedKey, writable: true);

            // Если прошлый запуск упал, в файле лежит настоящее исходное значение.
            if (!File.Exists(RestoreFile))
            {
                var original = key.GetValue(ClockValue) is int v ? v.ToString() : "missing";
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RestoreFile)!);
                File.WriteAllText(RestoreFile, original);
            }

            key.SetValue(ClockValue, 0, RegistryValueKind.DWord);
            _applied = true;
            NotifyExplorer();
        }
        catch { /* нет доступа к реестру: часы просто останутся на месте */ }
    }

    /// <summary>Включает часы и дату на панели задач, даже если исходное значение потерялось.</summary>
    public static void ShowTrayClock()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AdvancedKey, writable: true);
            if (key.GetValue(ClockValue) is int v && v == 0)
            {
                key.SetValue(ClockValue, 1, RegistryValueKind.DWord);
                NotifyExplorer();
            }
            if (File.Exists(RestoreFile)) File.Delete(RestoreFile);
            _applied = false;
        }
        catch { }
    }

    public static void Restore()
    {
        if (!_applied && !File.Exists(RestoreFile)) return;
        try
        {
            var original = File.Exists(RestoreFile) ? File.ReadAllText(RestoreFile).Trim() : "1";
            using var key = Registry.CurrentUser.CreateSubKey(AdvancedKey, writable: true);
            if (original == "missing") key.DeleteValue(ClockValue, throwOnMissingValue: false);
            else key.SetValue(ClockValue, int.Parse(original), RegistryValueKind.DWord);
            File.Delete(RestoreFile);
            _applied = false;
            NotifyExplorer();
        }
        catch { }
    }

    private static void NotifyExplorer()
    {
        const int WM_SETTINGCHANGE = 0x001A;
        const uint SMTO_ABORTIFHUNG = 0x0002;
        var HWND_BROADCAST = new IntPtr(0xFFFF);
        ShellNative.SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "TraySettings",
            SMTO_ABORTIFHUNG, 500, out _);
    }
}
