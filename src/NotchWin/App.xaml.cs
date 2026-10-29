using System.Windows;

namespace NotchWin;

public partial class App : Application
{
    private TopBarWindow? _bar;
    private MainWindow? _notch;
    private bool _watchdog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 2 && e.Args[0] == "--tray-watchdog" && int.TryParse(e.Args[1], out var pid))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _watchdog = true;
            TrayHost.RunWatchdog(pid);
            Shutdown();
            return;
        }

        // Если программа упадёт, всё равно возвращаем место сверху экрана.
        DispatcherUnhandledException += (_, _) => Cleanup();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();

        // Часы на панели задач Windows больше не прячем (пользователь попросил вернуть).
        // Возвращаем их, если прошлые версии успели спрятать.
        TaskbarTweaks.ShowTrayClock();

        // Путь к программе мог поменяться (новая сборка) — обновляем запись автозапуска.
        if (AppSettings.Current.RunAtStartup) Autostart.Apply(true);

        // Сначала панель: она резервирует место сверху. Потом челка поверх неё.
        _bar = new TopBarWindow();
        _bar.Show();

        // Список приложений для поиска читаем заранее, чтобы первый Alt+Пробел открывался мгновенно.
        SearchWindow.Warmup();

        var notch = _notch = new MainWindow();
        MainWindow = notch;
        _bar.FullscreenChanged += fullscreen => notch.Visibility = fullscreen ? Visibility.Hidden : Visibility.Visible;
        // Панель лежит под челкой. Если Windows подняла панель выше (клик, смена позиции), сразу возвращаем челку наверх.
        _bar.ZOrderChanged += KeepNotchOnTop;
        notch.Show();
    }

    /// <summary>Челка всегда поверх панели и наших всплывающих окон, кроме полноэкранного режима.</summary>
    public static void KeepNotchOnTop()
    {
        if (Current is App { _notch: { } notch }) notch.Dispatcher.BeginInvoke(notch.BringToTop);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_watchdog) Cleanup();
        base.OnExit(e);
    }

    private void Cleanup()
    {
        AppSettings.Flush();
        // Значки трея возвращаем Windows (это можно сделать только из UI-потока).
        try { if (Dispatcher.CheckAccess()) TrayHost.Stop(); } catch { }
        _bar?.UnregisterAppBar();
        TaskbarTweaks.Restore();
        Services.OsdHider.Restore();
        try { _notch?.ReleaseResources(); } catch { }
    }
}
