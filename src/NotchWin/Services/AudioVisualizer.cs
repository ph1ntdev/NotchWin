using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace NotchWin.Services;

/// <summary>
/// Слушает то, что сейчас играет в системе (WASAPI loopback), и раскладывает звук
/// на полосы частот для эквалайзера. Работает только пока включён, чтобы не тратить CPU.
/// </summary>
internal sealed class AudioVisualizer : IDisposable
{
    public const int BandCount = 7;

    private const int FftLength = 2048;            // ~43 мс при 48 кГц
    private const int FftPower = 11;               // 2^11 = 2048
    private readonly float[] _ring = new float[FftLength];
    private readonly Complex[] _fft = new Complex[FftLength];
    private readonly float[] _bands = new float[BandCount];
    private readonly object _lock = new();
    private int _ringPos;
    private float _peak = 1e-4f;

    private WasapiLoopbackCapture? _capture;
    private int[] _bandEdges = Array.Empty<int>();

    public bool IsRunning => _capture != null;

    public void Start()
    {
        if (_capture != null) return;
        try
        {
            _capture = new WasapiLoopbackCapture();
            BuildBandEdges(_capture.WaveFormat.SampleRate);
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, _) => { };
            _capture.StartRecording();
        }
        catch
        {
            Stop();
        }
    }

    public void Stop()
    {
        var c = _capture;
        _capture = null;
        if (c == null) return;
        try { c.DataAvailable -= OnData; c.StopRecording(); } catch { }
        try { c.Dispose(); } catch { }
        lock (_lock) Array.Clear(_bands);
    }

    /// <summary>Текущие уровни полос 0..1 (от низких частот к высоким).</summary>
    public void ReadBands(float[] target)
    {
        lock (_lock) Array.Copy(_bands, target, BandCount);
    }

    private void BuildBandEdges(int sampleRate)
    {
        // Полосы в логарифмической шкале от 45 Гц до 14 кГц: так бочка и бас видны отдельно от вокала.
        const double lo = 45, hi = 14000;
        _bandEdges = new int[BandCount + 1];
        for (var i = 0; i <= BandCount; i++)
        {
            var f = lo * Math.Pow(hi / lo, (double)i / BandCount);
            _bandEdges[i] = Math.Clamp((int)(f * FftLength / sampleRate), 1, FftLength / 2 - 1);
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        if (capture == null || e.BytesRecorded == 0) return;
        var format = capture.WaveFormat;
        var channels = format.Channels;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var frames = e.BytesRecorded / 4 / channels;
            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++)
                    sum += BitConverter.ToSingle(e.Buffer, (f * channels + c) * 4);
                Push(sum / channels);
            }
        }
        else if (format.BitsPerSample == 16)
        {
            var frames = e.BytesRecorded / 2 / channels;
            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++)
                    sum += BitConverter.ToInt16(e.Buffer, (f * channels + c) * 2) / 32768f;
                Push(sum / channels);
            }
        }
        else return;

        Analyze();
    }

    private void Push(float sample)
    {
        _ring[_ringPos] = sample;
        _ringPos = (_ringPos + 1) % FftLength;
    }

    private void Analyze()
    {
        for (var i = 0; i < FftLength; i++)
        {
            var s = _ring[(_ringPos + i) % FftLength];
            _fft[i].X = (float)(s * FastFourierTransform.HannWindow(i, FftLength));
            _fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftPower, _fft);

        Span<float> raw = stackalloc float[BandCount];
        float frameMax = 0;
        for (var b = 0; b < BandCount; b++)
        {
            float max = 0;
            for (var k = _bandEdges[b]; k <= Math.Max(_bandEdges[b], _bandEdges[b + 1] - 1); k++)
            {
                var mag = MathF.Sqrt(_fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y);
                if (mag > max) max = mag;
            }
            // У музыки высокие частоты тише низких: подтягиваем их, чтобы правые полосы тоже жили.
            raw[b] = max * (1f + b * 0.9f);
            frameMax = Math.Max(frameMax, raw[b]);
        }

        // Автоусиление: подстраиваемся под громкость трека, медленно забывая пики.
        _peak = Math.Max(_peak * 0.995f, frameMax);
        lock (_lock)
        {
            for (var b = 0; b < BandCount; b++)
                _bands[b] = frameMax < 1e-5f ? 0 : MathF.Sqrt(Math.Clamp(raw[b] / _peak, 0, 1));
        }
    }

    public void Dispose() => Stop();
}
