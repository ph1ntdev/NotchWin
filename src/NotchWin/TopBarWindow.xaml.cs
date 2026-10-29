using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NotchWin.Services;
using static NotchWin.AppBarNative;
using static NotchWin.NativeMethods;
using static NotchWin.ShellNative;
using static NotchWin.StatusNative;

namespace NotchWin;

public partial class TopBarWindow : Window
{
    // Тёмно-серый полупрозрачный тон панели (AABBGGRR). Челка чисто чёрная, поэтому её видно на панели.
    // Тон слабее, чем раньше: часть затемнения даёт подложка BarRoot, которая остаётся,
    // даже если Windows на мгновение снимет размытие.
    // Прозрачность берётся из настройки «Затемнение».
    private static uint BarTint => ((uint)Math.Round(Math.Clamp(AppSettings.Current.BarDim, 0, 100) * 2.55) << 24) | 0x262626;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<uint, string> _appNames = new();
    private readonly int _ownPid = Environment.ProcessId;
    private uint _callbackMessage;
    private uint _taskbarCreatedMessage;
    private bool _registered;
    private bool _fullscreen;
    private bool _syncingSlider;
    private IntPtr _lastForeground;
    private readonly PopupCloser _volumeCloser;
    private readonly PopupCloser _calendarCloser;
    private readonly PopupCloser _weatherCloser;
    private readonly PopupCloser _binCloser;
    private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(20) };
    private readonly DispatcherTimer _binWatch = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private DateTime _lastBinDrag;
    private WeatherReport? _weather;
    private bool _weatherLoading;

    /// <summary>Панель поменяла положение в стопке окон — челку нужно вернуть поверх неё.</summary>
    public event Action? ZOrderChanged;

    /// <summary>true, когда на основном мониторе открыто полноэкранное приложение (видео, игра, F11).</summary>
    public event Action<bool>? FullscreenChanged;

    public TopBarWindow()
    {
        InitializeComponent();
        _volumeCloser = new PopupCloser(VolumePopup, VolumeCard, VolumeButton);
        _volumeCloser.Closed += () => { AppsPanel.Children.Clear(); _appRows.Clear(); };
        _calendarCloser = new PopupCloser(CalendarPopup, CalendarCard, ClockButton);
        _weatherCloser = new PopupCloser(WeatherPopup, WeatherCard, WeatherButton);
        _binCloser = new PopupCloser(BinPopup, BinCard, BinButton);
        _trayCloser = new PopupCloser(TrayPopup, TrayCard, TrayMoreButton);
        _trayCloser.Closed += () => TrayMoreIcon.Text = "\uE70D";
        _trayCloser.Hold = () => _trayDrag != null || TrayMenuOpen();
        TrayHost.Changed += RenderTray;
        Loaded += (_, _) => { TrayHost.Apply(); RenderTray(); };
        Closed += (_, _) => TrayHost.Changed -= RenderTray;
        _binWatch.Tick += (_, _) => WatchBinDrag();
        _weatherTimer.Tick += (_, _) => StartWeatherRefresh();
        _weatherKey = WeatherKey();
        _layoutKey = LayoutKey();
        AppSettings.Changed += OnSettingsChanged;
        AppSettings.RecordingHotkeyChanged += RegisterSearchHotkey;
        Loaded += (_, _) => ApplyBarItems();
        Loaded += (_, _) => { StartWeatherRefresh(); _weatherTimer.Start(); UpdateBinIcon(); };
        SourceInitialized += (_, _) => OnSourceInitialized();
        Closed += (_, _) => UnregisterAppBar();
        Loaded += (_, _) => { UpdateStatus(); _tick.Start(); };
        _tick.Tick += (_, _) => UpdateStatus();
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(SetAppBarPosition);
    }

    // Храним HWND отдельно: при аварийном выходе снять AppBar нужно не из UI-потока.
    private IntPtr _hwnd;
    private IntPtr Handle => _hwnd;

    private void OnSourceInitialized()
    {
        var hwnd = _hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        var source = HwndSource.FromHwnd(hwnd)!;
        source.AddHook(WndProc);

        // Размытие фона под панелью. Если не вышло, просто тёмно-серый фон.
        source.CompositionTarget!.BackgroundColor = Colors.Transparent;
        if (!EnableAcrylic(hwnd, BarTint))
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        _acrylicAt = DateTime.Now;

        WatchVirtualDesktops();

        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        RegisterAppBar();
        RegisterSearchHotkey();
    }

    // ---------- AppBar: резервируем полосу сверху экрана ----------

    private void RegisterAppBar()
    {
        _callbackMessage = RegisterWindowMessage("NotchWin.AppBar");
        var data = NewData();
        data.uCallbackMessage = _callbackMessage;
        SHAppBarMessage(ABM_NEW, ref data);
        _registered = true;
        SetAppBarPosition();
    }

    /// <summary>Снимает резерв места. Вызывать обязательно при выходе, иначе полоса останется пустой.</summary>
    public void UnregisterAppBar()
    {
        if (_hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, SearchHotkeyId);
        if (!_registered || _hwnd == IntPtr.Zero) return;
        var data = NewData();
        SHAppBarMessage(ABM_REMOVE, ref data);
        _registered = false;
    }

    private void SetAppBarPosition()
    {
        if (!_registered) return;

        var info = GetPrimaryMonitor(out var scale);
        if (info == null) return;
        var heightPx = (int)Math.Round(Layout.BarHeight * scale);

        var data = NewData();
        data.uEdge = ABE_TOP;
        data.rc = info.Value.rcMonitor;
        data.rc.Bottom = data.rc.Top + heightPx;
        SHAppBarMessage(ABM_QUERYPOS, ref data);   // система может сдвинуть, если сверху уже есть другая панель
        data.rc.Bottom = data.rc.Top + heightPx;
        SHAppBarMessage(ABM_SETPOS, ref data);

        SetWindowPos(Handle, HWND_TOPMOST, data.rc.Left, data.rc.Top,
            data.rc.Right - data.rc.Left, data.rc.Bottom - data.rc.Top, SWP_NOACTIVATE);

        var monitor = info.Value.rcMonitor;
        _backdropSize = (monitor.Right - monitor.Left, monitor.Bottom - monitor.Top, data.rc.Bottom - data.rc.Top);
        RefreshBackdrop(force: true);
    }

    // ---------- Фон: свои размытые обои вместо системного размытия ----------

    /// <summary>У каждого рабочего стола могут быть свои обои: подхватываем их после перелистывания.</summary>
    private async void RefreshBackdropAfterSwitch()
    {
        foreach (var delay in new[] { 300, 1200 })
        {
            await Task.Delay(delay);
            RefreshBackdrop();
        }
    }

    private (int W, int H, int Bar) _backdropSize;
    private string? _backdropKey;
    private bool _backdropActive;

    /// <summary>Перерисовывает фон, если сменились обои (или force). Если обои не прочитать — остаётся системное размытие.</summary>
    private void RefreshBackdrop(bool force = false)
    {
        var key = WallpaperBackdrop.CurrentKey() + _backdropSize;
        if (!force && key == _backdropKey) return;
        _backdropKey = key;
        var brush = WallpaperBackdrop.Create(_backdropSize.W, _backdropSize.H, _backdropSize.Bar);
        _backdropActive = brush != null;
        if (brush != null) Background = brush;
    }

    private static MONITORINFO? GetPrimaryMonitor(out double scale)
    {
        scale = 1.0;
        var monitor = Screens.Target();
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;
        if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0) scale = dpiX / 96.0;
        return info;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TrayHost.TaskbarCreatedMessage) Dispatcher.BeginInvoke(TrayHost.OnTaskbarCreated);
        if (_registered && msg == (int)_callbackMessage)
        {
            switch (wParam.ToInt32())
            {
                case ABN_POSCHANGED:
                    SetAppBarPosition();
                    break;
                case ABN_FULLSCREENAPP:
                    // Не доверяем флагу напрямую: Windows шлёт его и при «Свернуть все окна»
                    // (рабочий стол тоже "на весь экран"). Проверяем сами.
                    CheckFullscreen();
                    break;
            }
            handled = true;
        }
        else if (msg == 0x001A /* WM_SETTINGCHANGE */ && wParam.ToInt32() == 0x0014 /* SPI_SETDESKWALLPAPER */)
        {
            Dispatcher.BeginInvoke(() => RefreshBackdrop(force: true));
        }
        else if (msg is 0x031E /* WM_DWMCOMPOSITIONCHANGED */ or 0x02B1 /* WM_WTSSESSION_CHANGE */)
        {
            _acrylicAt = DateTime.MinValue;
            Dispatcher.BeginInvoke(() => KeepAcrylic(IntPtr.Zero));
        }
        else if (msg == 0x0047 /* WM_WINDOWPOSCHANGED */)
        {
            // flags лежат после двух дескрипторов и четырёх int в структуре WINDOWPOS.
            var flags = Marshal.ReadInt32(lParam, IntPtr.Size * 2 + 16);
            if ((flags & 0x4 /* SWP_NOZORDER */) == 0) ZOrderChanged?.Invoke();
        }
        else if (msg == 0x0021 /* WM_MOUSEACTIVATE */ || msg == 0x0201 /* WM_LBUTTONDOWN */)
        {
            ZOrderChanged?.Invoke();
        }
        else if (msg == WM_HOTKEY && wParam.ToInt32() == SearchHotkeyId)
        {
            SearchWindow.Toggle();
            handled = true;
        }
        else if (msg == (int)_taskbarCreatedMessage)
        {
            // Проводник перезапустился: регистрация панели и настройки панели задач потерялись.
            _registered = false;
            RegisterAppBar();
        }
        return IntPtr.Zero;
    }

    private APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = Handle };

    // ---------- Полноэкранный режим ----------

    private void CheckFullscreen()
    {
        var fullscreen = AppSettings.Current.HideInFullscreen && IsFullscreenAppOnPrimary();
        if (fullscreen != _fullscreen)
        {
            _fullscreen = fullscreen;
            Visibility = fullscreen ? Visibility.Hidden : Visibility.Visible;
            FullscreenChanged?.Invoke(fullscreen);
        }

        // Страховка: если что-то спрятало панель (например, «Свернуть все окна»), возвращаем её.
        var hwnd = Handle;
        if (!fullscreen && hwnd != IntPtr.Zero && (IsIconic(hwnd) || !IsWindowVisible(hwnd)))
        {
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            SetAppBarPosition();
        }
    }

    private bool IsFullscreenAppOnPrimary()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        var cls = GetClassName(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

        GetWindowThreadProcessId(fg, out var pid);
        if (pid == _ownPid) return _fullscreen;

        var info = GetPrimaryMonitor(out _);
        if (info == null || !GetWindowRect(fg, out var r)) return false;
        var m = info.Value.rcMonitor;
        // Развёрнутое окно сюда не попадает: оно меньше экрана на нашу панель и панель задач.
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    // ---------- Статус ----------

    private DateTime _acrylicAt;
    private IntPtr _acrylicForeground;

    /// <summary>
    /// При переключении рабочих столов (4 пальца по тачпаду, Ctrl+Win+стрелка) Windows иногда
    /// сбрасывает размытие у панели. Включаем его заново при смене окна и раз в несколько секунд.
    /// </summary>
    private void KeepAcrylic(IntPtr foreground)
    {
        if (_hwnd == IntPtr.Zero || _backdropActive) return;
        if (foreground == _acrylicForeground && (DateTime.Now - _acrylicAt).TotalSeconds < 3) return;
        _acrylicForeground = foreground;
        _acrylicAt = DateTime.Now;
        EnableAcrylic(_hwnd, BarTint);
    }

    /// <summary>
    /// Смена рабочего стола записывается в реестр (CurrentVirtualDesktop). Ждём этого в фоновом
    /// потоке и включаем размытие сразу и ещё несколько раз, пока идёт анимация перелистывания.
    /// </summary>
    private void WatchVirtualDesktops()
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
                if (key == null) return;
                while (RegNotifyChangeKeyValue(key.Handle, true, 0x4 /* LAST_SET */, IntPtr.Zero, false) == 0)
                    Dispatcher.BeginInvoke(ReapplyAcrylicBurst);
            }
            catch { /* без слежения останется проверка раз в 3 секунды */ }
        }) { IsBackground = true, Name = "VirtualDesktopWatch" };
        thread.Start();
    }

    private async void ReapplyAcrylicBurst()
    {
        RefreshBackdropAfterSwitch();
        if (_backdropActive) return;
        foreach (var delay in new[] { 0, 120, 300, 600, 1000 })
        {
            if (delay > 0) await Task.Delay(delay);
            if (_hwnd == IntPtr.Zero) return;
            EnableAcrylic(_hwnd, BarTint);
        }
        _acrylicAt = DateTime.Now;
    }

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, bool watchSubtree,
        int filter, IntPtr hEvent, bool asynchronous);

    // ---------- Настройки ----------

    private string _weatherKey, _layoutKey;

    private static string WeatherKey()
    {
        var s = AppSettings.Current;
        return $"{s.WeatherCity}|{s.WeatherUseLocation}|{s.WeatherSource}";
    }

    private static string LayoutKey() => $"{AppSettings.Current.Monitor}|{Layout.BarHeight}";

    /// <summary>Настройку поменяли — применяем сразу, без перезапуска.</summary>
    private void OnSettingsChanged()
    {
        ApplyBarItems();
        TrayHost.Apply();
        RenderTray();
        UpdateClock(DateTime.Now);
        UpdateBattery();

        var layout = LayoutKey();
        if (layout != _layoutKey)
        {
            _layoutKey = layout;
            SetAppBarPosition();
        }
        RefreshBackdrop();
        if (!_backdropActive && _hwnd != IntPtr.Zero) EnableAcrylic(_hwnd, BarTint);

        // Город и источник — загружаем заново, остальное (градусы) — просто перерисовываем.
        var weather = WeatherKey();
        if (weather != _weatherKey)
        {
            _weatherKey = weather;
            StartWeatherRefresh();
        }
        else ShowWeather();

        RegisterSearchHotkey();
        CheckFullscreen();
    }

    private void ApplyBarItems()
    {
        var s = AppSettings.Current;
        static Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        AppNameText.Visibility = V(s.BarAppName);
        SearchButton.Visibility = V(s.BarSearch);
        WeatherButton.Visibility = V(s.BarWeather);
        BinButton.Visibility = V(s.BarBin);
        LayoutButton.Visibility = V(s.BarLayout);
        VolumeButton.Visibility = V(s.BarVolume);
    }

    private void UpdateClock(DateTime now)
    {
        var s = AppSettings.Current;
        var day = string.Join(" ", new[] { s.ClockWeekday ? now.ToString("ddd") : "", s.ClockDate ? now.ToString("d MMM") : "" }
            .Where(x => x.Length > 0));
        var time = now.ToString(s.ClockSeconds ? "HH:mm:ss" : "HH:mm");
        ClockText.Text = day.Length > 0 ? $"{day}  {time}" : time;
    }

    private void UpdateStatus()
    {
        CheckFullscreen();
        KeepAcrylic(GetForegroundWindow());
        // Слайд-шоу и «Windows: интересное» меняют обои без уведомления — проверяем раз в 10 секунд.
        if (DateTime.Now.Second % 10 == 0) RefreshBackdrop();

        var now = DateTime.Now;
        UpdateClock(now);

        var fg = GetForegroundWindow();
        var thread = GetWindowThreadProcessId(fg, out var pid);
        if (pid != 0 && pid != _ownPid)
        {
            _lastForeground = fg;
            // На рабочем столе («Свернуть все окна») никакого названия, как в macOS у Finder без окон.
            AppNameText.Text = GetClassName(fg) is "Progman" or "WorkerW" ? "" : GetAppName(pid);
            LayoutText.Text = GetLayoutName(thread);
        }

        UpdateVolume();
        UpdateBattery();
        if (now.Second % 10 == 0) UpdateBinIcon();
    }

    private string GetAppName(uint pid)
    {
        if (_appNames.TryGetValue(pid, out var cached)) return cached;
        string name;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
            try
            {
                var desc = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) name = desc;
            }
            catch { /* у системных процессов нет доступа к модулю */ }
        }
        catch { name = ""; }
        if (name is "explorer" or "Explorer" or "Проводник" or "Windows Explorer") name = "Проводник";
        _appNames[pid] = name;
        return name;
    }

    private static string GetLayoutName(uint thread)
    {
        try
        {
            var langId = (int)(GetKeyboardLayout(thread).ToInt64() & 0xFFFF);
            return new CultureInfo(langId).TwoLetterISOLanguageName.ToUpperInvariant();
        }
        catch { return ""; }
    }

    private void UpdateVolume()
    {
        var v = AudioVolume.Get();
        // Кнопку не прячем никогда: даже если устройство не ответило, микшер должен открываться.
        if (v == null) { VolumeIcon.Text = "\uE767"; return; }
        var (level, muted) = v.Value;
        var icon = muted || level < 0.01f ? ""
            : level < 0.34f ? ""
            : level < 0.67f ? ""
            : "";
        VolumeIcon.Text = icon;
        PopupMuteIcon.Text = icon;
        VolumeButton.ToolTip = muted ? "Звук выключен" : $"Громкость {Math.Round(level * 100)}%";
        VolumePercent.Text = $"{Math.Round(level * 100)}%";

        // Пока пользователь двигает ползунок, не перебиваем его значением из системы.
        if (!VolumeSlider.IsMouseCaptureWithin)
        {
            _syncingSlider = true;
            VolumeSlider.Value = Math.Round(level * 100);
            _syncingSlider = false;
        }
    }

    private void UpdateBattery()
    {
        if (!GetSystemPowerStatus(out var s) || (s.BatteryFlag & 128) != 0 || s.BatteryLifePercent > 100)
        {
            BatteryButton.Visibility = Visibility.Collapsed; // настольный ПК без батареи
            return;
        }
        BatteryButton.Visibility = AppSettings.Current.BarBattery ? Visibility.Visible : Visibility.Collapsed;
        var pct = s.BatteryLifePercent;
        BatteryText.Text = $"{pct}%";
        var charging = s.ACLineStatus == 1;
        // E850..E859 = батарея 0..90%, E83F = 100%, E945 = молния (зарядка)
        BatteryIcon.Text = charging ? "" : pct >= 100 ? "" : ((char)(0xE850 + pct / 10)).ToString();
        BatteryButton.ToolTip = charging ? $"Заряжается, {pct}%" : $"Батарея {pct}%";
    }

    // ---------- Кнопки ----------

    private void OnLayoutClick(object sender, RoutedEventArgs e)
    {
        // Просим активное окно переключить раскладку на следующую, как по Alt+Shift.
        if (_lastForeground != IntPtr.Zero)
            PostMessage(_lastForeground, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, HKL_NEXT);
        Dispatcher.BeginInvoke(UpdateStatus, DispatcherPriority.Background);
    }

    private void OnBatteryClick(object sender, RoutedEventArgs e) => OpenUri("ms-settings:batterysaver");

    private void CloseOtherPopups(PopupCloser keep)
    {
        foreach (var c in new[] { _volumeCloser, _calendarCloser, _weatherCloser, _binCloser })
            if (c != keep) c.Close();
    }

    private void OnVolumeClick(object sender, RoutedEventArgs e)
    {
        CloseOtherPopups(_volumeCloser);
        _volumeCloser.Toggle(() => { UpdateVolume(); RebuildMixer(); });
    }

    private void OnVolumeRightClick(object sender, MouseButtonEventArgs e)
    {
        AudioVolume.ToggleMute();
        UpdateVolume();
        e.Handled = true; // не открывать меню «Выход»
    }

    private void OnVolumeWheel(object sender, MouseWheelEventArgs e)
    {
        AudioVolume.Change(e.Delta > 0 ? 0.04f : -0.04f);
        UpdateVolume();
        e.Handled = true;
    }

    private void OnVolumeSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSlider || !IsLoaded) return;
        AudioVolume.Set((float)(e.NewValue / 100));
        VolumePercent.Text = $"{Math.Round(e.NewValue)}%";
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        AudioVolume.ToggleMute();
        UpdateVolume();
    }

    // ---------- Микшер (как в EarTrumpet) ----------

    private readonly Dictionary<uint, (Slider Slider, TextBlock Percent, TextBlock MuteIcon)> _appRows = new();

    private void RebuildMixer()
    {
        // Устройства вывода: клик делает устройство основным.
        DevicesPanel.Children.Clear();
        foreach (var device in AudioMixer.GetOutputDevices())
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var check = new TextBlock { Text = device.IsDefault ? "\uE73E" : "", Style = (Style)FindResource("Icon"), FontSize = 12 };
            var name = new TextBlock
            {
                Text = device.Name, Style = (Style)FindResource("Item"), FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = device.IsDefault ? FontWeights.SemiBold : FontWeights.Normal,
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(check);
            row.Children.Add(name);

            var button = new System.Windows.Controls.Button
            {
                Style = (Style)FindResource("BarButton"), Content = row, Height = 28, Padding = new Thickness(4, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            var id = device.Id;
            button.Click += (_, _) => { AudioMixer.SetDefaultDevice(id); UpdateVolume(); RebuildMixer(); };
            DevicesPanel.Children.Add(button);
        }

        // Приложения, которые сейчас могут играть звук.
        AppsPanel.Children.Clear();
        _appRows.Clear();
        var apps = AudioMixer.GetApps();
        AppsHeader.Visibility = apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var app in apps)
            AppsPanel.Children.Add(BuildAppRow(app));
    }

    private UIElement BuildAppRow(AppAudio app)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

        // Иконка приложения; клик выключает его звук.
        var muteIcon = new TextBlock
        {
            Style = (Style)FindResource("Icon"), FontSize = 10, Text = "\uE74F",
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -2, -2), Visibility = app.Muted ? Visibility.Visible : Visibility.Collapsed,
        };
        var iconHost = new Grid { Width = 22, Height = 22 };
        if (app.Icon != null)
            iconHost.Children.Add(new System.Windows.Controls.Image { Source = app.Icon, Opacity = app.Muted ? 0.4 : 1 });
        else
            iconHost.Children.Add(new TextBlock { Style = (Style)FindResource("Icon"), Text = "\uE7F5", FontSize = 16 });
        iconHost.Children.Add(muteIcon);
        var iconButton = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("BarButton"), Content = iconHost, Padding = new Thickness(4, 0, 4, 0),
            Height = 30, ToolTip = app.Muted ? "Включить звук" : "Выключить звук",
        };
        var pid = app.ProcessId;
        iconButton.Click += (_, _) => { AudioMixer.ToggleAppMute(pid); RebuildMixer(); };

        var middle = new StackPanel { Margin = new Thickness(8, 0, 8, 0) };
        middle.Children.Add(new TextBlock
        {
            Text = app.Name, FontSize = 12, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var slider = new Slider { Style = (Style)FindResource("FlatSlider"), Value = Math.Round(app.Volume * 100) };
        middle.Children.Add(slider);
        Grid.SetColumn(middle, 1);

        var percent = new TextBlock
        {
            Style = (Style)FindResource("Item"), FontSize = 12, TextAlignment = TextAlignment.Right,
            Text = $"{Math.Round(app.Volume * 100)}%",
        };
        Grid.SetColumn(percent, 2);

        slider.ValueChanged += (_, e) =>
        {
            AudioMixer.SetAppVolume(pid, (float)(e.NewValue / 100));
            percent.Text = $"{Math.Round(e.NewValue)}%";
        };

        grid.Children.Add(iconButton);
        grid.Children.Add(middle);
        grid.Children.Add(percent);
        _appRows[pid] = (slider, percent, muteIcon);
        return grid;
    }

    // ---------- Календарь ----------

    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    private void OnClockClick(object sender, RoutedEventArgs e)
    {
        CloseOtherPopups(_calendarCloser);
        _calendarCloser.Toggle(() =>
        {
            _calendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            BuildCalendar(0);
        });
    }

    private void OnCalendarPrev(object sender, RoutedEventArgs e) { _calendarMonth = _calendarMonth.AddMonths(-1); BuildCalendar(-1); }
    private void OnCalendarNext(object sender, RoutedEventArgs e) { _calendarMonth = _calendarMonth.AddMonths(1); BuildCalendar(1); }
    private void OnCalendarToday(object sender, RoutedEventArgs e)
    {
        var target = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var dir = target.CompareTo(_calendarMonth);
        _calendarMonth = target;
        BuildCalendar(dir);
    }

    /// <summary>direction: -1 листаем назад, 1 вперёд, 0 без анимации.</summary>
    private void BuildCalendar(int direction)
    {
        if (direction != 0)
        {
            // Новый месяц въезжает с той стороны, куда листаем, и проявляется.
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(260);
            DaysShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(direction * 60, 0, duration) { EasingFunction = ease });
            DaysGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            CalendarMonth.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, duration));
        }

        var culture = CultureInfo.CurrentCulture;
        var today = DateTime.Today;
        CalendarToday.Text = Capitalize(today.ToString("dddd", culture));
        CalendarTodaySub.Text = today.ToString("d MMMM yyyy", culture);
        CalendarMonth.Text = Capitalize(culture.DateTimeFormat.GetMonthName(_calendarMonth.Month)) + " " + _calendarMonth.Year;

        var firstDay = culture.DateTimeFormat.FirstDayOfWeek;
        WeekdayRow.Children.Clear();
        for (var i = 0; i < 7; i++)
        {
            var dow = (DayOfWeek)(((int)firstDay + i) % 7);
            WeekdayRow.Children.Add(new TextBlock
            {
                Text = Capitalize(culture.DateTimeFormat.GetShortestDayName(dow)),
                FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4),
            });
        }

        DaysGrid.Children.Clear();
        var offset = ((int)_calendarMonth.DayOfWeek - (int)firstDay + 7) % 7;
        var start = _calendarMonth.AddDays(-offset);
        for (var i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            var isToday = day == today;
            var inMonth = day.Month == _calendarMonth.Month;
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

            var cell = new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Margin = new Thickness(1),
                Background = isToday ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)) : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = day.Day.ToString(), FontSize = 12.5,
                    FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = !inMonth ? new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF))
                        : weekend && !isToday ? new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80))
                        : Brushes.White,
                },
            };
            DaysGrid.Children.Add(cell);
        }
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    private static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void OnSettings(object sender, RoutedEventArgs e) => SettingsWindow.ShowSingle();

    private void OnWeatherSettings(object sender, RoutedEventArgs e) => SettingsWindow.ShowSingle("weather");

    // ---------- Поиск ----------

    private const int SearchHotkeyId = 0x4E57;
    private const int WM_HOTKEY = 0x0312;

    /// <summary>true — сочетание для поиска занято другой программой (показываем в настройках).</summary>
    public static bool SearchHotkeyBusy { get; private set; }

    private void RegisterSearchHotkey()
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterHotKey(_hwnd, SearchHotkeyId);
        var combo = AppSettings.Current.SearchHotkey;
        var shown = combo;
        var ok = false;
        if (!AppSettings.RecordingHotkey && Hotkeys.TryParse(combo, out var mods, out var vk))
        {
            ok = RegisterHotKey(_hwnd, SearchHotkeyId, mods | Hotkeys.MOD_NOREPEAT, vk);
            // Alt+Пробел часто занят (например, PowerToys Run) — тогда Ctrl+Alt+Пробел, как раньше.
            if (!ok && combo == "Alt+Space"
                && RegisterHotKey(_hwnd, SearchHotkeyId, Hotkeys.MOD_ALT | Hotkeys.MOD_CONTROL | Hotkeys.MOD_NOREPEAT, vk))
            {
                ok = true;
                shown = "Ctrl+Alt+Space";
            }
        }
        SearchHotkeyBusy = !ok && combo.Length > 0 && !AppSettings.RecordingHotkey;
        SearchButton.ToolTip = ok ? $"Поиск ({Hotkeys.Display(shown)})" : "Поиск";
    }

    private void OnSearchClick(object sender, RoutedEventArgs e) => SearchWindow.Toggle();

    // ---------- Погода ----------

    private async Task RefreshWeatherAsync()
    {
        if (_weatherLoading) return;
        _weatherLoading = true;
        if (_weather == null) ShowWeather();
        try
        {
            var report = await WeatherService.GetAsync();
            if (report != null) _weather = report;
        }
        finally { _weatherLoading = false; }
        ShowWeather();
    }

    private void ShowWeather()
    {
        var w = _weather;
        if (w == null)
        {
            // Данных пока нет: кнопка всё равно на месте, а в окне видно, что случилось.
            WeatherIcon.Text = "\u2601";
            WeatherText.Text = "—";
            WeatherButton.ToolTip = null;
            WeatherCity.Text = "Погода";
            WeatherBig.Text = "—";
            WeatherBigIcon.Text = "";
            WeatherCondition.Text = _weatherLoading ? "Загружаю…" : WeatherService.LastError ?? "Загружаю…";
            WeatherDetails.Text = _weatherLoading ? "" : "Нажмите «Обновить» или выберите город в настройках.";
            WeatherSource.Text = "Open-Meteo";
            WeatherHours.Children.Clear();
            WeatherDays.Children.Clear();
            return;
        }

        WeatherIcon.Text = w.Icon;
        WeatherText.Text = WeatherService.Temp(w.Temp);
        WeatherButton.ToolTip = $"{w.City}: {w.Text}";

        WeatherCity.Text = w.City;
        WeatherBig.Text = WeatherService.Temp(w.Temp);
        WeatherBigIcon.Text = w.Icon;
        WeatherCondition.Text = w.Text;
        WeatherDetails.Text = $"Ощущается как {WeatherService.Temp(w.Feels)} · ветер {Math.Round(w.Wind)} м/с · влажность {w.Humidity}%";
        WeatherSource.Text = $"{w.Source} · {DateTime.Now:HH:mm}";

        var dim = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        WeatherHours.Children.Clear();
        foreach (var h in w.Hours)
        {
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            col.Children.Add(new TextBlock { Text = h.Time, FontSize = 11, Foreground = dim, HorizontalAlignment = HorizontalAlignment.Center });
            col.Children.Add(new TextBlock
            {
                Text = h.Icon, FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 16, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4),
            });
            col.Children.Add(new TextBlock { Text = WeatherService.Temp(h.Temp), FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
            WeatherHours.Children.Add(col);
        }

        WeatherDays.Children.Clear();
        foreach (var d in w.Days)
        {
            var row = new Grid { Height = 28 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            var name = new TextBlock { Text = d.Name, FontSize = 12.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            var icon = new TextBlock
            {
                Text = d.Icon, FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 15, Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            };
            var temps = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            temps.Inlines.Add(new System.Windows.Documents.Run(WeatherService.Temp(d.Max)) { Foreground = Brushes.White });
            temps.Inlines.Add(new System.Windows.Documents.Run("   " + WeatherService.Temp(d.Min)) { Foreground = dim });
            Grid.SetColumn(icon, 1);
            Grid.SetColumn(temps, 2);
            row.Children.Add(name);
            row.Children.Add(icon);
            row.Children.Add(temps);
            WeatherDays.Children.Add(row);
        }
    }

    private void OnWeatherClick(object sender, RoutedEventArgs e)
    {
        CloseOtherPopups(_weatherCloser);
        // Показываем то, что уже загружено (окно сразу нужного размера), а свежие данные подтянем фоном.
        _weatherCloser.Toggle(() => { if ((DateTime.Now - _lastWeatherRefresh).TotalMinutes > 10) StartWeatherRefresh(); });
    }

    private DateTime _lastWeatherRefresh = DateTime.Now;

    private void StartWeatherRefresh()
    {
        _lastWeatherRefresh = DateTime.Now;
        _ = RefreshWeatherAsync();
    }

    private void OnWeatherRefresh(object sender, RoutedEventArgs e)
    {
        WeatherSource.Text = "Обновляю…";
        StartWeatherRefresh();
    }

    // ---------- Корзина ----------

    private void OnBinClick(object sender, RoutedEventArgs e)
    {
        _binCloser.Close();
        RecycleBin.Open();
    }

    private void UpdateBinIcon()
    {
        var (items, bytes) = RecycleBin.Query();
        BinButton.ToolTip = items > 0 ? $"Корзина: {items} шт., {FormatSize(bytes)}" : "Корзина пуста";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} МБ",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.#} ГБ",
    };

    private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void OnBinDragEnter(object sender, DragEventArgs e)
    {
        if (!HasFiles(e)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        _lastBinDrag = DateTime.Now;
        if (_binCloser.IsOpen) return;
        CloseOtherPopups(_binCloser);

        // Окно встаёт прямо над значком (по центру), чтобы мышь почти не пришлось двигать.
        BinPopup.HorizontalOffset = (BinButton.ActualWidth - BinCard.Width) / 2;
        _binHot = false;
        _binCloser.Open();
        _binWatch.Start();

        // Значок корзины «выезжает» из панели в центр окна, растёт и загорается красным.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(260);
        BinShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-40, 0, duration) { EasingFunction = ease });
        BinScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.35, 1, duration) { EasingFunction = ease });
        BinScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.35, 1, duration) { EasingFunction = ease });
        SetBinHot(true);
    }

    private void OnBinDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasFiles(e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        _lastBinDrag = DateTime.Now;
    }

    private void OnBinCardDragOver(object sender, DragEventArgs e)
    {
        OnBinDragOver(sender, e);
        SetBinHot(HasFiles(e));
    }

    private void OnBinCardDragLeave(object sender, DragEventArgs e) => SetBinHot(false);

    private bool _binHot;

    /// <summary>Файл над большой корзиной: она подрастает и краснеет — «сейчас удалится».</summary>
    private void SetBinHot(bool hot)
    {
        if (_binHot == hot) return;
        _binHot = hot;
        var duration = TimeSpan.FromMilliseconds(180);
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
        BinCircle.Background = new SolidColorBrush(hot ? Color.FromArgb(0xCC, 0xFF, 0x45, 0x3A) : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        BinBigIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(hot ? 1 : 0.7, duration));
    }

    /// <summary>Файл унесли от корзины или бросили где-то ещё — прячем большое окно.</summary>
    private void WatchBinDrag()
    {
        if (!_binCloser.IsOpen) { _binWatch.Stop(); return; }
        if ((DateTime.Now - _lastBinDrag).TotalMilliseconds > 350)
        {
            _binWatch.Stop();
            _binCloser.Close();
        }
    }

    private void OnBinDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _binWatch.Stop();
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) { _binCloser.Close(); return; }

        BinTitle.Text = RecycleBin.Delete(files) ? (files.Length == 1 ? "Удалено" : $"Удалено: {files.Length}") : "Не удалось удалить";
        BinSub.Text = "Вернуть можно из корзины";
        // Корзина «проглатывает» файл: короткий толчок.
        var bump = new DoubleAnimation(1, 0.85, TimeSpan.FromMilliseconds(120)) { AutoReverse = true };
        BinScale.BeginAnimation(ScaleTransform.ScaleXProperty, bump);
        BinScale.BeginAnimation(ScaleTransform.ScaleYProperty, bump);
        SetBinHot(false);
        UpdateBinIcon();
        var close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        close.Tick += (_, _) =>
        {
            close.Stop();
            _binCloser.Close();
            BinTitle.Text = "Отпустите, чтобы удалить";
            BinSub.Text = "Файлы можно будет восстановить";
        };
        close.Start();
    }

    private bool _confirmEmpty;

    private void OnBinMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var (items, bytes) = RecycleBin.Query();
        _confirmEmpty = false;
        BinEmptyItem.Foreground = Brushes.White;
        BinEmptyItem.IsEnabled = items > 0;
        BinEmptyItem.Header = items > 0 ? $"Очистить корзину ({items} шт., {FormatSize(bytes)})" : "Корзина пуста";
    }

    /// <summary>Первый клик спрашивает подтверждение прямо в меню, второй — очищает.</summary>
    private void OnBinEmpty(object sender, RoutedEventArgs e)
    {
        if (!_confirmEmpty)
        {
            _confirmEmpty = true;
            BinEmptyItem.Header = "Нажмите ещё раз — удалить навсегда";
            BinEmptyItem.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
            return;
        }
        _confirmEmpty = false;
        if (BinButton.ContextMenu is { } menu) menu.IsOpen = false;
        RecycleBin.Empty();
        UpdateBinIcon();
    }

    private void OnLanguageSettings(object sender, RoutedEventArgs e) => OpenUri("ms-settings:regionlanguage");
    private void OnTimeSettings(object sender, RoutedEventArgs e) => OpenUri("ms-settings:dateandtime");

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    // ---------- Значки из трея Windows ----------

    private readonly PopupCloser _trayCloser;
    private readonly Dictionary<ManagedShell.WindowsTray.NotifyIcon, FrameworkElement> _trayItems = new();
    private readonly DispatcherTimer _trayRefresh = new() { Interval = TimeSpan.FromSeconds(5) };

    private void RenderTray()
    {
        if (_trayDrag != null) return; // пока тащим значок, раскладку не трогаем
        var s = AppSettings.Current;
        var mode = s.TrayIcons;
        var icons = TrayHost.Icons.ToList();
        foreach (var gone in _trayItems.Keys.Except(icons).ToList()) _trayItems.Remove(gone);

        bool InBar(ManagedShell.WindowsTray.NotifyIcon icon) =>
            s.TrayInBar.TryGetValue(TrayHost.Key(icon), out var v) ? v : mode == "all" || TrayHost.IsPromoted(icon);
        int Order(ManagedShell.WindowsTray.NotifyIcon icon)
        {
            var i = s.TrayOrder.IndexOf(TrayHost.Key(icon));
            return i < 0 ? int.MaxValue : i;
        }

        var bar = icons.Where(InBar).OrderBy(Order).ToList();
        var hidden = icons.Except(bar).OrderBy(Order).ToList();
        Fill(TrayPanel, bar);
        Fill(TrayOverflow, hidden);
        TrayPanel.Visibility = bar.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TrayMoreButton.Visibility = hidden.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hidden.Count == 0) _trayCloser.Close();

        // Пользователь мог поменять видимость значков в Параметрах Windows — перепроверяем.
        if (TrayHost.Running && mode == "windows") { _trayRefresh.Tick -= OnTrayRefresh; _trayRefresh.Tick += OnTrayRefresh; _trayRefresh.Start(); }
        else _trayRefresh.Stop();

        void Fill(Panel panel, List<ManagedShell.WindowsTray.NotifyIcon> list)
        {
            var items = list.Select(TrayItem).ToList();
            if (panel.Children.Cast<UIElement>().SequenceEqual(items)) return;
            foreach (var item in items) (item.Parent as Panel)?.Children.Remove(item);
            panel.Children.Clear();
            foreach (var item in items) panel.Children.Add(item);
        }
    }

    private void OnTrayRefresh(object? sender, EventArgs e) => RenderTray();

    private FrameworkElement TrayItem(ManagedShell.WindowsTray.NotifyIcon icon)
    {
        if (_trayItems.TryGetValue(icon, out var existing)) return existing;

        var image = new Image { Width = 16, Height = 16, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(icon.Icon)) { Source = icon });
        var item = new Border
        {
            Width = 28, Height = 24, CornerRadius = new CornerRadius(6), Margin = new Thickness(1, 0, 1, 0),
            Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)), Child = image,
            VerticalAlignment = VerticalAlignment.Center, Tag = icon,
        };
        item.SetBinding(ToolTipProperty, new System.Windows.Data.Binding(nameof(icon.Title)) { Source = icon });
        ToolTipService.SetInitialShowDelay(item, 400);

        var hover = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
        var idle = item.Background;
        bool down = false;
        uint Mouse() => ManagedShell.Common.Helpers.MouseHelper.GetCursorPositionParam();
        void Place()
        {
            // Программы ставят своё меню рядом со значком — сообщаем им, где он на экране.
            if (PresentationSource.FromVisual(item) == null) return;
            var a = item.PointToScreen(new Point(0, 0));
            var b = item.PointToScreen(new Point(item.ActualWidth, item.ActualHeight));
            icon.Placement = new ManagedShell.Interop.NativeMethods.Rect((int)a.X, (int)a.Y, (int)b.X, (int)b.Y);
        }

        item.MouseEnter += (_, _) => { item.Background = hover; Place(); icon.IconMouseEnter(Mouse()); };
        item.MouseLeave += (_, _) =>
        {
            item.Background = idle;
            if (_trayPress == null) down = false;
            icon.IconMouseLeave(Mouse());
        };
        item.MouseMove += (_, _) =>
        {
            if (_trayDrag == item) { MoveTrayDrag(); return; }
            if (_trayPress == item && TrayCursor(out var p) && (Math.Abs(p.X - _trayPressAt.X) > 5 || Math.Abs(p.Y - _trayPressAt.Y) > 5))
            {
                BeginTrayDrag(item, image);
                return;
            }
            icon.IconMouseMove(Mouse());
        };
        item.LostMouseCapture += (_, _) =>
        {
            if (_trayDrag == item) EndTrayDrag(false);
            _trayPress = null;
        };
        item.AddHandler(MouseDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            down = true;
            Place();
            e.Handled = true;
            if (e.ChangedButton == MouseButton.Left)
            {
                // Левую кнопку программе пока не отдаём: это может быть начало перетаскивания.
                _trayPress = item;
                TrayCursor(out _trayPressAt);
                item.CaptureMouse();
                return;
            }
            icon.IconMouseDown(e.ChangedButton, Mouse(), (int)GetDoubleClickTime());
        }), true);
        item.AddHandler(MouseUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            e.Handled = true;
            if (e.ChangedButton == MouseButton.Left)
            {
                var dragged = _trayDrag == item;
                _trayPress = null;
                if (dragged) { EndTrayDrag(true); item.ReleaseMouseCapture(); down = false; return; }
                item.ReleaseMouseCapture();
            }
            // На тачпаде касание часто приходит только как «отпустили» — тогда досылаем и нажатие.
            if (!down || e.ChangedButton == MouseButton.Left) icon.IconMouseDown(e.ChangedButton, Mouse(), (int)GetDoubleClickTime());
            down = false;
            Place();
            icon.IconMouseUp(e.ChangedButton, Mouse(), (int)GetDoubleClickTime());
            if (e.ChangedButton == MouseButton.Left && item.Parent == TrayOverflow) _trayCloser.Close();
            if (e.ChangedButton == MouseButton.Right)
            {
                // Пока открыто меню программы, список скрытых значков не закрываем.
                _trayMenuUntil = DateTime.Now.AddSeconds(1.5);
                GetWindowThreadProcessId(icon.HWnd, out _trayMenuPid);
            }
        }), true);

        _trayItems[icon] = item;
        return item;
    }

    // ---------- Перетаскивание значков: порядок и перенос между панелью и списком под стрелкой ----------

    private FrameworkElement? _trayPress, _trayDrag;
    private TrayPoint _trayPressAt;
    private System.Windows.Controls.Primitives.Popup? _trayGhost;
    private Border? _trayMarker;

    private void BeginTrayDrag(FrameworkElement item, Image image)
    {
        _trayDrag = item;
        item.Opacity = 0.25;

        // Значок «в руке» едет за курсором.
        var ghostImage = new Image { Width = 18, Height = 18, Source = image.Source };
        RenderOptions.SetBitmapScalingMode(ghostImage, BitmapScalingMode.HighQuality);
        _trayGhost = new System.Windows.Controls.Primitives.Popup
        {
            AllowsTransparency = true, IsHitTestVisible = false, Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute,
            Child = new Border
            {
                Width = 30, Height = 26, CornerRadius = new CornerRadius(7), Child = ghostImage,
                Background = new SolidColorBrush(Color.FromArgb(0x55, 0x30, 0x30, 0x34)), Opacity = 0.95,
            },
        };
        _trayGhost.IsOpen = true;

        // Полоска, куда встанет значок.
        _trayMarker = new Border
        {
            Width = 2, Height = 18, CornerRadius = new CornerRadius(1), Background = Brushes.White, Opacity = 0.8,
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
        };

        // Чтобы было куда бросить, показываем список под стрелкой, даже если он пустой.
        TrayMoreButton.Visibility = Visibility.Visible;
        TrayPanel.Visibility = Visibility.Visible;
        TrayOverflow.MinWidth = 60;
        TrayOverflow.MinHeight = 24;
        if (!_trayCloser.IsOpen) { TrayMoreIcon.Text = "\uE70E"; _trayCloser.Open(); }
        MoveTrayDrag();
    }

    private void MoveTrayDrag()
    {
        if (_trayGhost == null || !TrayCursor(out var p)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        _trayGhost.HorizontalOffset = p.X / dpi.DpiScaleX - 15;
        _trayGhost.VerticalOffset = p.Y / dpi.DpiScaleY - 13;

        (_trayMarker?.Parent as Panel)?.Children.Remove(_trayMarker);
        var (panel, index) = TrayDropTarget(p);
        if (panel != null && _trayMarker != null)
            panel.Children.Insert(TrayChildIndex(panel, index), _trayMarker);
    }

    /// <summary>Куда упадёт значок: панель или список под стрелкой, и на какое место (без учёта самого значка).</summary>
    private (Panel? Panel, int Index) TrayDropTarget(TrayPoint p)
    {
        var point = new Point(p.X, p.Y);
        Panel? panel = null;
        if (TrayPopup.IsOpen && ScreenRect(TrayCard) is { } card && card.Contains(point)) panel = TrayOverflow;
        else if (ScreenRect(BarRoot) is { } bar && bar.Contains(point)) panel = TrayPanel;
        if (panel == null) return (null, 0);

        var others = panel.Children.OfType<FrameworkElement>().Where(c => c != _trayDrag && c != _trayMarker && c.Tag != null).ToList();
        var index = others.Count;
        for (var i = 0; i < others.Count; i++)
        {
            if (ScreenRect(others[i]) is not { } r) continue;
            var sameRow = point.Y < r.Bottom;
            if (panel == TrayPanel ? point.X < r.Left + r.Width / 2 : sameRow && (point.Y < r.Top || point.X < r.Left + r.Width / 2))
            {
                index = i;
                break;
            }
        }
        return (panel, index);
    }

    /// <summary>Номер в Children с учётом того, что в панели лежат сам перетаскиваемый значок и полоска.</summary>
    private int TrayChildIndex(Panel panel, int index)
    {
        var seen = 0;
        for (var i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is FrameworkElement { Tag: not null } c && c != _trayDrag)
            {
                if (seen == index) return i;
                seen++;
            }
        }
        return panel.Children.Count;
    }

    private void EndTrayDrag(bool drop)
    {
        var item = _trayDrag;
        _trayDrag = null;
        if (_trayGhost != null) _trayGhost.IsOpen = false;
        _trayGhost = null;
        (_trayMarker?.Parent as Panel)?.Children.Remove(_trayMarker);
        TrayOverflow.MinWidth = 0;
        TrayOverflow.MinHeight = 0;
        if (item == null) return;
        item.Opacity = 1;

        if (drop && TrayCursor(out var p) && item.Tag is ManagedShell.WindowsTray.NotifyIcon icon)
        {
            var (panel, index) = TrayDropTarget(p);
            if (panel != null)
            {
                List<string> Keys(Panel from) => from.Children.OfType<FrameworkElement>()
                    .Where(c => c != item && c.Tag is ManagedShell.WindowsTray.NotifyIcon)
                    .Select(c => TrayHost.Key((ManagedShell.WindowsTray.NotifyIcon)c.Tag)).ToList();
                var bar = Keys(TrayPanel);
                var overflow = Keys(TrayOverflow);
                var key = TrayHost.Key(icon);
                var target = panel == TrayPanel ? bar : overflow;
                target.Insert(Math.Clamp(index, 0, target.Count), key);
                AppSettings.Update(s =>
                {
                    s.TrayInBar[key] = panel == TrayPanel;
                    var shown = bar.Concat(overflow).ToList();
                    s.TrayOrder = shown.Concat(s.TrayOrder.Where(k => !shown.Contains(k))).ToList();
                });
            }
        }
        RenderTray();
    }

    private static Rect? ScreenRect(FrameworkElement element)
    {
        if (!element.IsVisible || PresentationSource.FromVisual(element) == null) return null;
        var a = element.PointToScreen(new Point(0, 0));
        var b = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return new Rect(a, b);
    }

    [StructLayout(LayoutKind.Sequential)] private struct TrayPoint { public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")] private static extern bool TrayCursor(out TrayPoint p);

    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();

    private DateTime _trayMenuUntil;
    private uint _trayMenuPid;

    /// <summary>Открыто ли сейчас меню, вызванное правым кликом по значку в трее.</summary>
    private bool TrayMenuOpen()
    {
        if (DateTime.Now < _trayMenuUntil) return true;
        if (_trayMenuPid == 0) return false;
        // Обычные меню Windows — окно класса #32768.
        var menu = FindWindowW("#32768", null);
        if (menu != IntPtr.Zero && IsWindowVisible(menu)) return true;
        // Свои меню (Qt, Electron и т. п.) — всплывающее окно той же программы на переднем плане.
        var fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out var pid);
        if (pid == _trayMenuPid && (GetWindowLongW(fg, -20) & (0x8 | 0x80)) != 0) return true; // TOPMOST | TOOLWINDOW
        _trayMenuPid = 0;
        return false;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string cls, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(IntPtr hwnd, int index);

    private void OnTrayMore(object sender, RoutedEventArgs e)
    {
        _trayCloser.Toggle(() => TrayMoreIcon.Text = "\uE70E");
    }
}
