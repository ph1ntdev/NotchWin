using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NotchWin;

/// <summary>
/// Горизонтальная шкала минут для таймера: крутите колесо или тяните мышью,
/// значение под оранжевой меткой по центру — длительность таймера.
/// </summary>
public sealed class TimeRuler : FrameworkElement
{
    private const double Spacing = 22;   // пикселей на минуту: шире шаг — спокойнее шкала
    private const double SecondSpacing = 2.4; // пока таймер идёт: пикселей на секунду, чтобы движение было видно
    public int Minimum { get; set; } = 1;
    public int Maximum { get; set; } = 180;

    /// <summary>Отображаемое (анимируемое, дробное) положение шкалы.</summary>
    public static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        nameof(Shown), typeof(double), typeof(TimeRuler),
        new FrameworkPropertyMetadata(5.0, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, e) => ((TimeRuler)d).PreviewChanged?.Invoke((int)Math.Round((double)e.NewValue))));

    /// <summary>Минуты под меткой прямо во время прокрутки или перетаскивания (для живого обновления времени).</summary>
    public event Action<int>? PreviewChanged;

    public double Shown
    {
        get => (double)GetValue(ShownProperty);
        set => SetValue(ShownProperty, value);
    }

    private int _value = 5;
    private bool _dragging;
    private double _dragStartX, _dragStartValue;

    /// <summary>Выбранное число минут.</summary>
    public int Value
    {
        get => _value;
        set => SetValue(value, animate: true, notify: false);
    }

    public event Action<int>? ValueChanged;

    // ---------- Режим «идёт время» ----------
    // Пока таймер запущен, шкала показывает секунды и плавно едет к нулю.

    private bool _following;
    private double _followSeconds;

    public static readonly DependencyProperty SecondsProperty = DependencyProperty.Register(
        nameof(Seconds), typeof(double), typeof(TimeRuler),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Оставшиеся секунды под меткой (анимируется между тиками таймера).</summary>
    public double Seconds
    {
        get => (double)GetValue(SecondsProperty);
        set => SetValue(SecondsProperty, value);
    }

    public void Follow(TimeSpan remaining)
    {
        var target = Math.Max(0, remaining.TotalSeconds);
        if (!_following)
        {
            _following = true;
            BeginAnimation(SecondsProperty, null);
            Seconds = target;
        }
        else if (Math.Abs(target - _followSeconds) > 0.01)
        {
            // Таймер тикает 4 раза в секунду; между тиками доезжаем линейно — движение непрерывное.
            var jump = Math.Abs(target - Seconds) > 5;
            BeginAnimation(SecondsProperty, jump ? null : new DoubleAnimation(target, TimeSpan.FromMilliseconds(260)));
            if (jump) Seconds = target;
        }
        _followSeconds = target;
        InvalidateVisual();
    }

    public void StopFollowing()
    {
        if (!_following) return;
        _following = false;
        BeginAnimation(SecondsProperty, null);
        SetValue(_value, animate: true, notify: false);
        InvalidateVisual();
    }

    public TimeRuler()
    {
        Height = 44;
        Cursor = Cursors.SizeWE;
        Focusable = false;
    }

    private void SetValue(int value, bool animate, bool notify)
    {
        value = Math.Clamp(value, Minimum, Maximum);
        var changed = value != _value;
        _value = value;
        if (animate)
        {
            BeginAnimation(ShownProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        else
        {
            BeginAnimation(ShownProperty, null);
            Shown = value;
        }
        if (changed && notify) ValueChanged?.Invoke(value);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        SetValue(_value + (e.Delta > 0 ? -1 : 1), animate: true, notify: true);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStartX = e.GetPosition(this).X;
        _dragStartValue = Shown;
        BeginAnimation(ShownProperty, null);
        Shown = _dragStartValue;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        var dx = e.GetPosition(this).X - _dragStartX;
        Shown = Math.Clamp(_dragStartValue - dx / Spacing, Minimum, Maximum);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        // Отпустили: шкала доезжает до ближайшей целой минуты.
        SetValue((int)Math.Round(Shown), animate: true, notify: true);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // чтобы ловить мышь по всей площади

        var center = w / 2;
        if (_following)
        {
            RenderSeconds(dc, w, h, center);
            return;
        }
        var shown = Shown;
        var half = w / 2 / Spacing + 1;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal,
            FontWeights.SemiBold, FontStretches.Normal);

        for (var m = (int)Math.Floor(shown - half); m <= (int)Math.Ceiling(shown + half); m++)
        {
            if (m < Minimum || m > Maximum) continue;
            var x = center + (m - shown) * Spacing;
            // Чем дальше от центра, тем прозрачнее, шкала плавно уходит за края.
            var fade = Math.Clamp(1 - Math.Abs(x - center) / center, 0, 1);
            var major = m % 5 == 0 || m == 1;
            var brush = new SolidColorBrush(Color.FromArgb((byte)(255 * fade * (major ? 0.9 : 0.45)), 255, 255, 255));
            brush.Freeze();
            var tickH = major ? 12 : 6;
            dc.DrawRoundedRectangle(brush, null, new Rect(x - 1, 4 + (12 - tickH), 2, tickH), 1, 1);

            if (major)
            {
                var text = new FormattedText(m.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, 11, brush, dpi);
                dc.DrawText(text, new Point(x - text.Width / 2, 22));
            }
        }

        DrawMarker(dc, center);
    }

    private static void DrawMarker(DrawingContext dc, double center)
    {
        // Метка выбора по центру.
        var accent = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
        accent.Freeze();
        dc.DrawRoundedRectangle(accent, null, new Rect(center - 1.5, 0, 3, 20), 1.5, 1.5);
    }

    /// <summary>Шкала секунд: деление каждые 5 секунд, подписи на целых минутах, ноль — конец.</summary>
    private void RenderSeconds(DrawingContext dc, double w, double h, double center)
    {
        var seconds = Seconds;
        var half = w / 2 / SecondSpacing + 5;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal,
            FontWeights.SemiBold, FontStretches.Normal);

        var first = (int)Math.Floor((seconds - half) / 5) * 5;
        for (var s = Math.Max(0, first); s <= seconds + half; s += 5)
        {
            var x = center + (s - seconds) * SecondSpacing;
            var fade = Math.Clamp(1 - Math.Abs(x - center) / center, 0, 1);
            var major = s % 60 == 0;
            // Пройденное время (правее метки после старта) бледнее — видно, что шкала уезжает.
            var passed = s > seconds;
            var alpha = fade * (major ? 0.9 : 0.4) * (passed ? 0.5 : 1);
            var brush = new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), 255, 255, 255));
            brush.Freeze();
            var tickH = major ? 12 : s % 15 == 0 ? 8 : 5;
            dc.DrawRoundedRectangle(brush, null, new Rect(x - 1, 4 + (12 - tickH), 2, tickH), 1, 1);
            if (major)
            {
                var text = new FormattedText((s / 60).ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, 11, brush, dpi);
                dc.DrawText(text, new Point(x - text.Width / 2, 22));
            }
        }
        DrawMarker(dc, center);
    }
}
