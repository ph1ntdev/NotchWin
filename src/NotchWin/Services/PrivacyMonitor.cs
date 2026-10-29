using Microsoft.Win32;

namespace NotchWin.Services;

/// <summary>
/// Определяет, что камера или микрофон сейчас заняты каким-то приложением.
/// Windows записывает это в реестр (тот же источник, что у значка микрофона на панели задач):
/// у работающего приложения LastUsedTimeStop равен 0.
/// </summary>
internal static class PrivacyMonitor
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    public static (bool Camera, bool Microphone) Check() => (IsInUse("webcam"), IsInUse("microphone"));

    private static bool IsInUse(string capability)
    {
        try
        {
            return Scan(Registry.CurrentUser, Root + capability) || Scan(Registry.LocalMachine, Root + capability);
        }
        catch { return false; }
    }

    private static bool Scan(RegistryKey hive, string path)
    {
        using var key = hive.OpenSubKey(path);
        if (key == null) return false;
        foreach (var name in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(name);
            if (sub == null) continue;
            if (name == "NonPackaged")
            {
                foreach (var app in sub.GetSubKeyNames())
                {
                    using var appKey = sub.OpenSubKey(app);
                    if (appKey != null && IsActive(appKey)) return true;
                }
            }
            else if (IsActive(sub)) return true;
        }
        return false;
    }

    /// <summary>Обычные программы (не из Store), которые прямо сейчас пишут микрофон: имена exe без пути.</summary>
    public static List<string> MicrophoneApps()
    {
        var result = new List<string>();
        try
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var key = hive.OpenSubKey(Root + @"microphone\NonPackaged");
                if (key == null) continue;
                foreach (var app in key.GetSubKeyNames())
                {
                    using var appKey = key.OpenSubKey(app);
                    // Имя ключа — путь к exe, где «\» заменены на «#».
                    if (appKey != null && IsActive(appKey)) result.Add(app[(app.LastIndexOf('#') + 1)..]);
                }
            }
        }
        catch { }
        return result;
    }

    private static bool IsActive(RegistryKey key) =>
        key.GetValue("LastUsedTimeStart") is long start && start > 0 &&
        key.GetValue("LastUsedTimeStop") is long stop && stop == 0;
}
