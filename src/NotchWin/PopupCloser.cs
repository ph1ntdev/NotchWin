using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace NotchWin;

/// <summary>Как появляется окно: выпадает сверху или «вырастает» из своего левого верхнего угла.</summary>
internal enum PopupMotion { Drop, Grow }

/// <summary>
/// Плавное открытие и закрытие выпадающих окон панели. Закрывает окно, когда кликнули
/// в любом другом месте экрана или переключились на другое окно. Обычный StaysOpen=False
/// этого не умеет надёжно и закрывает без анимации.
/// </summary>
internal sealed class PopupCloser
{
    private readonly Popup _popup;
    private readonly FrameworkElement _card;
    private readonly FrameworkElement _anchor;
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly TranslateTransform _shift = new();
    private readonly ScaleTransform _scale = new();
    private readonly PopupMotion _motion;
    private IntPtr _foreground;
    private bool _wasPressed;
    private bool _closing;

    public event Action? Closed;
    /// <summary>Пока возвращает true, окно не закрывается (например, открыто меню программы из трея).</summary>
    public Func<bool>? Hold;
    public bool IsOpen => _popup.IsOpen && !_closing;

    public PopupCloser(Popup popup, FrameworkElement card, FrameworkElement anchor, PopupMotion motion = PopupMotion.Drop)
    {
        _popup = popup;
        _card = card;
        _anchor = anchor;
        _motion = motion;
        _popup.StaysOpen = true;
        _popup.PopupAnimation = PopupAnimation.None;
        if (card is System.Windows.Controls.Border border) Glass.Attach(popup, border);
        if (motion == PopupMotion.Grow)
        {
            _card.RenderTransformOrigin = new Point(0, 0);
            _card.RenderTransform = _scale;
        }
        else _card.RenderTransform = _shift;
        _watch.Tick += (_, _) => Watch();
    }

    public void Toggle(Action? beforeOpen = null)
    {
        if (IsOpen) Close();
        else { beforeOpen?.Invoke(); Open(); }
    }

    public void Open()
    {
        _closing = false;
        _popup.IsOpen = true;
        Animate(1, 0, 180, null);
        _foreground = GetForegroundWindow();
        _wasPressed = AnyButtonPressed();
        _watch.Start();
    }

    public void Close()
    {
        if (!_popup.IsOpen || _closing) return;
        _closing = true;
        _watch.Stop();
        Animate(0, -6, 140, () =>
        {
            if (!_closing) return; // за время анимации окно открыли снова
            _popup.IsOpen = false;
            _closing = false;
            Closed?.Invoke();
        });
    }

    // ---------- Плавность самого окна ----------
    // У «стеклянного» окна нельзя сделать прозрачный фон, поэтому на время анимации делаем окно
    // слоистым и меняем его общую прозрачность, а в покое возвращаем обычный режим (иначе пропадёт размытие).

    private IntPtr Hwnd => PresentationSource.FromVisual(_popup.Child) is System.Windows.Interop.HwndSource s ? s.Handle : IntPtr.Zero;

    private void FadeWindow(bool show, int ms)
    {
        var hwnd = Hwnd;
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLong(hwnd, -20);
        SetWindowLong(hwnd, -20, ex | 0x80000 /* WS_EX_LAYERED */);
        SetLayeredWindowAttributes(hwnd, 0, show ? (byte)0 : (byte)255, 2 /* LWA_ALPHA */);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
            var eased = 1 - Math.Pow(1 - t, 3);
            var a = show ? eased : 1 - eased;
            SetLayeredWindowAttributes(hwnd, 0, (byte)(255 * a), 2);
            if (t < 1) return;
            CompositionTarget.Rendering -= tick;
            if (show) SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) & ~0x80000);
        };
        CompositionTarget.Rendering += tick;
    }

    private void Animate(double opacity, double y, int ms, Action? done)
    {
        FadeWindow(opacity > 0, ms);
        var duration = TimeSpan.FromMilliseconds(ms);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (opacity > 0) { _card.Opacity = 0; _shift.Y = -6; }
        var fade = new DoubleAnimation(opacity, duration) { EasingFunction = ease };
        if (done != null) fade.Completed += (_, _) => done();
        _card.BeginAnimation(UIElement.OpacityProperty, fade);
        if (_motion == PopupMotion.Grow)
        {
            // Содержимое растёт из угла, где остался значок, которым окно открыли.
            var from = opacity > 0 ? 0.88 : 1;
            var to = opacity > 0 ? 1 : 0.92;
            var grow = new DoubleAnimation(from, to, duration) { EasingFunction = ease };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        else _shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, duration) { EasingFunction = ease });
    }

    private void Watch()
    {
        // Переключились на другое окно (Alt+Tab, клик по панели задач и т.п.).
        var fg = GetForegroundWindow();
        if (Hold?.Invoke() == true)
        {
            _foreground = fg;
            _wasPressed = AnyButtonPressed();
            return;
        }
        if (fg != _foreground)
        {
            GetWindowThreadProcessId(fg, out var pid);
            if (pid != Environment.ProcessId) { Close(); return; }
            _foreground = fg; // это наше же окно (сама челка или панель), не повод закрываться
        }

        var pressed = AnyButtonPressed();
        if (pressed && !_wasPressed && GetCursorPos(out var p))
        {
            // Смотрим, какое окно реально под курсором: само всплывающее окно — не закрываемся,
            // окно со значком-якорем — только если клик по самому значку, всё остальное — закрываемся.
            var under = GetAncestor(WindowFromPoint(p), 2 /* GA_ROOT */);
            var point = new Point(p.X, p.Y);
            var onPopup = under != IntPtr.Zero && under == Hwnd;
            if (!onPopup && !Contains(_anchor, point)) Close();
        }
        _wasPressed = pressed;
    }

    private static bool Contains(FrameworkElement element, Point screen)
    {
        if (!element.IsVisible || PresentationSource.FromVisual(element) == null) return false;
        var tl = element.PointToScreen(new Point(0, 0));
        var br = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return screen.X >= tl.X && screen.X <= br.X && screen.Y >= tl.Y && screen.Y <= br.Y;
    }

    private static bool AnyButtonPressed() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
}
