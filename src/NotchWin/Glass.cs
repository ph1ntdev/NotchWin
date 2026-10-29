using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace NotchWin;

/// <summary>
/// «Стекло» для всплывающих окон: фон под окном размывается (акрил), углы скругляются (Windows 11).
/// Если система размытие не поддерживает, карточка просто остаётся тёмной и непрозрачной.
/// </summary>
internal static class Glass
{
    // Тёмный полупрозрачный тон размытия, AABBGGRR.
    private const uint Tint = 0x9C1E1C1C;

    public static void Attach(Popup popup, Border card)
    {
        // Акрил работает только у обычных (не «слоистых») окон.
        popup.AllowsTransparency = false;
        popup.Opened += (_, _) => Apply(popup.Child, card);
    }

    public static void Apply(Visual visual, Border card)
    {
        if (PresentationSource.FromVisual(visual) is not HwndSource source) return;
        var hwnd = source.Handle;
        var ok = ShellNative.EnableAcrylic(hwnd, Tint);
        RoundCorners(hwnd);
        // Углы скругляет сама Windows, поэтому у карточки их убираем — иначе в уголках видны тёмные треугольники.
        card.CornerRadius = new CornerRadius(0);
        card.BorderThickness = new Thickness(0);
        if (ok)
        {
            source.CompositionTarget!.BackgroundColor = Colors.Transparent;
            card.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x1C, 0x1C, 0x1E));
        }
        else
        {
            // Размытие недоступно: просто тёмный непрозрачный фон, без прозрачных (чёрных) пикселей.
            source.CompositionTarget!.BackgroundColor = Color.FromRgb(0x1C, 0x1C, 0x1E);
            card.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        }
    }

    public static void RoundCorners(IntPtr hwnd)
    {
        try
        {
            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        }
        catch { }
    }

    /// <summary>
    /// Плавно проявляет или прячет стеклянное окно целиком. Прозрачность у такого окна задаётся
    /// только через «слоистый» режим, а в покое его надо снимать, иначе пропадёт размытие.
    /// </summary>
    public static void Fade(IntPtr hwnd, bool show, int ms, Action? done = null)
    {
        if (hwnd == IntPtr.Zero) { done?.Invoke(); return; }
        SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x80000 /* WS_EX_LAYERED */);
        SetLayeredWindowAttributes(hwnd, 0, show ? (byte)0 : (byte)255, 2 /* LWA_ALPHA */);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
            var eased = 1 - Math.Pow(1 - t, 3);
            SetLayeredWindowAttributes(hwnd, 0, (byte)(255 * (show ? eased : 1 - eased)), 2);
            if (t < 1) return;
            CompositionTarget.Rendering -= tick;
            if (show) SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) & ~0x80000);
            done?.Invoke();
        };
        CompositionTarget.Rendering += tick;
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
