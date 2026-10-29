using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NotchWin;

/// <summary>
/// Плавная прокрутка: колесо и тачпад задают цель, а список доезжает до неё за несколько кадров.
/// Шаг меньше стандартного, чтобы одно движение колеса не пролистывало сразу полсписка.
/// </summary>
internal sealed class SmoothScroll
{
    private readonly ScrollViewer _viewer;
    private readonly bool _horizontal;
    private readonly double _factor;
    private readonly Stopwatch _clock = new();
    private double _target;
    private bool _running;

    private SmoothScroll(ScrollViewer viewer, bool horizontal, double factor)
    {
        _viewer = viewer;
        _horizontal = horizontal;
        _factor = factor;
    }

    /// <summary>Подключает к списку. factor — сколько пикселей на единицу колеса (щелчок колеса = 120).</summary>
    public static SmoothScroll Attach(ScrollViewer viewer, bool horizontal, double factor)
    {
        var scroll = new SmoothScroll(viewer, horizontal, factor);
        viewer.PreviewMouseWheel += (_, e) =>
        {
            scroll.ScrollBy(-e.Delta);
            e.Handled = true;
        };
        return scroll;
    }

    private double Offset => _horizontal ? _viewer.HorizontalOffset : _viewer.VerticalOffset;
    private double Max => _horizontal ? _viewer.ScrollableWidth : _viewer.ScrollableHeight;

    /// <summary>Сдвиг в единицах колеса: плюс — вправо/вниз.</summary>
    public void ScrollBy(double wheelDelta)
    {
        if (!_running) _target = Offset;
        _target = Math.Clamp(_target + wheelDelta * _factor, 0, Max);
        if (_running) return;
        _running = true;
        _clock.Restart();
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var dt = Math.Min(_clock.Elapsed.TotalSeconds, 0.05);
        _clock.Restart();
        var current = Offset;
        var diff = _target - current;
        // Экспоненциальное приближение: быстро в начале, мягко в конце, не зависит от частоты кадров.
        var next = Math.Abs(diff) < 0.5 ? _target : current + diff * (1 - Math.Exp(-dt * 16));
        if (_horizontal) _viewer.ScrollToHorizontalOffset(next);
        else _viewer.ScrollToVerticalOffset(next);
        if (next != _target) return;
        CompositionTarget.Rendering -= OnFrame;
        _running = false;
    }
}
