using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace NotchWin.Services;

/// <summary>
/// Микрофон и звук Discord без его горячих клавиш: глушим аудиопотоки самого процесса Discord
/// в Windows (как ползунок приложения в микшере). Собеседники тебя не слышат, хотя значок
/// микрофона в самом Discord не меняется.
/// </summary>
internal static class DiscordAudio
{
    private static readonly string[] Names = { "discord", "discordptb", "discordcanary", "vesktop" };

    /// <summary>Глушит или включает микрофон Discord. Возвращает false, если потоков Discord не нашлось.</summary>
    public static bool SetMicMuted(bool muted) => Apply(DataFlow.Capture, muted);

    /// <summary>Глушит или включает звук Discord (то, что ты слышишь).</summary>
    public static bool SetSoundMuted(bool muted) => Apply(DataFlow.Render, muted);

    private static bool Apply(DataFlow flow, bool muted)
    {
        var found = false;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        try
                        {
                            var session = sessions[i];
                            if (session.IsSystemSoundsSession || !IsDiscord(session.GetProcessID)) continue;
                            session.SimpleAudioVolume.Mute = muted;
                            found = true;
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
        return found;
    }

    private static bool IsDiscord(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return Names.Contains(process.ProcessName.ToLowerInvariant());
        }
        catch { return false; }
    }
}
