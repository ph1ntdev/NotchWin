using System.Management;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace NotchWin.Services;

/// <summary>
/// Сообщает об изменении громкости и яркости, чтобы показать их в челке
/// вместо стандартного всплывающего окна Windows.
/// </summary>
internal sealed class VolumeWatcher : IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private string? _deviceId;
    private ManagementEventWatcher? _brightness;
    private readonly System.Threading.Timer _rebind;

    /// <summary>Уровень 0..1 и признак выключенного звука. Вызывается не в UI-потоке.</summary>
    public event Action<float, bool>? VolumeChanged;

    /// <summary>Яркость 0..100. Работает на ноутбуках, у внешних мониторов Windows таких событий не шлёт.</summary>
    public event Action<int>? BrightnessChanged;

    public VolumeWatcher()
    {
        // Устройство по умолчанию может смениться (наушники, колонки) — переподписываемся.
        _rebind = new System.Threading.Timer(_ => Bind(), null, 0, 3000);
        StartBrightness();
    }

    private void Bind()
    {
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (device.ID == _deviceId) { device.Dispose(); return; }

            if (_device != null)
            {
                _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume;
                _device.Dispose();
            }
            _device = device;
            _deviceId = device.ID;
            _device.AudioEndpointVolume.OnVolumeNotification += OnVolume;
        }
        catch { }
    }

    private void OnVolume(AudioVolumeNotificationData data) => VolumeChanged?.Invoke(data.MasterVolume, data.Muted);

    private void StartBrightness()
    {
        try
        {
            _brightness = new ManagementEventWatcher(new ManagementScope(@"root\WMI"),
                new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _brightness.EventArrived += (_, e) =>
            {
                try { BrightnessChanged?.Invoke(Convert.ToInt32(e.NewEvent["Brightness"])); } catch { }
            };
            _brightness.Start();
        }
        catch
        {
            _brightness = null; // настольный ПК: яркость через WMI недоступна
        }
    }

    public void Dispose()
    {
        _rebind.Dispose();
        try { _brightness?.Stop(); _brightness?.Dispose(); } catch { }
        if (_device != null) _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume;
        _device?.Dispose();
        _enumerator?.Dispose();
    }
}

/// <summary>
/// Прячет стандартное окно громкости Windows, пока работает программа (приём из HideVolumeOSD).
/// Работает на Windows 10 и ранних сборках 11. В новых сборках 11 такого окна нет, тогда ничего не делаем.
/// </summary>
internal static class OsdHider
{
    private static IntPtr _osd;

    public static void TryHide()
    {
        if (_osd != IntPtr.Zero) return;
        var hwnd = IntPtr.Zero;
        while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, "NativeHWNDHost", "")) != IntPtr.Zero)
        {
            if (FindWindowEx(hwnd, IntPtr.Zero, "DirectUIHWND", "") != IntPtr.Zero)
            {
                _osd = hwnd;
                ShowWindow(hwnd, 6 /* SW_MINIMIZE */);
                return;
            }
        }
    }

    public static void Restore()
    {
        if (_osd == IntPtr.Zero) return;
        ShowWindow(_osd, 9 /* SW_RESTORE */);
        _osd = IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);
}
