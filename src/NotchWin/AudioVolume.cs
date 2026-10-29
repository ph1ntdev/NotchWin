using NAudio.CoreAudioApi;

namespace NotchWin;

/// <summary>Громкость основного устройства вывода.</summary>
internal static class AudioVolume
{
    public static (float Level, bool Muted)? Get()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var v = device.AudioEndpointVolume;
            return (v.MasterVolumeLevelScalar, v.Mute);
        }
        catch { return null; }
    }

    public static void Change(float delta)
    {
        var v = Get();
        if (v != null) Set(v.Value.Level + delta);
    }

    public static void Set(float level)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(level, 0f, 1f);
            if (level > 0) device.AudioEndpointVolume.Mute = false;
        }
        catch { }
    }

    public static void ToggleMute()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
        }
        catch { }
    }
}
