using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;

namespace NotchWin;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>Звук одного приложения (все его аудиосессии вместе), как строка в EarTrumpet.</summary>
public sealed record AppAudio(uint ProcessId, string Name, ImageSource? Icon, float Volume, bool Muted);

/// <summary>Микшер: устройства вывода и громкость отдельных приложений (через NAudio).</summary>
internal static class AudioMixer
{
    private static readonly Dictionary<string, ImageSource?> IconCache = new();

    public static List<AudioDevice> GetOutputDevices()
    {
        var result = new List<AudioDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = def.ID;
            }
            catch { }

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                    result.Add(new AudioDevice(device.ID, device.FriendlyName, device.ID == defaultId));
            }
        }
        catch { }
        return result;
    }

    /// <summary>Делает устройство основным для всего (как выбор устройства в EarTrumpet).</summary>
    public static void SetDefaultDevice(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            for (var role = 0; role < 3; role++) policy.SetDefaultEndpoint(id, role);
            Marshal.ReleaseComObject(policy);
        }
        catch { }
    }

    public static List<AppAudio> GetApps()
    {
        var groups = new Dictionary<uint, AppAudio>();
        ForEachSession((pid, isSystem, session) =>
        {
            if (groups.ContainsKey(pid)) return;
            var (name, icon) = isSystem ? ("Системные звуки", null) : DescribeProcess(pid, session.DisplayName);
            groups[pid] = new AppAudio(pid, name, icon, session.SimpleAudioVolume.Volume, session.SimpleAudioVolume.Mute);
        });
        return groups.Values
            .OrderBy(a => a.ProcessId == 0 ? 1 : 0).ThenBy(a => a.Name)
            .ToList();
    }

    public static void SetAppVolume(uint pid, float level)
    {
        ForEachSession((p, _, session) =>
        {
            if (p != pid) return;
            session.SimpleAudioVolume.Volume = Math.Clamp(level, 0f, 1f);
            if (level > 0) session.SimpleAudioVolume.Mute = false;
        });
    }

    public static void ToggleAppMute(uint pid)
    {
        bool? target = null;
        ForEachSession((p, _, session) =>
        {
            if (p != pid) return;
            target ??= !session.SimpleAudioVolume.Mute;
            session.SimpleAudioVolume.Mute = target.Value;
        });
    }

    private static void ForEachSession(Action<uint, bool, AudioSessionControl> action)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                try
                {
                    if (session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                    var isSystem = session.IsSystemSoundsSession;
                    action(isSystem ? 0 : session.GetProcessID, isSystem, session);
                }
                catch { }
            }
        }
        catch { }
    }

    private static (string, ImageSource?) DescribeProcess(uint pid, string? displayName)
    {
        var path = ShellIcons.GetProcessPath(pid);
        var name = !string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith('@') ? displayName : "";
        if (name == "" && path != null)
        {
            try { name = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription ?? ""; } catch { }
            if (string.IsNullOrWhiteSpace(name)) name = System.IO.Path.GetFileNameWithoutExtension(path);
        }
        if (name == "") name = $"Процесс {pid}";

        ImageSource? icon = null;
        if (path != null && !IconCache.TryGetValue(path, out icon))
        {
            icon = ShellIcons.Load(path);
            IconCache[path] = icon;
        }
        return (name, icon);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    /// <summary>Недокументированный интерфейс смены устройства по умолчанию (им же пользуется EarTrumpet).</summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        void GetMixFormat(IntPtr a, IntPtr b);
        void GetDeviceFormat(IntPtr a, int b, IntPtr c);
        void ResetDeviceFormat(IntPtr a);
        void SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        void GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        void SetProcessingPeriod(IntPtr a, IntPtr b);
        void GetShareMode(IntPtr a, IntPtr b);
        void SetShareMode(IntPtr a, IntPtr b);
        void GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        void SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        void SetEndpointVisibility(IntPtr a, int b);
    }
}

/// <summary>Иконки файлов и программ, путь к exe процесса.</summary>
internal static class ShellIcons
{
    public static string? GetProcessPath(uint pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    public static ImageSource? Load(string path)
    {
        var info = new SHFILEINFO();
        var res = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 /* SHGFI_ICON */);
        if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { DestroyIcon(info.hIcon); }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);
}
