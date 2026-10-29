namespace NotchWin;

internal static class Layout
{
    /// <summary>Высота верхней панели в DIP (настройка). Развёрнутые окна начинаются ниже неё.</summary>
    public static double BarHeight => Math.Round(Math.Clamp(AppSettings.Current.BarHeight, 26, 44));
}
