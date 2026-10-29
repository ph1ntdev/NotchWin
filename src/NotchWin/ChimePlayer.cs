using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace NotchWin;

/// <summary>
/// Свой звук окончания таймера: три восходящие ноты колокольчика, дважды.
/// Синтезируется на лету, чтобы его нельзя было спутать со звуками Windows.
/// </summary>
internal static class ChimePlayer
{
    public static void Play()
    {
        Task.Run(() =>
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var mix = device.AudioClient.MixFormat;
                var format = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
                var bytes = Synthesize(format.SampleRate, format.Channels);

                var stream = new RawSourceWaveStream(new MemoryStream(bytes), format);
                var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 80);
                output.PlaybackStopped += (_, _) => { output.Dispose(); stream.Dispose(); device.Dispose(); };
                output.Init(stream);
                output.Play();
            }
            catch
            {
                System.Media.SystemSounds.Asterisk.Play();
            }
        });
    }

    private static byte[] Synthesize(int rate, int channels)
    {
        // Ми, соль, до на две октавы выше первой: светлый «динь-динь-динь».
        double[] notes = { 1318.5, 1568.0, 2093.0 };
        const double noteGap = 0.14, ringTime = 0.9, repeatGap = 0.55;
        var total = (int)(rate * (2 * (noteGap * notes.Length) + repeatGap + ringTime));
        var samples = new float[total];

        for (var rep = 0; rep < 2; rep++)
        {
            for (var n = 0; n < notes.Length; n++)
            {
                var start = (int)(rate * (rep * (noteGap * notes.Length + repeatGap) + n * noteGap));
                var len = (int)(rate * ringTime);
                for (var i = 0; i < len && start + i < total; i++)
                {
                    var t = (double)i / rate;
                    var env = Math.Exp(-t * 5.5) * Math.Min(1, t * 400);         // быстрая атака, мягкое затухание
                    var tone = Math.Sin(2 * Math.PI * notes[n] * t)
                             + 0.35 * Math.Sin(2 * Math.PI * notes[n] * 2.76 * t) * Math.Exp(-t * 9); // обертон колокольчика
                    samples[start + i] += (float)(0.22 * env * tone);
                }
            }
        }

        var bytes = new byte[total * channels * 4];
        for (var i = 0; i < total; i++)
        {
            var s = Math.Clamp(samples[i], -1f, 1f);
            for (var c = 0; c < channels; c++)
                BitConverter.TryWriteBytes(new Span<byte>(bytes, (i * channels + c) * 4, 4), s);
        }
        return bytes;
    }
}
