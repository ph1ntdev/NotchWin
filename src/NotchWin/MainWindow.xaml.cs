using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using NotchWin.Services;
using Windows.Devices.Radios;
using static NotchWin.NativeMethods;

namespace NotchWin;

public partial class MainWindow : Window
{
    // Размеры в DIP. Свёрнутая челка ровно по высоте панели, поэтому окна под ней ничего не теряют.
    // Ширина, высота и скругления свёрнутой челки задаются в настройках.
    private static double CollapsedWidth => Math.Clamp(AppSettings.Current.NotchWidth, 180, 300);
    private static double CollapsedHeight => Layout.BarHeight;
    private const double ActivityWidth = 340;
    private const double ExpandedWidth = 400;
    private static double CollapsedRadius => Math.Clamp(AppSettings.Current.NotchRadius, 0, 16);
    private static double ExpandedRadius => Math.Clamp(AppSettings.Current.NotchExpandedRadius, 12, 40);
    private const double EarRadius = 6;

    private enum Page { Media, Shelf, Timer, Note }

    private readonly Spring _width = new(CollapsedWidth);
    private readonly Spring _height = new(CollapsedHeight);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastFrame;
    private bool _animating;
    private bool _expanded;
    private double _expandedHeight = 130;
    /// <summary>Ширина «крыла» с каждой стороны свёрнутой челки (таймер, загрузка). 0 — крыльев нет.</summary>
    private double _wing;
    private Page _page = Page.Media;

    private readonly DispatcherTimer _collapseDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _barsTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _activityTimer = new();
    private readonly DispatcherTimer _timelineTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DateTime _startedAt = DateTime.Now;
    private int _tickCount;

    private readonly MediaService _media = new();
    private readonly AudioVisualizer _visualizer = new();
    private readonly VolumeWatcher _volume = new();
    private readonly TransferMonitor _transfers = new();
    private readonly TimerService _timer = new();
    private readonly RadioService _radio = new();

    private readonly Rectangle[] _bars = new Rectangle[AudioVisualizer.BandCount];
    private readonly float[] _bandTargets = new float[AudioVisualizer.BandCount];
    private readonly double[] _bandShown = new double[AudioVisualizer.BandCount];
    private bool _playing;
    private Color _accent = Color.FromRgb(0x4A, 0xDE, 0x80);

    public MainWindow()
    {
        InitializeComponent();

        _width.Stiffness = 300;
        _height.Stiffness = 340;

        CreateBars();
        // Щелчок колеса (120) сдвигает полку примерно на две трети карточки.
        _shelfScroll = SmoothScroll.Attach(ShelfScroller, horizontal: true, factor: 0.45);
        foreach (var list in new[] { TranslateScroll, WifiScroll, SendScroll, BluetoothScroll })
            SmoothScroll.Attach(list, horizontal: false, factor: 0.4);
        _openDelay.Tick += (_, _) =>
        {
            _openDelay.Stop();
            if (IsMouseOver) SetExpanded(true);
        };
        AppSettings.Changed += ApplySettings;
        AppSettings.RecordingHotkeyChanged += RegisterTranslateHotkey;
        _collapseDelay.Tick += (_, _) =>
        {
            _collapseDelay.Stop();
            // Мышь ушла с челки в окно Wi-Fi/Bluetooth или в меню правого клика — не сворачиваемся.
            if (IsOverSidePopup() || AnyMenuOpen) return;
            SetExpanded(false);
        };
        // Меню правого клика — отдельное окно, и мышь в нём для челки «ушла». Пока оно открыто, держим челку.
        AddHandler(ContextMenuOpeningEvent, new ContextMenuEventHandler((_, _) => _collapseDelay.Stop()), true);
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((m, _) =>
        {
            OpenMenus.Add((ContextMenu)m);
            _collapseDelay.Stop();
        }));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler((m, _) =>
        {
            OpenMenus.Remove((ContextMenu)m);
            if (!AnyMenuOpen && _expanded && !IsMouseOver) StartCollapseDelay();
        }));
        // Маленькая обложка следует за прозрачностью основной части (после «живого» уведомления).
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(OpacityProperty, typeof(UIElement))
            .AddValueChanged(CompactSlots, (_, _) => ApplyFrame());
        _tick.Tick += (_, _) => OnTick();
        _barsTimer.Tick += (_, _) => AnimateBars();
        _activityTimer.Tick += (_, _) => HideActivity();
        _timelineTimer.Tick += (_, _) => UpdateTimeline();
        // Нажатие на таймлайн слушаем даже если его уже кто-то пометил обработанным:
        // при тапе по тачпаду нажатие иногда до нас не доходило, приходило только отпускание.
        TimelineSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnTimelineDown), true);
        _media.TimelineChanged += () => Dispatcher.BeginInvoke(UpdateTimeline);
        TimerRuler.ValueChanged += minutes => _timer.SetLength(TimeSpan.FromMinutes(minutes));
        TimerRuler.PreviewChanged += minutes =>
        {
            // Время над шкалой меняется прямо во время прокрутки.
            if (!_timer.IsActive) TimerBigText.Text = TimeSpan.FromMinutes(minutes).ToString(minutes >= 60 ? @"h\:mm\:ss" : @"mm\:ss");
        };

        _wifiCloser = new PopupCloser(WifiPopup, WifiCard, WifiToggle);
        _btCloser = new PopupCloser(BluetoothPopup, BluetoothCard, BluetoothToggle);
        _sideCloseDelay.Tick += (_, _) => { _sideCloseDelay.Stop(); CloseSidePopupsIfLeft(); };
        _sendCloser = new PopupCloser(SendPopup, SendCard, ShelfSendButton);
        _hoverTimer.Tick += (_, _) => CheckHoverOpen();
        Deactivated += (_, _) => OnNotchDeactivated();
        // Прошлая версия глушила потоки Discord в Windows, и Windows это запомнила — снимаем.
        Task.Run(() => { DiscordAudio.SetMicMuted(false); DiscordAudio.SetSoundMuted(false); });
        Activated += (_, _) =>
        {
            if (_noteMode && _expanded) Dispatcher.BeginInvoke(() => Keyboard.Focus(NoteBox), DispatcherPriority.Input);
        };

        _timer.Changed += UpdateTimerUi;
        _timer.Finished += OnTimerFinished;

        _volume.VolumeChanged += (level, muted) => Dispatcher.BeginInvoke(() => ShowVolume(level, muted));
        _volume.BrightnessChanged += b => Dispatcher.BeginInvoke(() =>
        {
            if (AppSettings.Current.ShowVolumeInNotch) ShowActivity("", "", b / 100.0, $"{b}%", TimeSpan.FromSeconds(1.6));
        });

        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) =>
        {
            ApplySettings();
            TrayHost.Balloon += (title, text, icon) => Dispatcher.BeginInvoke(() =>
                ShowActivity("\uE7E7", string.IsNullOrWhiteSpace(title) ? text : title,
                    null, string.IsNullOrWhiteSpace(title) ? "" : text, TimeSpan.FromSeconds(5), icon));
            LoadShelf();
            UpdateTimerUi();
            ApplyFrame();
            _tick.Start();
            _media.Changed += s => Dispatcher.BeginInvoke(() => ShowMedia(s));
            try { await _media.StartAsync(); } catch { /* медиа API недоступен, челка работает без плеера */ }
        };
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(PositionOnPrimaryMonitor);
    }

    /// <summary>Останавливает захват звука и прочие фоновые вещи перед выходом.</summary>
    public void ReleaseResources()
    {
        _visualizer.Dispose();
        _volume.Dispose();
    }

    // ---------- Окно: стили, позиция, поверх всех ----------

    private IntPtr _hwnd;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        SetActivatable(false);
        SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        PositionOnPrimaryMonitor();

        // Следим за буфером обмена, чтобы ловить скриншоты Win+Shift+S.
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        AddClipboardFormatListener(_hwnd);

        RegisterTranslateHotkey();

        // Флешки, которые уже вставлены при запуске.
        if (AppSettings.Current.ShowDrives) _drives.AddRange(DriveService.Removable());
        RenderShelf();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x031D /* WM_CLIPBOARDUPDATE */) Dispatcher.BeginInvoke(OnClipboardChanged);
        else if (msg == 0x0312 /* WM_HOTKEY */ && wParam.ToInt32() == TranslateHotkeyId) Dispatcher.BeginInvoke(TranslateSelection);
        else if (msg == 0x0219 /* WM_DEVICECHANGE */) OnDeviceChange(wParam, lParam);
        else if (msg == 0x020E /* WM_MOUSEHWHEEL: тачпад влево-вправо */ && ShelfScroller.IsMouseOver)
        {
            ScrollShelf((short)((wParam.ToInt64() >> 16) & 0xFFFF));
            handled = true;
            return new IntPtr(1);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Обычно челка не забирает фокус у активного окна. Для ввода пароля Wi-Fi фокус нужен, включаем на время.
    /// </summary>
    private void SetActivatable(bool activatable)
    {
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex = activatable ? ex & ~WS_EX_NOACTIVATE : ex | WS_EX_NOACTIVATE;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        if (activatable) ForceForeground();
    }

    /// <summary>
    /// Windows не даёт фоновой программе забрать фокус клавиатуры, и Activate() молча не срабатывает:
    /// буквы уходят в прежнее окно. Временно подключаемся к вводу текущего окна, тогда разрешают.
    /// </summary>
    private void ForceForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == _hwnd) return;
        var theirThread = GetWindowThreadProcessId(foreground, out _);
        var ourThread = GetCurrentThreadId();
        var attached = theirThread != ourThread && AttachThreadInput(ourThread, theirThread, true);
        try
        {
            BringWindowToTop(_hwnd);
            SetForegroundWindow(_hwnd);
            SetFocus(_hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(ourThread, theirThread, false);
        }
        Activate();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(PositionOnPrimaryMonitor);
    }

    /// <summary>Ставит окно по центру верхнего края основного монитора с учётом его DPI.</summary>
    private void PositionOnPrimaryMonitor()
    {
        if (_hwnd == IntPtr.Zero) return;

        var monitor = Screens.Target();
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var scale = 1.0;
        if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0)
            scale = dpiX / 96.0;

        var w = (int)Math.Round(Width * scale);
        var h = (int)Math.Round(Height * scale);
        var screenWidth = info.rcMonitor.Right - info.rcMonitor.Left;
        var x = info.rcMonitor.Left + (screenWidth - w) / 2;

        SetWindowPos(_hwnd, HWND_TOPMOST, x, info.rcMonitor.Top, w, h, SWP_NOACTIVATE);
    }

    public void BringToTop()
    {
        if (_hwnd == IntPtr.Zero || Visibility != Visibility.Visible) return;
        // Иначе челка залезает поверх открытого меню и прячет половину пунктов.
        if (AnyMenuOpen) return;
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        // Окна Wi-Fi и Bluetooth лежат поверх челки (значок в их углу), поднимаем их следом.
        foreach (var popup in new[] { WifiPopup, BluetoothPopup, SendPopup, TranslatePopup })
            if (popup.IsOpen && PresentationSource.FromVisual(popup.Child) is HwndSource source)
                SetWindowPos(source.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void OnTick()
    {
        if (_hwnd == IntPtr.Zero) return;
        _tickCount++;

        // «Свернуть все окна» может спрятать челку. Если она должна быть видна, возвращаем.
        if (Visibility == Visibility.Visible && (ShellNative.IsIconic(_hwnd) || !ShellNative.IsWindowVisible(_hwnd)))
        {
            ShellNative.ShowWindow(_hwnd, ShellNative.SW_SHOWNOACTIVATE);
            PositionOnPrimaryMonitor();
        }
        // Другие topmost-окна (в том числе наша панель) могут перекрыть челку. Возвращаемся наверх.
        BringToTop();

        if (_tickCount % 5 == 1 && AppSettings.Current.ShowVolumeInNotch) OsdHider.TryHide();
        if (_tickCount % 2 == 0) _ = PollPrivacyAsync();
        if (_tickCount % 2 == 1) _ = PollNotificationsAsync();
        CheckBattery();
        _ = PollTransfersAsync();
        UpdateDiscordUi();
    }

    private void OnExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        SetExpanded(false);
        SettingsWindow.ShowSingle();
    }

    // ---------- Настройки ----------

    private string? _appliedTranslate;
    private string? _appliedPosition;
    private bool? _appliedLocalSend;

    /// <summary>Настройку поменяли — применяем сразу: размеры, вкладки, клавиши, монитор.</summary>
    private void ApplySettings()
    {
        var s = AppSettings.Current;

        // Размеры свёрнутой челки.
        CollapsedView.Height = CollapsedHeight;
        ExpandedView.RowDefinitions[0].Height = new GridLength(CollapsedHeight);
        CompactSlots.Width = CollapsedWidth - 24;
        Shape.Opacity = Math.Clamp(s.NotchOpacity, 0.3, 1);
        Shape.StrokeThickness = s.NotchOutline ? 1 : 0;
        _collapseDelay.Interval = TimeSpan.FromMilliseconds(Math.Clamp(s.CloseDelayMs, 50, 3000));
        Bars.Visibility = _playing && s.ShowEqualizer ? Visibility.Visible : Visibility.Collapsed;
        if (!s.ShowVolumeInNotch) OsdHider.Restore();
        ShelfSendButton.Visibility = s.LocalSendEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (_appliedLocalSend != s.LocalSendEnabled)
        {
            _appliedLocalSend = s.LocalSendEnabled;
            if (!s.LocalSendEnabled) _sendCloser.Close();
            RenderShelf(); // пункт «Отправить через LocalSend» в меню файлов
        }

        ApplyTabs();

        // Монитор и высота панели: переставляем окно.
        var position = $"{s.Monitor}|{CollapsedHeight}";
        if (position != _appliedPosition)
        {
            _appliedPosition = position;
            PositionOnPrimaryMonitor();
        }
        if (s.TranslateHotkey != _appliedTranslate)
        {
            _appliedTranslate = s.TranslateHotkey;
            RegisterTranslateHotkey();
        }

        // Флешки на полке.
        if (!s.ShowDrives && _drives.Count > 0) { _drives.Clear(); RenderShelf(); }
        else if (s.ShowDrives && _drives.Count == 0 && _hwnd != IntPtr.Zero)
        {
            _drives.AddRange(DriveService.Removable());
            if (_drives.Count > 0) RenderShelf();
        }
        TrimShelf();

        UpdateDiscordUi();
        UpdateTargets();
    }

    private static readonly string[] AllTabs = { "media", "shelf", "timer", "note" };

    private Button TabButton(string id) => id switch
    {
        "shelf" => TabShelf,
        "timer" => TabTimer,
        "note" => TabNote,
        _ => TabMedia,
    };

    private static Page PageOf(string id) => id switch
    {
        "shelf" => Page.Shelf,
        "timer" => Page.Timer,
        "note" => Page.Note,
        _ => Page.Media,
    };

    /// <summary>Все вкладки в порядке из настроек (новые, которых нет в списке, — в конце).</summary>
    private static List<string> TabOrder()
    {
        var order = AppSettings.Current.Tabs.Where(AllTabs.Contains).Distinct().ToList();
        order.AddRange(AllTabs.Except(order));
        return order;
    }

    private static List<string> VisibleTabs()
    {
        var visible = TabOrder().Where(t => !AppSettings.Current.HiddenTabs.Contains(t)).ToList();
        return visible.Count > 0 ? visible : new List<string> { "media" };
    }

    private void ApplyTabs()
    {
        var visible = VisibleTabs();
        TabsPanel.Children.Clear();
        foreach (var id in TabOrder())
        {
            var tab = TabButton(id);
            tab.Visibility = visible.Contains(id) ? Visibility.Visible : Visibility.Collapsed;
            TabsPanel.Children.Add(tab);
        }
        // Открытая вкладка скрыта — переходим на первую видимую.
        if (_expanded && !visible.Select(PageOf).Contains(_page)) ShowPage(PageOf(visible[0]));
    }

    private Page StartPage()
    {
        var visible = VisibleTabs().Select(PageOf).ToList();
        var start = AppSettings.Current.StartTab == "last" ? _page : PageOf(AppSettings.Current.StartTab);
        return visible.Contains(start) ? start : visible[0];
    }

    private void StartCollapseDelay()
    {
        _collapseDelay.Stop();
        _collapseDelay.Start();
    }

    // ---------- Раскрытие и анимация ----------

    private readonly DispatcherTimer _openDelay = new();

    private void OnMouseEnterIsland(object sender, MouseEventArgs e)
    {
        _collapseDelay.Stop();
        var delay = AppSettings.Current.OpenDelayMs;
        if (_expanded || delay <= 0) { SetExpanded(true); return; }
        // Задержка из настроек: челка не раскрывается, если мышь просто пролетела мимо.
        _openDelay.Interval = TimeSpan.FromMilliseconds(delay);
        _openDelay.Start();
    }

    private void OnMouseLeaveIsland(object sender, MouseEventArgs e)
    {
        _openDelay.Stop();
        // Пока вводят пароль Wi-Fi, челку не сворачиваем.
        if (PasswordRow.Visibility == Visibility.Visible && WifiPassword.IsKeyboardFocusWithin) return;
        // Пишут заметку — тоже не сворачиваем, пока окно в фокусе.
        if (NoteBox.IsKeyboardFocusWithin && IsActive) return;
        StartCollapseDelay();
    }

    private bool _openingForDrop;
    /// <summary>Открытые меню правого клика (во всём приложении). Пока они есть, челку не сворачиваем и не поднимаем над ними.</summary>
    private static readonly HashSet<ContextMenu> OpenMenus = new();
    private static bool AnyMenuOpen
    {
        get
        {
            OpenMenus.RemoveWhere(m => !m.IsOpen);
            return OpenMenus.Count > 0;
        }
    }

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        ExpandedView.IsHitTestVisible = expanded;
        if (expanded)
        {
            // Открываемся на вкладке из настроек. Исключение — перетаскивание файла на полку.
            ShowPage(_openingForDrop ? Page.Shelf : StartPage());
            _openingForDrop = false;
            _ = RefreshRadiosAsync();
            _expandedAt = DateTime.Now;
        }
        else
        {
            _timelineTimer.Stop();
            _hoverTimer.Stop();
            _wifiCloser.Close();
            _btCloser.Close();
            _sendCloser.Close();
            HideWifiOverlay();
            DropZones.Visibility = Visibility.Collapsed;
            if (NoteBox.IsKeyboardFocusWithin) Keyboard.ClearFocus();
            SetNoteMode(false);
        }
        UpdateTargets();
    }

    /// <summary>Пересчитывает, какого размера должна быть челка сейчас.</summary>
    private void UpdateTargets()
    {
        if (_expanded)
        {
            _expandedHeight = MeasureExpanded();
            _width.Target = ExpandedWidth;
            _height.Target = _expandedHeight;
            // Раскрытие пружинистое, как у Dynamic Island.
            _width.Damping = _height.Damping = 0.74;
            _width.Stiffness = 300;
            _height.Stiffness = 340;
        }
        else
        {
            var collapsed = CollapsedWidth + 2 * _wing;
            _width.Target = ActivityView.Visibility == Visibility.Visible ? Math.Max(ActivityWidth, collapsed) : collapsed;
            _height.Target = CollapsedHeight;
            // Сворачивание без перелёта: иначе челка «подпрыгивает» ниже панели и выглядит дёргано.
            _width.Damping = _height.Damping = 1.0;
            _width.Stiffness = 420;
            _height.Stiffness = 460;
        }
        StartAnimation();
    }

    private double MeasureExpanded()
    {
        ExpandedView.Width = ExpandedWidth;
        ExpandedView.Measure(new Size(ExpandedWidth, double.PositiveInfinity));
        return Math.Max(110, Math.Ceiling(ExpandedView.DesiredSize.Height));
    }

    private void StartAnimation()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = _clock.Elapsed;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var dt = Math.Min((now - _lastFrame).TotalSeconds, 0.05);
        _lastFrame = now;

        _width.Step(dt);
        _height.Step(dt);
        ApplyFrame();

        // Когда пружины успокоились, отписываемся, чтобы не грузить CPU в простое.
        if (_width.IsAtRest && _height.IsAtRest)
        {
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
        }
    }

    private void ApplyFrame()
    {
        var w = _width.Value;
        var h = _height.Value;
        var collapsedHeight = CollapsedHeight;
        var progress = Math.Clamp((h - collapsedHeight) / Math.Max(1, _expandedHeight - collapsedHeight), 0, 1.2);
        var radius = CollapsedRadius + (ExpandedRadius - CollapsedRadius) * Math.Min(progress, 1);

        Shape.Data = BuildNotchGeometry(ActualWidth > 0 ? ActualWidth : Width, w, h, radius, EarRadius);
        Body.Width = w;
        Body.Height = h;
        Canvas.SetLeft(ExpandedView, (w - ExpandedWidth) / 2);

        ExpandedView.Opacity = Math.Clamp((progress - 0.45) / 0.55, 0, 1);
        CollapsedView.Opacity = Math.Clamp(1 - progress * 3, 0, 1);
        PlaceMorphCover(w, Math.Clamp(progress, 0, 1));

        // Окна Wi-Fi и Bluetooth привязаны к своим значкам: пока челка раскрывается, едут вместе с ними.
        foreach (var popup in new[] { WifiPopup, BluetoothPopup })
        {
            if (!popup.IsOpen) continue;
            var offset = popup.HorizontalOffset;
            popup.HorizontalOffset = offset + 0.01;
            popup.HorizontalOffset = offset;
        }
    }

    /// <summary>
    /// Обложка не исчезает и не появляется заново: она плавно растёт из маленькой
    /// (слева в свёрнутой челке) в большую (место CoverSlot в плеере) и обратно.
    /// </summary>
    private void PlaceMorphCover(double bodyWidth, double p)
    {
        if (MorphCoverBrush.ImageSource == null) { MorphCover.Visibility = Visibility.Collapsed; return; }
        MorphCover.Visibility = Visibility.Visible;

        // Свёрнутое положение: левый край основной части свёрнутой челки. Раскрытое: место обложки в плеере.
        // Оба положения считаем от центра челки, поэтому обложка едет по прямой, а не по дуге.
        const double smallSize = 20, bigSize = 60;
        var smallY = (CollapsedHeight - smallSize) / 2;
        var smallX = (bodyWidth - CollapsedWidth) / 2 + 12;
        // Отступ 18 = радиус угла челки (30) минус радиус угла обложки (12): углы концентричны.
        var bigX = (bodyWidth - ExpandedWidth) / 2 + 18;
        var bigY = CollapsedHeight + 8;

        var toMedia = _page == Page.Media;
        var t = toMedia ? p : 0;
        var size = smallSize + (bigSize - smallSize) * t;
        MorphCover.Width = MorphCover.Height = size;
        MorphCover.CornerRadius = new CornerRadius(5 + 7 * t);
        Canvas.SetLeft(MorphCover, smallX + (bigX - smallX) * t);
        Canvas.SetTop(MorphCover, smallY + (bigY - smallY) * t);

        // Во время «живого» уведомления маленькая обложка уступает место значку уведомления.
        var compact = CompactSlots.Opacity;
        MorphCover.Opacity = toMedia ? Math.Max(compact * (1 - p), p) : compact * (1 - p);
    }

    /// <summary>
    /// Форма челки: прямоугольник, прижатый к верхнему краю экрана, со скруглёнными
    /// нижними углами и вогнутыми "ушками" сверху, которые плавно переходят в край экрана.
    /// </summary>
    private static Geometry BuildNotchGeometry(double windowWidth, double w, double h, double r, double ear)
    {
        var cx = windowWidth / 2;
        var left = cx - w / 2;
        var right = cx + w / 2;
        r = Math.Max(0, Math.Min(r, Math.Min(w / 2, h - ear)));

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(left - ear, 0), isFilled: true, isClosed: true);
            c.LineTo(new Point(right + ear, 0), true, false);
            c.ArcTo(new Point(right, ear), new Size(ear, ear), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(right, h - r), true, false);
            c.ArcTo(new Point(right - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(left + r, h), true, false);
            c.ArcTo(new Point(left, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(left, ear), true, false);
            c.ArcTo(new Point(left - ear, 0), new Size(ear, ear), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    // ---------- Вкладки ----------

    private void OnTab(object sender, RoutedEventArgs e)
    {
        var page = sender == TabShelf ? Page.Shelf
            : sender == TabTimer ? Page.Timer
            : sender == TabNote ? Page.Note
            : Page.Media;
        ShowPage(page);
        UpdateTargets();
    }

    private void ShowPage(Page page)
    {
        _page = page;
        MediaPage.Visibility = page == Page.Media ? Visibility.Visible : Visibility.Collapsed;
        ShelfPage.Visibility = page == Page.Shelf ? Visibility.Visible : Visibility.Collapsed;
        TimerPage.Visibility = page == Page.Timer ? Visibility.Visible : Visibility.Collapsed;
        NotePage.Visibility = page == Page.Note ? Visibility.Visible : Visibility.Collapsed;
        if (page == Page.Note) UpdateNoteStatus();
        SetNoteMode(page == Page.Note);

        foreach (var (tab, p) in new[] { (TabMedia, Page.Media), (TabShelf, Page.Shelf), (TabTimer, Page.Timer), (TabNote, Page.Note) })
            tab.Opacity = p == page ? 1 : 0.45;

        if (page == Page.Media) { UpdateTimeline(); _timelineTimer.Start(); }
        else _timelineTimer.Stop();
        ApplyFrame();
    }

    // ---------- «Живые» уведомления в свёрнутой челке ----------

    private void ShowActivity(string icon, string text, double? progress, string value, TimeSpan duration,
        ImageSource? image = null, Color? fill = null, bool animateFill = false)
    {
        ActivityIcon.Text = icon;
        ActivityIcon.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
        ActivityImage.ImageSource = image;
        ActivityImageHost.Visibility = image == null ? Visibility.Collapsed : Visibility.Visible;
        ActivityIcon.Foreground = fill is Color c && c != Colors.White ? new SolidColorBrush(c) : Brushes.White;
        ActivityText.Text = text;
        ActivityText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        ActivityTrack.Visibility = progress == null ? Visibility.Collapsed : Visibility.Visible;
        ActivityFill.Background = new SolidColorBrush(fill ?? Colors.White);
        ActivityValue.Text = value;
        // Без полосы прогресса (уведомления) тексту можно занять больше места.
        ActivityValue.MaxWidth = progress == null ? 200 : 120;
        ActivityValue.Margin = new Thickness(progress == null ? 10 : 0, 0, 0, 0);

        var wasVisible = ActivityView.Visibility == Visibility.Visible;
        ActivityView.Visibility = Visibility.Visible;
        if (!wasVisible)
        {
            ActivityView.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            RefreshCompactOpacity();
            UpdateTargets();
        }

        // Ширину полосы прогресса ставим после раскладки, когда известна ширина дорожки.
        Dispatcher.BeginInvoke(() =>
        {
            if (progress is not double p) return;
            var width = Math.Clamp(p, 0, 1) * ActivityTrack.ActualWidth;
            if (animateFill)
            {
                // Зарядка: полоска «наливается» от нуля до текущего заряда.
                ActivityFill.BeginAnimation(WidthProperty, new DoubleAnimation(0, width, TimeSpan.FromMilliseconds(1100))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
            }
            else
            {
                ActivityFill.BeginAnimation(WidthProperty, null);
                ActivityFill.Width = width;
            }
        }, DispatcherPriority.Loaded);

        _activityTimer.Stop();
        _activityTimer.Interval = duration;
        _activityTimer.Start();
    }

    private void HideActivity()
    {
        _activityTimer.Stop();
        if (ActivityView.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) =>
        {
            if (_activityTimer.IsEnabled) return; // за это время пришло новое уведомление
            ActivityView.Visibility = Visibility.Collapsed;
            UpdateTargets();
        };
        ActivityView.BeginAnimation(OpacityProperty, fade);
        _activityHiding = true;
        RefreshCompactOpacity();
        _activityHiding = false;
    }

    private bool _activityHiding;

    /// <summary>
    /// Что видно в свёрнутой челке: во время «живого» уведомления всё остальное уступает ему место,
    /// иначе — основная часть и те крылья, в которых что-то есть.
    /// </summary>
    private void RefreshCompactOpacity()
    {
        var activity = ActivityView.Visibility == Visibility.Visible && !_activityHiding;
        Fade(CompactSlots, activity ? 0 : 1);
        Fade(LeftWing, !activity && HasVisibleChild(LeftWing) ? 1 : 0);
        Fade(RightWing, !activity && HasVisibleChild(RightWing) ? 1 : 0);
    }

    private static bool HasVisibleChild(Panel panel) =>
        panel.Children.OfType<UIElement>().Any(c => c.Visibility == Visibility.Visible);

    private readonly Dictionary<UIElement, double> _fadeTargets = new();

    private void Fade(UIElement element, double to)
    {
        if (_fadeTargets.TryGetValue(element, out var current) && current == to) return;
        _fadeTargets[element] = to;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 220 : 140)));
    }

    // ---------- Крылья свёрнутой челки ----------

    /// <summary>
    /// Раскладывает фоновые «живые» штуки (таймер, загрузка) по крыльям слева и справа от челки.
    /// Порядок в списке — приоритет: кто важнее, тот получает своё любимое место. Новые виджеты
    /// добавляются сюда же, так они никогда не налезут на обложку, точки камеры или эквалайзер.
    /// </summary>
    private void LayoutWings()
    {
        var items = new List<(FrameworkElement Element, bool PreferLeft)>();
        if (DiscordWing.Visibility == Visibility.Visible) items.Add((DiscordWing, true));
        if (CompactTimer.Visibility == Visibility.Visible) items.Add((CompactTimer, true));
        if (TransferRing.Visibility == Visibility.Visible) items.Add((TransferRing, false));

        FrameworkElement? left = null, right = null;
        foreach (var (element, preferLeft) in items)
        {
            if (preferLeft && left == null) left = element;
            else if (!preferLeft && right == null) right = element;
            else if (left == null) left = element;
            else if (right == null) right = element;
            // Если оба места заняты, остальное видно только в раскрытой челке.
        }

        foreach (var element in new FrameworkElement[] { DiscordWing, CompactTimer, TransferRing })
        {
            var target = element == left ? LeftWing : element == right ? RightWing : null;
            if (target == null || element.Parent == target) continue;
            ((Panel)element.Parent).Children.Remove(element);
            target.Children.Add(element);
        }

        static double Measure(FrameworkElement? e)
        {
            if (e == null) return 0;
            e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return e.DesiredSize.Width;
        }
        var content = Math.Max(Measure(left), Measure(right));
        // Крылья одинаковые с обеих сторон, чтобы челка оставалась по центру.
        var wing = content > 0 ? Math.Ceiling(content) + 20 : 0;
        if (Math.Abs(wing - _wing) > 0.5)
        {
            _wing = wing;
            if (!_expanded) UpdateTargets();
        }
        RefreshCompactOpacity();
    }

    private void ShowVolume(float level, bool muted)
    {
        if (!AppSettings.Current.ShowVolumeInNotch) return;
        // При запуске Windows присылает текущее значение — это не изменение, не показываем.
        if ((DateTime.Now - _startedAt).TotalSeconds < 2) return;
        var icon = muted || level < 0.01f ? "" : level < 0.34f ? "" : level < 0.67f ? "" : "";
        ShowActivity(icon, "", muted ? 0 : level, muted ? "Выкл" : $"{Math.Round(level * 100)}%", TimeSpan.FromSeconds(1.6));
    }

    // ---------- Камера и микрофон ----------

    private async Task PollPrivacyAsync()
    {
        var (camera, mic, apps) = await Task.Run(() =>
        {
            var (c, m) = PrivacyMonitor.Check();
            return (c, m, m ? PrivacyMonitor.MicrophoneApps() : new List<string>());
        });
        CameraDot.Visibility = camera ? Visibility.Visible : Visibility.Collapsed;
        MicDot.Visibility = mic ? Visibility.Visible : Visibility.Collapsed;
        UpdateDiscordCall(apps.Any(IsDiscord));
    }

    // ---------- Загрузки и копирование ----------

    private bool _transferBusy;

    private async Task PollTransfersAsync()
    {
        if (_transferBusy) return;
        _transferBusy = true;
        try
        {
            var state = _sendState ?? await Task.Run(_transfers.Poll);
            ShowTransfer(state);
        }
        catch { }
        finally { _transferBusy = false; }
    }

    private string? _transferKey;
    private DateTime _transferStarted;

    /// <summary>
    /// Начало загрузки ненадолго показываем широким уведомлением, потом оно «сжимается»
    /// в кружок справа, который заполняется по мере передачи. Подробности — в раскрытой челке.
    /// </summary>
    private void ShowTransfer(TransferState? state)
    {
        if (state == null || state.Finished)
        {
            if (state != null && _transferKey != null)
                ShowActivity("\uE73E", state.Title, null, state.Detail, TimeSpan.FromSeconds(3));
            _transferKey = null;
            TransferRing.Visibility = Visibility.Collapsed;
            TransferInfo.Visibility = Visibility.Collapsed;
            LayoutWings();
            return;
        }

        var icon = state.Kind switch { "copy" => "\uE8C8", "send" => "\uE72D", _ => "\uE896" };
        var key = state.Kind + state.Title;
        if (_transferKey != key)
        {
            _transferKey = key;
            _transferStarted = DateTime.Now;
            ShowActivity(icon, state.Kind switch { "copy" => "Копирование", "send" => "LocalSend", _ => "Загрузка началась" },
                null, state.Title,
                TimeSpan.FromSeconds(2.5));
        }

        TransferRing.Visibility = Visibility.Visible;
        // Копирование сообщает точный процент. Браузер полный размер файла не сообщает,
        // поэтому для загрузки кружок заполняется примерно: быстро в начале, медленнее к концу.
        var progress = state.Progress
            ?? 0.92 * (1 - Math.Exp(-(DateTime.Now - _transferStarted).TotalSeconds / 25));
        TransferArc.Data = ArcGeometry(Math.Clamp(progress, 0.03, 1));
        LayoutWings();

        var wasVisible = TransferInfo.Visibility == Visibility.Visible;
        TransferInfoIcon.Text = icon;
        TransferInfoName.Text = state.Title;
        TransferInfoDetail.Text = state.Detail;
        TransferInfo.Visibility = Visibility.Visible;
        if (!wasVisible && _expanded) UpdateTargets();
    }

    /// <summary>Дуга кружка прогресса (18x18, начало сверху, по часовой).</summary>
    private static Geometry ArcGeometry(double fraction)
    {
        const double c = 9, r = 7.8;
        if (fraction >= 0.999) return new EllipseGeometry(new Point(c, c), r, r);
        var angle = fraction * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c, c - r), false, false);
            ctx.ArcTo(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    // ---------- Плеер и эквалайзер ----------

    private void CreateBars()
    {
        for (var i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Rectangle
            {
                Width = 3, Height = 16, Margin = new Thickness(1, 0, 1, 0), RadiusX = 1.5, RadiusY = 1.5,
                Fill = new SolidColorBrush(_accent), RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 0.2),
            };
            Bars.Children.Add(_bars[i]);
        }
    }

    private void AnimateBars()
    {
        if (_visualizer.IsRunning)
        {
            _visualizer.ReadBands(_bandTargets);
        }
        else
        {
            // Захват звука недоступен: спокойная анимация, чтобы эквалайзер не стоял.
            var t = _clock.Elapsed.TotalSeconds;
            for (var i = 0; i < _bandTargets.Length; i++)
                _bandTargets[i] = (float)(0.35 + 0.3 * Math.Sin(t * (3 + i * 0.7) + i));
        }

        for (var i = 0; i < _bars.Length; i++)
        {
            var target = _bandTargets[i];
            // Быстро вверх на удар, плавно вниз — так движение читается как ритм.
            var k = target > _bandShown[i] ? 0.65 : 0.18;
            _bandShown[i] += (target - _bandShown[i]) * k;
            ((ScaleTransform)_bars[i].RenderTransform).ScaleY = 0.15 + 0.85 * Math.Clamp(_bandShown[i], 0, 1);
        }
    }

    private void ShowMedia(MediaState? state)
    {
        var hasMedia = state != null && !string.IsNullOrEmpty(state.Title);

        TitleText.Text = hasMedia ? state!.Title : "Ничего не играет";
        ArtistText.Text = hasMedia ? state!.Artist : "Включите музыку в любом плеере";
        MorphCoverBrush.ImageSource = hasMedia ? state!.Cover : null;
        ApplyFrame();

        // Цвет эквалайзера под обложку.
        _accent = ColorExtractor.FromImage(state?.Cover) ?? Color.FromRgb(0x4A, 0xDE, 0x80);
        var brush = new SolidColorBrush(_accent);
        brush.Freeze();
        foreach (var bar in _bars) bar.Fill = brush;

        var playing = hasMedia && state!.IsPlaying;
        PlayPauseButton.Content = playing ? "" : "";
        Bars.Visibility = playing && AppSettings.Current.ShowEqualizer ? Visibility.Visible : Visibility.Collapsed;

        if (playing != _playing)
        {
            _playing = playing;
            if (playing)
            {
                _visualizer.Start();
                _barsTimer.Start();
            }
            else
            {
                _visualizer.Stop();
                _barsTimer.Stop();
            }
        }
    }

    private async void OnPlayPause(object sender, RoutedEventArgs e) => await _media.PlayPauseAsync();
    private async void OnNext(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void OnPrevious(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    // ---------- Таймлайн трека ----------

    private bool _seeking;
    private MediaTimeline? _timeline;

    private void UpdateTimeline()
    {
        if (!_expanded || _page != Page.Media) return;
        _timeline = _media.GetTimeline();
        TimelineRow.Visibility = _timeline == null ? Visibility.Hidden : Visibility.Visible;
        if (_timeline == null || _seeking) return;

        var now = _timeline.Now();
        if (_seekTarget is TimeSpan wanted)
        {
            // Пока плеер не прислал свежую позицию (а браузеры присылают её редко), считаем от точки,
            // куда перемотали. Старой позиции до перемотки не верим — из-за неё ползунок и прыгал назад.
            var expected = wanted + (_timeline.IsPlaying ? DateTime.Now - _seekAt : TimeSpan.Zero);
            // VK в браузере иногда принимает перемотку и через долю секунды откатывает её назад.
            // Пока FinishSeek проверяет и при необходимости повторяет перемотку, показываем цель.
            if ((DateTime.Now - _seekAt).TotalSeconds < 3.2 && expected < _timeline.Duration) now = expected;
            else _seekTarget = null;
        }
        TimelineSlider.Value = now.TotalSeconds / _timeline.Duration.TotalSeconds * 1000;
        PositionText.Text = FormatTime(now);
        DurationText.Text = FormatTime(_timeline.Duration);
    }

    // Перемотку делаем сами: клик сразу ставит позицию в точку клика, перетаскивание двигает её,
    // а после отпускания держим новую позицию, пока плеер не подтвердит перемотку.
    private TimeSpan? _seekTarget;
    private DateTime _seekAt;

    private void OnTimelineDown(object sender, MouseButtonEventArgs e)
    {
        if (_timeline == null || _seeking) return;
        MediaService.Log($"down x={e.GetPosition(TimelineSlider).X:0}");
        _seeking = true;
        TimelineSlider.CaptureMouse();
        MoveTimelineTo(e.GetPosition(TimelineSlider).X);
        e.Handled = true;
    }

    private void OnTimelineMove(object sender, MouseEventArgs e)
    {
        if (_seeking) MoveTimelineTo(e.GetPosition(TimelineSlider).X);
    }

    private void MoveTimelineTo(double x)
    {
        const double thumb = 7; // половина ширины кружка ползунка
        var width = Math.Max(1, TimelineSlider.ActualWidth - thumb * 2);
        var fraction = Math.Clamp((x - thumb) / width, 0, 1);
        TimelineSlider.Value = fraction * 1000;
        if (_timeline != null)
            PositionText.Text = FormatTime(TimeSpan.FromSeconds(fraction * _timeline.Duration.TotalSeconds));
    }

    private void OnTimelineUp(object sender, MouseButtonEventArgs e)
    {
        MediaService.Log($"up seeking={_seeking} x={e.GetPosition(TimelineSlider).X:0}");
        if (!_seeking && _timeline != null)
        {
            // Нажатие потерялось: считаем отпускание кликом в этой точке.
            _seeking = true;
            MoveTimelineTo(e.GetPosition(TimelineSlider).X);
        }
        e.Handled = true;
        FinishSeek();
    }

    // Если кнопку отпустили, а событие не дошло (захват мыши потерян), перематываем всё равно.
    private void OnTimelineLostCapture(object sender, MouseEventArgs e) => FinishSeek();

    private async void FinishSeek()
    {
        if (!_seeking) return;
        _seeking = false;
        if (TimelineSlider.IsMouseCaptured) TimelineSlider.ReleaseMouseCapture();
        if (_timeline == null) return;
        var target = TimeSpan.FromSeconds(TimelineSlider.Value / 1000 * _timeline.Duration.TotalSeconds);
        _seekTarget = target;
        _seekAt = DateTime.Now;
        var id = ++_seekId;
        await _media.SeekAsync(target);

        // Проверяем позицию несколько раз: если плеер откатился назад, перематываем снова.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(700);
            if (id != _seekId || _seeking) return;
            var actual = _media.GetTimeline();
            if (actual == null) return;
            var expected = target + (actual.IsPlaying ? DateTime.Now - _seekAt : TimeSpan.Zero);
            var off = (actual.Now() - expected).TotalSeconds;
            MediaService.Log($"check {attempt}: now={actual.Now():mm\\:ss} pos={actual.Position:mm\\:ss} upd={actual.UpdatedAt.LocalDateTime:HH:mm:ss.fff} expected={expected:mm\\:ss} off={off:0.0} shown={PositionText.Text}");
            if (Math.Abs(off) > 2.5 && attempt < 4) await _media.SeekAsync(expected);
        }
    }

    private int _seekId;

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    // ---------- Таймер и помодоро ----------

    private void OnPomodoro(object sender, RoutedEventArgs e) => _timer.StartPomodoro();

    /// <summary>Большая кнопка: Старт / Продолжить, а пока таймер идёт — добавить минуту.</summary>
    private void OnTimerToggle(object sender, RoutedEventArgs e)
    {
        if (_timer.IsRunning) _timer.Add(TimeSpan.FromMinutes(1));
        else _timer.Toggle();
    }

    /// <summary>Пока таймер идёт — «Стоп»: останавливает на текущем времени. Когда стоит — «Сброс».</summary>
    private void OnTimerStopReset(object sender, RoutedEventArgs e)
    {
        if (_timer.IsRunning)
        {
            _timer.Toggle();
            return;
        }
        _timer.Reset();
        // Возвращаем ту длительность, что выбрана на шкале (помодоро её не трогает).
        _timer.SetLength(TimeSpan.FromMinutes(TimerRuler.Value));
    }

    private void UpdateTimerUi()
    {
        var left = _timer.Remaining < TimeSpan.Zero ? TimeSpan.Zero : _timer.Remaining;
        var text = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"mm\:ss");
        TimerBigText.Text = text;

        var running = _timer.IsRunning;
        var active = _timer.IsActive;
        TimerStartIcon.Data = (Geometry)FindResource(running ? "PlusGeo" : "PlayGeo");
        TimerStartText.Text = running ? "+1 минута" : active ? "Продолжить" : "Старт";
        TimerStopText.Text = running ? "Стоп" : "Сброс";
        TimerStopIcon.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        TimerStopButton.Opacity = active ? 1 : 0.5;

        // Пока таймер запущен или на паузе, шкала показывает оставшееся время и едет к нулю.
        if (active) TimerRuler.Follow(left);
        else TimerRuler.StopFollowing();
        TimerRuler.IsHitTestVisible = !active;

        TimerModeText.Text = _timer.Mode switch
        {
            TimerMode.Work => $"Помодоро · работа · цикл {_timer.PomodoroCycle}",
            TimerMode.ShortBreak => "Помодоро · короткий перерыв",
            TimerMode.LongBreak => "Помодоро · длинный перерыв",
            _ => active && !running ? "Таймер · пауза" : "Таймер",
        };

        CompactTimer.Text = text;
        CompactTimer.Foreground = new SolidColorBrush(_timer.Mode is TimerMode.ShortBreak or TimerMode.LongBreak
            ? Color.FromRgb(0x30, 0xD1, 0x58) : Color.FromRgb(0xFF, 0x9F, 0x0A));
        CompactTimer.Opacity = running ? 1 : 0.5;
        CompactTimer.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        LayoutWings();
    }

    private void OnTimerFinished(TimerMode finished)
    {
        var message = finished switch
        {
            TimerMode.Work => "Перерыв! Отдохните",
            TimerMode.ShortBreak or TimerMode.LongBreak => "Пора работать",
            _ => "Время вышло",
        };
        if (AppSettings.Current.TimerSound) ChimePlayer.Play();
        ShowActivity("", message, null, "", TimeSpan.FromSeconds(5));
    }

    // ---------- Полка для файлов ----------

    private static readonly string ShelfFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotchWin", "shelf.txt");
    private readonly List<string> _shelf = new();
    private readonly HashSet<string> _pinned = new(StringComparer.OrdinalIgnoreCase);
    private Point _dragStart;

    /// <summary>Добавляет файл на полку (например, свежий скриншот).</summary>
    private void AddToShelf(string path)
    {
        // Повторно добавленный файл переезжает в начало полки.
        _shelf.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        _shelf.Add(path);
        TrimShelf();
        SaveShelf();
        RenderShelf();
    }

    /// <summary>Ограничение полки из настроек: лишние старые файлы уходят, закреплённые остаются.</summary>
    private void TrimShelf()
    {
        var limit = AppSettings.Current.ShelfLimit;
        if (limit <= 0) return;
        var removed = false;
        while (_shelf.Count(p => !_pinned.Contains(p)) > limit)
        {
            _shelf.Remove(_shelf.First(p => !_pinned.Contains(p)));
            removed = true;
        }
        if (!removed) return;
        SaveShelf();
        RenderShelf();
    }

    private void TogglePin(string path)
    {
        if (!_pinned.Remove(path)) _pinned.Add(path);
        SaveShelf();
        RenderShelf();
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        _collapseDelay.Stop();
        if (_expanded) { ShowPage(Page.Shelf); UpdateTargets(); }
        else { _openingForDrop = true; SetExpanded(true); }
        // Без LocalSend зон нет: файл просто ложится на полку.
        if (!AppSettings.Current.LocalSendEnabled) return;
        DropZones.Visibility = Visibility.Visible;
        HighlightDropZone(e);
    }

    /// <summary>Файл тащат над челкой: левая половина — отложить на полку, правая — отправить через LocalSend.</summary>
    private bool HighlightDropZone(DragEventArgs e)
    {
        // Половину считаем по положению курсора относительно самой челки: зоны могут быть ещё не разложены.
        var x = e.GetPosition(Body).X;
        var send = x > Body.ActualWidth / 2;
        _dropToSend = send;
        StyleDropZone(DropShelf, !send);
        StyleDropZone(DropSend, send);
        return send;
    }

    private bool _dropToSend;

    private static void StyleDropZone(Border zone, bool active)
    {
        zone.Background = new SolidColorBrush(active ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        zone.BorderBrush = new SolidColorBrush(active ? Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        zone.Opacity = active ? 1 : 0.7;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        _collapseDelay.Stop();
        if (e.Effects != DragDropEffects.None && AppSettings.Current.LocalSendEnabled)
        {
            DropZones.Visibility = Visibility.Visible;
            HighlightDropZone(e);
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e) => StartCollapseDelay();

    private void OnDrop(object sender, DragEventArgs e)
    {
        // Решаем по последней подсветке во время перетаскивания: её видел пользователь.
        var toSend = AppSettings.Current.LocalSendEnabled
            && (DropZones.Visibility == Visibility.Visible ? _dropToSend : HighlightDropZone(e));
        DropZones.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        if (toSend)
        {
            e.Handled = true;
            // Окно со списком устройств открываем после завершения перетаскивания.
            Dispatcher.BeginInvoke(() => OpenSendPopup(files.ToList()), DispatcherPriority.Background);
            return;
        }
        foreach (var f in files)
        {
            _shelf.RemoveAll(p => p.Equals(f, StringComparison.OrdinalIgnoreCase));
            _shelf.Add(f);
        }
        SaveShelf();
        RenderShelf();
        e.Handled = true;
    }

    // Колесо мыши и прокрутка двумя пальцами вверх-вниз листают полку вбок (плавно, см. SmoothScroll),
    // свайп влево-вправо по тачпаду приходит отдельным сообщением (WM_MOUSEHWHEEL, см. WndProc).
    private SmoothScroll? _shelfScroll;

    private void ScrollShelf(int delta) => _shelfScroll?.ScrollBy(delta);

    private void OnShelfClear(object sender, RoutedEventArgs e)
    {
        // Закреплённое остаётся на полке.
        _shelf.RemoveAll(p => !_pinned.Contains(p));
        SaveShelf();
        RenderShelf();
    }

    private void LoadShelf()
    {
        try
        {
            if (File.Exists(ShelfFile))
                foreach (var line in File.ReadAllLines(ShelfFile))
                {
                    // Закреплённые файлы записаны со звёздочкой в начале.
                    var pinned = line.StartsWith('*');
                    var path = pinned ? line[1..] : line;
                    if (!File.Exists(path) && !Directory.Exists(path)) continue;
                    _shelf.Add(path);
                    if (pinned) _pinned.Add(path);
                }
        }
        catch { }
        RenderShelf();
        TrimShelf();
    }

    private void SaveShelf()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ShelfFile)!);
            _pinned.RemoveWhere(p => !_shelf.Contains(p, StringComparer.OrdinalIgnoreCase));
            File.WriteAllLines(ShelfFile, _shelf.Select(p => _pinned.Contains(p) ? "*" + p : p));
        }
        catch { }
    }

    private void RenderShelf()
    {
        ShelfItems.Children.Clear();
        // Сначала подключённые флешки, потом закреплённые файлы, дальше — последние добавленные.
        foreach (var drive in _drives)
            ShelfItems.Children.Add(BuildDriveItem(drive));
        foreach (var path in Enumerable.Reverse(_shelf).OrderByDescending(p => _pinned.Contains(p)))
            ShelfItems.Children.Add(BuildShelfItem(path));
        var empty = _shelf.Count == 0 && _drives.Count == 0;
        ShelfDropHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ShelfContent.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (_expanded) UpdateTargets();
    }

    /// <summary>Подсказка при наведении: крупная миниатюра и полный путь.</summary>
    private static object BuildShelfTooltip(string path)
    {
        var big = new Image { Width = 220, Height = 160, Stretch = Stretch.Uniform, Margin = new Thickness(0, 2, 0, 6) };
        RenderOptions.SetBitmapScalingMode(big, BitmapScalingMode.HighQuality);
        var panel = new StackPanel { MaxWidth = 240 };
        panel.Children.Add(big);
        panel.Children.Add(new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.8 });
        var tip = new ToolTip { Content = panel };
        tip.Opened += (_, _) =>
        {
            if (big.Source != null) return;
            big.Source = ShellImage.GetThumbnail(path, 256, img => big.Source = img) ?? ShellIcons.Load(path);
        };
        return tip;
    }

    private UIElement BuildShelfItem(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        // Сразу показываем значок, а миниатюру (картинка, кадр видео, страница PDF) подставляем, когда загрузится.
        var preview = new Image
        {
            Source = ShellIcons.Load(path), Width = 40, Height = 40, Margin = new Thickness(0, 0, 0, 3), Stretch = Stretch.Uniform,
        };
        RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.HighQuality);
        var frame = new Border { CornerRadius = new CornerRadius(5), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, Child = preview };
        panel.Children.Add(frame);
        if (ShellImage.GetThumbnail(path, 96, img => { if (img != null) preview.Source = img; }) is { } thumb) preview.Source = thumb;
        panel.Children.Add(new TextBlock
        {
            Text = name, Foreground = Brushes.White, FontSize = 11, Width = 64, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var pinned = _pinned.Contains(path);
        var content = new Grid();
        content.Children.Add(panel);
        if (pinned)
            content.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Icon"), Text = "\uE840", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 1, 0),
            });

        var item = new Border
        {
            Width = 76, Height = 74, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(pinned ? Color.FromArgb(0x26, 0xFF, 0x9F, 0x0A) : Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            Child = content, Padding = new Thickness(4), ToolTip = BuildShelfTooltip(path), Cursor = Cursors.Hand,
        };

        item.MouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(this);
            if (e.ClickCount == 2) OpenPath(path);
        };
        item.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _dragStart.X) < 5 && Math.Abs(pos.Y - _dragStart.Y) < 5) return;
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            DragDrop.DoDragDrop(item, new DataObject(DataFormats.FileDrop, new[] { path }),
                DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            if (!IsMouseOver) StartCollapseDelay();
        };

        var menu = new ContextMenu();
        menu.Items.Add(MakeMenuItem("Открыть", () => OpenPath(path)));
        menu.Items.Add(MakeMenuItem(pinned ? "Открепить" : "Закрепить", () => TogglePin(path)));
        if (AppSettings.Current.LocalSendEnabled)
            menu.Items.Add(MakeMenuItem("Отправить через LocalSend", () => OpenSendPopup(new List<string> { path })));
        menu.Items.Add(MakeMenuItem("Показать в папке", () =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })));
        menu.Items.Add(MakeMenuItem("Убрать с полки", () => { _shelf.Remove(path); _pinned.Remove(path); SaveShelf(); RenderShelf(); }));
        item.ContextMenu = menu;
        return item;
    }

    private static MenuItem MakeMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => { try { action(); } catch { } };
        return item;
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    // ---------- Сеть: Wi-Fi и Bluetooth ----------

    private bool? _wifiOn, _bluetoothOn;
    private WifiNetwork? _pendingNetwork;
    private bool _wifiConnecting;

    private static readonly Brush RowBrush = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RowHoverBrush = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ConnectedBrush = Frozen(Color.FromArgb(0x48, 0x0A, 0x84, 0xFF));
    private static readonly Brush DimBrush = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly Brush BlueBrush = Frozen(Color.FromRgb(0x40, 0x9C, 0xFF));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private async Task RefreshRadiosAsync()
    {
        (_wifiOn, _bluetoothOn) = await _radio.GetRadioStatesAsync();
        // Подсказки не показываем: наведение и так открывает окно.
        WifiToggle.Opacity = _wifiOn == true ? 1 : 0.4;
        WifiToggle.Visibility = _wifiOn == null ? Visibility.Collapsed : Visibility.Visible;
        BluetoothToggle.Opacity = _bluetoothOn == true ? 1 : 0.4;
        BluetoothToggle.Visibility = _bluetoothOn == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnWifiToggle(object sender, RoutedEventArgs e)
    {
        HideWifiOverlay();
        await _radio.SetRadioAsync(RadioKind.WiFi, _wifiOn != true);
        await RefreshRadiosAsync();
        if (_wifiCloser.IsOpen) _ = ScanWifiAsync();
    }

    private async void OnBluetoothToggle(object sender, RoutedEventArgs e)
    {
        await _radio.SetRadioAsync(RadioKind.Bluetooth, _bluetoothOn != true);
        await RefreshRadiosAsync();
        if (_btCloser.IsOpen) _ = LoadBluetoothAsync();
    }

    private async Task ScanWifiAsync()
    {
        if (_wifiOn == false)
        {
            WifiList.Children.Clear();
            ShowWifiMessage("Wi-Fi выключен. Нажмите на значок слева вверху, чтобы включить.", null);
            return;
        }

        var networks = await _radio.ScanAsync();
        if (networks == null)
        {
            WifiList.Children.Clear();
            ShowWifiMessage("Windows не дала доступ к списку сетей. Нажмите здесь, чтобы разрешить приложениям " +
                            "доступ к местоположению (Windows требует его для поиска Wi-Fi).",
                "ms-settings:privacy-location");
            return;
        }

        RenderWifi(networks);
        // Сообщение «Подключаюсь…» или ввод пароля оставляем, остальное убираем.
        if (_pendingNetwork == null && !_wifiConnecting) HideWifiOverlay();
    }

    private List<WifiNetwork> _lastNetworks = new();

    private void RenderWifi(List<WifiNetwork> networks)
    {
        _lastNetworks = networks;
        WifiList.Children.Clear();
        foreach (var n in networks) WifiList.Children.Add(BuildWifiRow(n));
    }

    /// <summary>Сразу после подключения подсвечиваем сеть синим, не дожидаясь нового поиска сетей.</summary>
    private void MarkConnected(string ssid) =>
        RenderWifi(_lastNetworks
            .Select(n => n with { Connected = n.Ssid == ssid })
            .OrderByDescending(n => n.Connected).ThenByDescending(n => n.Bars).ToList());

    private void ShowWifiMessage(string text, string? uri)
    {
        WifiMessage.Text = text;
        WifiOverlay.Visibility = Visibility.Visible;
        WifiMessage.Cursor = uri == null ? null : Cursors.Hand;
        WifiMessage.MouseLeftButtonUp -= OnWifiMessageClick;
        _wifiMessageUri = uri;
        if (uri != null) WifiMessage.MouseLeftButtonUp += OnWifiMessageClick;
    }

    private void HideWifiOverlay()
    {
        WifiOverlay.Visibility = Visibility.Collapsed;
        PasswordRow.Visibility = Visibility.Collapsed;
        WifiPassword.Clear();
        _wifiMessageUri = null;
        if (_pendingNetwork != null)
        {
            _pendingNetwork = null;
            SetActivatable(false);
        }
    }

    private string? _wifiMessageUri;
    private void OnWifiMessageClick(object sender, MouseButtonEventArgs e)
    {
        if (_wifiMessageUri != null) OpenPath(_wifiMessageUri);
    }

    /// <summary>Строка списка, которая подсвечивается при наведении и реагирует на клик.</summary>
    private Border BuildRow(UIElement content, bool highlighted, Action? click)
    {
        var row = new Border
        {
            Child = content, Height = 36, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 0, 4),
            CornerRadius = new CornerRadius(10), Background = highlighted ? ConnectedBrush : RowBrush,
            Cursor = click != null ? Cursors.Hand : null,
        };
        if (!highlighted)
        {
            row.MouseEnter += (_, _) => row.Background = RowHoverBrush;
            row.MouseLeave += (_, _) => row.Background = RowBrush;
        }
        if (click != null) row.MouseLeftButtonUp += (_, _) => click();
        return row;
    }

    private UIElement BuildWifiRow(WifiNetwork n)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Сила сигнала: чем меньше делений, тем бледнее значок.
        var bars = Math.Clamp(n.Bars, 1, 4);
        grid.Children.Add(new TextBlock { Style = (Style)FindResource("Icon"), Text = "", FontSize = 14, Opacity = 0.3 + 0.175 * bars });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = n.Ssid, Foreground = Brushes.White, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = n.Connected ? FontWeights.SemiBold : FontWeights.Normal,
        });
        if (n.Connected)
            text.Children.Add(new TextBlock { Text = "Подключено", Foreground = BlueBrush, FontSize = 11 });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var right = new TextBlock
        {
            Style = (Style)FindResource("Icon"), FontSize = 12, Margin = new Thickness(8, 0, 0, 0),
            Foreground = n.Connected ? Brushes.White : DimBrush,
            Text = n.Connected ? "" : n.Secured ? "" : "",
        };
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var row = BuildRow(grid, n.Connected, () => _ = ConnectAsync(n));
        if (n.Connected) row.Height = 42;
        return row;
    }

    private async Task ConnectAsync(WifiNetwork n)
    {
        // Уже подключены: просто убираем ввод пароля, если он был открыт для другой сети.
        if (n.Connected) { HideWifiOverlay(); return; }
        if (_pendingNetwork != null && _pendingNetwork.Ssid != n.Ssid) HideWifiOverlay();

        ShowWifiMessage($"Подключаюсь к «{n.Ssid}»…", null);
        _wifiConnecting = true;
        var connected = await _radio.ConnectAsync(n, null);
        _wifiConnecting = false;
        if (connected)
        {
            HideWifiOverlay();
            MarkConnected(n.Ssid);
            return;
        }
        if (!n.Secured)
        {
            ShowWifiMessage($"Не удалось подключиться к «{n.Ssid}».", null);
            return;
        }

        // Сеть с паролем, который Windows ещё не знает: просим ввести.
        _pendingNetwork = n;
        ShowWifiMessage($"Пароль для «{n.Ssid}»", null);
        PasswordRow.Visibility = Visibility.Visible;
        WifiPassword.Clear();
        SetActivatable(true);
        WifiPassword.Focus();
    }

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnWifiConnectWithPassword(sender, e);
        if (e.Key == Key.Escape) HideWifiOverlay();
    }

    private async void OnWifiConnectWithPassword(object sender, RoutedEventArgs e)
    {
        if (_pendingNetwork == null) return;
        var network = _pendingNetwork;
        WifiMessage.Text = $"Подключаюсь к «{network.Ssid}»…";
        var ok = await _radio.ConnectAsync(network, WifiPassword.Password);
        if (!ok)
        {
            WifiMessage.Text = "Неверный пароль или сеть недоступна. Попробуйте ещё раз.";
            return;
        }
        HideWifiOverlay();
        MarkConnected(network.Ssid);
    }

    // ---------- Отдельные окна Wi-Fi и Bluetooth ----------

    private readonly PopupCloser _wifiCloser;
    private readonly PopupCloser _btCloser;
    private readonly DispatcherTimer _sideCloseDelay = new() { Interval = TimeSpan.FromMilliseconds(280) };

    // Список открывается, только если задержать курсор на значке: случайное касание
    // при раскрытии челки ничего не открывает.
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private FrameworkElement? _hoverTarget;
    private DateTime _hoverSince, _expandedAt;
    private const double HoverDelay = 0.4, SettleDelay = 0.5;

    private void StartHover(FrameworkElement target)
    {
        _sideCloseDelay.Stop();
        _hoverTarget = target;
        _hoverSince = DateTime.Now;
        _hoverTimer.Start();
    }

    private void CheckHoverOpen()
    {
        if (_hoverTarget == null || !_hoverTarget.IsMouseOver || !_expanded) { _hoverTimer.Stop(); return; }
        var now = DateTime.Now;
        if ((now - _hoverSince).TotalSeconds < HoverDelay || (now - _expandedAt).TotalSeconds < SettleDelay + HoverDelay) return;
        _hoverTimer.Stop();
        if (_hoverTarget == WifiToggle) OpenWifiPopup();
        else OpenBluetoothPopup();
    }

    private void OnWifiHover(object sender, MouseEventArgs e)
    {
        if (_wifiCloser.IsOpen) { _sideCloseDelay.Stop(); return; }
        // Соседнее окно уже открыто — переключаемся сразу, как в меню на Mac.
        if (_btCloser.IsOpen) { _sideCloseDelay.Stop(); OpenWifiPopup(); return; }
        StartHover(WifiToggle);
    }

    private void OnBluetoothHover(object sender, MouseEventArgs e)
    {
        if (_btCloser.IsOpen) { _sideCloseDelay.Stop(); return; }
        if (_wifiCloser.IsOpen) { _sideCloseDelay.Stop(); OpenBluetoothPopup(); return; }
        StartHover(BluetoothToggle);
    }

    private void OpenWifiPopup()
    {
        if (!_expanded || _wifiCloser.IsOpen) return;
        _btCloser.Close();
        // Окно открывается сразу со старым списком и обновляет его на месте, поэтому ничего не прыгает.
        _wifiCloser.Open();
        _ = ScanWifiAsync();
    }

    private void OpenBluetoothPopup()
    {
        if (!_expanded || _btCloser.IsOpen) return;
        _wifiCloser.Close();
        _btCloser.Open();
        _ = LoadBluetoothAsync();
    }

    private void OnSideHoverLeave(object sender, MouseEventArgs e)
    {
        _sideCloseDelay.Start();
        if (!IsMouseOver) StartCollapseDelay();
    }

    private bool IsOverSidePopup() =>
        (_wifiCloser.IsOpen && WifiCard.IsMouseOver) || (_btCloser.IsOpen && BluetoothCard.IsMouseOver)
        || (_sendCloser.IsOpen && SendCard.IsMouseOver)
        || (PasswordRow.Visibility == Visibility.Visible && WifiPassword.IsKeyboardFocusWithin);

    private void CloseSidePopupsIfLeft()
    {
        var typing = PasswordRow.Visibility == Visibility.Visible && WifiPassword.IsKeyboardFocusWithin;
        if (_wifiCloser.IsOpen && !WifiCard.IsMouseOver && !WifiToggle.IsMouseOver && !typing) _wifiCloser.Close();
        if (_btCloser.IsOpen && !BluetoothCard.IsMouseOver && !BluetoothToggle.IsMouseOver) _btCloser.Close();
    }

    private string? _btSignature;

    private async Task LoadBluetoothAsync()
    {
        if (_bluetoothOn == false)
        {
            BluetoothList.Children.Clear();
            _btSignature = null;
            ShowBluetoothStatus("Bluetooth выключен");
            return;
        }
        // Список ищем, пока окно выезжает, а перестраиваем только если что-то поменялось:
        // перестройка во время анимации и давала подёргивания.
        var started = DateTime.Now;
        var devices = await RadioService.GetBluetoothDevicesAsync();
        var signature = string.Join("|", devices.Select(d => d.Name + d.Connected));
        if (signature == _btSignature && BluetoothList.Children.Count > 0) { ShowBluetoothStatus(null); return; }
        var wait = 200 - (DateTime.Now - started).TotalMilliseconds;
        if (wait > 0) await Task.Delay((int)wait);
        _btSignature = signature;
        BluetoothList.Children.Clear();
        foreach (var d in devices)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Style = (Style)FindResource("Icon"), Text = "", FontSize = 13,
                Opacity = d.Connected ? 1 : 0.45 });
            var name = new TextBlock { Text = d.Name, Foreground = Brushes.White, FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = d.Connected ? FontWeights.SemiBold : FontWeights.Normal };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            if (d.Connected)
            {
                var check = new TextBlock { Style = (Style)FindResource("Icon"), Text = "", FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
                Grid.SetColumn(check, 2);
                row.Children.Add(check);
            }
            BluetoothList.Children.Add(BuildRow(row, d.Connected, null));
        }
        ShowBluetoothStatus(devices.Count == 0 ? "Нет сопряжённых устройств" : null);
    }

    private void ShowBluetoothStatus(string? text)
    {
        BluetoothStatus.Text = text ?? "";
        BluetoothStatus.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnBluetoothSettings(object sender, RoutedEventArgs e) => OpenPath("ms-settings:bluetooth");

    private async void OnWifiRescan(object sender, RoutedEventArgs e)
    {
        HideWifiOverlay();
        await ScanWifiAsync();
    }

    // ---------- LocalSend ----------

    private readonly PopupCloser _sendCloser;
    private List<LocalSendDevice> _sendDevices = new();
    private List<string> _sendPaths = new();
    private CancellationTokenSource? _scanCts;
    private TransferState? _sendState;

    private void OnShelfSend(object sender, RoutedEventArgs e)
    {
        if (_sendCloser.IsOpen) { _sendCloser.Close(); return; }
        OpenSendPopup(_shelf.ToList());
    }

    private void OpenSendPopup(List<string> paths)
    {
        _sendPaths = paths;
        if (!_expanded) return;
        if (_page != Page.Shelf) { ShowPage(Page.Shelf); UpdateTargets(); }
        _sendCloser.Open();
        _ = RefreshSendDevicesAsync();
    }

    private async Task RefreshSendDevicesAsync()
    {
        // Сначала показываем найденные в прошлый раз, список обновляется на месте.
        SendList.Visibility = Visibility.Visible;
        RenderSendDevices();
        SendStatus.Text = _sendDevices.Count == 0 ? "Ищу устройства с LocalSend…" : "";
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var found = await LocalSendService.DiscoverAsync(cts.Token);
        if (cts.IsCancellationRequested) return;
        _sendDevices = found;
        RenderSendDevices();
        SendStatus.Text = found.Count == 0
            ? "Никого не нашёл. Откройте LocalSend на телефоне или другом компьютере в той же Wi-Fi сети."
            : "";
    }

    private void RenderSendDevices()
    {
        SendList.Children.Clear();
        foreach (var device in _sendDevices)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Icon"), FontSize = 15,
                Text = device.DeviceType switch { "mobile" => "", "web" => "", _ => "" },
            });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = device.Alias, Foreground = Brushes.White, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            if (device.DeviceModel.Length > 0)
                text.Children.Add(new TextBlock { Text = device.DeviceModel, Foreground = DimBrush, FontSize = 11 });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            var row = BuildRow(grid, false, () => _ = SendToAsync(device));
            row.Height = 42;
            SendList.Children.Add(row);
        }
    }

    private async Task SendToAsync(LocalSendDevice device)
    {
        var paths = _sendPaths.Count > 0 ? _sendPaths : _shelf.ToList();
        if (paths.Count == 0) return;
        // Окно не закрываем: в нём видно, что происходит (ждём подтверждения, проценты, ошибка).
        SendList.Visibility = Visibility.Collapsed;
        SendStatus.Text = $"Ждём, пока «{device.Alias}» примет файлы…";
        _sendState = new TransferState("send", device.Alias, null, "Ждём подтверждения", false);
        ShowTransfer(_sendState);
        bool ok;
        string message;
        try
        {
            (ok, message) = await LocalSendService.SendAsync(device, paths, (fraction, label) =>
                Dispatcher.BeginInvoke(() =>
                {
                    if (_sendState == null) return;
                    var text = fraction > 0 ? $"{Math.Round(fraction * 100)}% · {label}" : label;
                    _sendState = new TransferState("send", device.Alias, fraction, text, false);
                    SendStatus.Text = text;
                }), CancellationToken.None);
        }
        catch (Exception ex)
        {
            ok = false;
            message = ex is System.Net.Http.HttpRequestException
                ? $"«{device.Alias}» не отвечает. Откройте LocalSend на нём и попробуйте ещё раз."
                : ex.Message;
        }
        _sendState = null;
        ShowTransfer(null);
        SendStatus.Text = (ok ? "✓ " : "") + message;
        ShowActivity(ok ? "\uE73E" : "\uE783", ok ? "Отправлено" : "Не отправлено", null, message, TimeSpan.FromSeconds(4));
        if (ok)
        {
            await Task.Delay(1500);
            _sendCloser.Close();
        }
    }

    // ---------- Obsidian ----------

    private string? _lastNote;

    private void UpdateNoteStatus() =>
        NoteStatus.Text = NotesService.Describe() ?? "Obsidian не найден: откройте хранилище в Obsidian или выберите папку в настройках";

    private bool _noteMode;
    private IntPtr _previousForeground;

    /// <summary>
    /// Пока открыта вкладка заметки, челка разрешает себя активировать. Тогда клик по полю ввода
    /// активирует окно обычным путём (программно Windows фокус у другого окна забрать не даёт).
    /// После ухода с вкладки фокус возвращается окну, где он был.
    /// </summary>
    private void SetNoteMode(bool on)
    {
        if (_noteMode == on || _hwnd == IntPtr.Zero) return;
        _noteMode = on;
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, on ? ex & ~WS_EX_NOACTIVATE : ex | WS_EX_NOACTIVATE);
        if (on)
        {
            _previousForeground = GetForegroundWindow();
            return;
        }
        if (IsActive && _previousForeground != IntPtr.Zero && _previousForeground != _hwnd)
            SetForegroundWindow(_previousForeground);
        _previousForeground = IntPtr.Zero;
    }

    private void OnNoteFocus(object sender, MouseButtonEventArgs e)
    {
        if (!IsActive) ForceForeground();
        Dispatcher.BeginInvoke(() => { NoteBox.Focus(); Keyboard.Focus(NoteBox); }, DispatcherPriority.Input);
    }

    private void OnNoteChanged(object sender, TextChangedEventArgs e) =>
        NotePlaceholder.Visibility = NoteBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnNoteKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SaveNote();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Keyboard.ClearFocus();
            SetExpanded(false);
            e.Handled = true;
        }
    }

    private void OnNoteSave(object sender, RoutedEventArgs e) => SaveNote();

    private void SaveNote()
    {
        var text = NoteBox.Text.Trim();
        if (text.Length == 0) return;
        try
        {
            var file = NotesService.Save(text);
            if (file == null) { UpdateNoteStatus(); return; }
            _lastNote = file;
            NoteBox.Clear();
            NoteStatus.Text = $"Сохранено: {System.IO.Path.GetFileName(file)}";
            ShowActivity("\uE70B", "Заметка сохранена", null, System.IO.Path.GetFileNameWithoutExtension(file),
                TimeSpan.FromSeconds(2), fill: Color.FromRgb(0x9B, 0x86, 0xFF));
        }
        catch (Exception ex) { NoteStatus.Text = "Не удалось сохранить: " + ex.Message; }
    }

    private void OnNoteOpen(object sender, RoutedEventArgs e)
    {
        // Открываем последнюю сохранённую заметку, а если её не было — хранилище или папку.
        NotesService.Open(_lastNote);
        SetExpanded(false);
    }

    private void OnNotchDeactivated()
    {
        // Кликнули в другое окно, пока писали заметку: отдаём фокус и сворачиваемся (черновик остаётся).
        if (!NoteBox.IsKeyboardFocusWithin && PasswordRow.Visibility != Visibility.Visible) return;
        if (PasswordRow.Visibility == Visibility.Visible) return;
        Keyboard.ClearFocus();
        // Ушли в другое окно — фокус возвращать уже не нужно.
        _previousForeground = IntPtr.Zero;
        if (!IsMouseOver && !AnyMenuOpen) SetExpanded(false);
    }

    // ---------- Скриншоты Win+Shift+S → полка ----------

    private static readonly string[] ScreenshotTools = { "ScreenClippingHost", "SnippingTool", "ScreenSketch" };
    private DateTime _lastScreenshot;

    private async void OnClipboardChanged()
    {
        try
        {
            if (!AppSettings.Current.ShelfScreenshots) return;
            if ((DateTime.Now - _lastScreenshot).TotalSeconds < 2 || DateTime.Now < _ignoreClipboardUntil) return;
            var owner = GetClipboardOwner();
            GetWindowThreadProcessId(owner, out var pid);
            string name;
            try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; }
            catch { return; }
            if (!ScreenshotTools.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase))) return;

            // Снимок забираем сразу, пока буфер не сменился. Ножницы кладут PNG, иначе берём обычную картинку.
            byte[]? png = null;
            if (Clipboard.GetData("PNG") is MemoryStream stream) png = stream.ToArray();
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                png = ms.ToArray();
            }
            if (png == null) return;
            _lastScreenshot = DateTime.Now;

            // Windows 11 сама сохраняет снимок в «Изображения\Снимки экрана» — берём этот файл, чтобы не дублировать.
            await Task.Delay(1200);
            var path = FindAutoSavedScreenshot() ?? SaveScreenshot(png);
            AddToShelf(path);
            ShowActivity("", "Скриншот на полке", null, "Тяните его в любое окно", TimeSpan.FromSeconds(2.5));
        }
        catch { /* буфер занят другой программой */ }
    }

    private static string? FindAutoSavedScreenshot()
    {
        try
        {
            var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            if (!Directory.Exists(folder)) return null;
            return new DirectoryInfo(folder).GetFiles("*.png")
                .Where(f => (DateTime.Now - f.CreationTime).TotalSeconds < 6)
                .OrderByDescending(f => f.CreationTime).FirstOrDefault()?.FullName;
        }
        catch { return null; }
    }

    private static string SaveScreenshot(byte[] png)
    {
        var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, $"Скриншот {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
        File.WriteAllBytes(path, png);
        return path;
    }

    // ---------- Уведомления Telegram и ВКонтакте ----------

    private readonly NotificationWatcher _notifications = new();
    private bool _notificationsBusy;

    private async Task PollNotificationsAsync()
    {
        if (_notificationsBusy || !_notifications.Available) return;
        _notificationsBusy = true;
        try
        {
            var list = await Task.Run(_notifications.Poll);
            if (list.Count == 0) return;
            // Из Discord показываем только входящие звонки (обычные сообщения не просили).
            var settings = AppSettings.Current;
            if (settings.NotifyDiscordCalls)
                foreach (var call in list.Where(x => x.App == "Discord" && IsCallNotification(x)))
                    ShowDiscordRinging(call);
            list = list.Where(x => x.App == "Telegram" ? settings.NotifyTelegram
                : x.App == "ВКонтакте" && settings.NotifyVk).ToList();
            if (list.Count == 0) return;
            var n = list[^1]; // если пришло несколько сразу, показываем последнее
            ImageSource? avatar = null;
            if (n.ImagePath != null)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(n.ImagePath);
                    bmp.DecodePixelWidth = 44;
                    bmp.EndInit();
                    bmp.Freeze();
                    avatar = bmp;
                }
                catch { }
            }
            var color = n.App == "Telegram" ? Color.FromRgb(0x2A, 0xAB, 0xEE) : Color.FromRgb(0x44, 0x7B, 0xBA);
            ShowActivity("", n.Title, null, n.Text, TimeSpan.FromSeconds(5), avatar, color);
        }
        catch { }
        finally { _notificationsBusy = false; }
    }

    // ---------- Батарея: зарядка и низкий заряд ----------

    private int _lastAc = -1;
    private int _warnedAt = 101;

    private void CheckBattery()
    {
        if (!StatusNative.GetSystemPowerStatus(out var s) || (s.BatteryFlag & 128) != 0 || s.BatteryLifePercent > 100) return;
        int pct = s.BatteryLifePercent, ac = s.ACLineStatus;
        var green = Color.FromRgb(0x30, 0xD1, 0x58);

        var settings = AppSettings.Current;
        var low = Math.Clamp(settings.LowBatteryPercent, 0, 60);
        var critical = low / 2;
        if (_lastAc == 0 && ac == 1 && !settings.ChargingAnimation) _warnedAt = 101;
        else if (_lastAc == 0 && ac == 1)
        {
            // Подключили зарядку: полоска наливается зелёным до текущего заряда.
            ShowActivity("", "Зарядка", pct / 100.0, $"{pct}%", TimeSpan.FromSeconds(3), fill: green, animateFill: true);
            _warnedAt = 101;
        }
        else if (_lastAc == 1 && ac == 0 && settings.ChargingAnimation)
        {
            ShowActivity("", "От батареи", pct / 100.0, $"{pct}%", TimeSpan.FromSeconds(2));
        }
        else if (low > 0 && ac == 0 && (pct <= critical && _warnedAt > critical || pct <= low && _warnedAt > low))
        {
            _warnedAt = pct <= critical ? critical : low;
            ShowActivity("", pct <= critical ? "Почти разряжено" : "Низкий заряд", pct / 100.0, $"{pct}%",
                TimeSpan.FromSeconds(5), fill: Color.FromRgb(0xFF, 0x45, 0x3A));
        }
        _lastAc = ac;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    // ---------- Discord: звонок в крыле, микрофон и звук, входящий звонок ----------

    private DateTime? _discordSince;
    private bool _discordMuted, _discordDeafened;
    private string? _ringingFrom;
    private DateTime _ringingUntil;

    private static bool IsDiscord(string exe) =>
        exe.Contains("discord", StringComparison.OrdinalIgnoreCase) || exe.Contains("vesktop", StringComparison.OrdinalIgnoreCase);

    private static bool IsCallNotification(AppNotification n)
    {
        var text = (n.Title + " " + n.Text).ToLowerInvariant();
        return new[] { "звон", "вызов", "call", "ringing" }.Any(text.Contains);
    }

    /// <summary>Discord занял микрофон — значит, ты в голосовом канале или звонке.</summary>
    private void UpdateDiscordCall(bool active)
    {
        if (active == (_discordSince != null)) return;
        _discordSince = active ? DateTime.Now : null;
        if (active) _ringingFrom = null; // ответили на звонок
        else ReleaseDiscordAudio();
        UpdateDiscordUi();
    }

    private void ShowDiscordRinging(AppNotification n)
    {
        _ringingFrom = n.Title;
        _ringingUntil = DateTime.Now.AddSeconds(30);
        ShowActivity("", "Discord · звонок", null, n.Title, TimeSpan.FromSeconds(8), null, Color.FromRgb(0x58, 0x65, 0xF2));
        UpdateDiscordUi();
    }

    private void UpdateDiscordUi()
    {
        if (_ringingFrom != null && DateTime.Now > _ringingUntil) _ringingFrom = null;
        var inCall = _discordSince is DateTime since;
        var wasVisible = DiscordInfo.Visibility == Visibility.Visible;

        // Крыло: время в звонке. Если микрофон выключен — значок перечёркнут и красный.
        var wingVisible = inCall && AppSettings.Current.DiscordWing ? Visibility.Visible : Visibility.Collapsed;
        if (inCall)
        {
            var t = DateTime.Now - _discordSince!.Value;
            DiscordWingTime.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
            DiscordWingIcon.Text = _discordMuted ? "" : "";
            DiscordWingIcon.Foreground = _discordMuted ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) : new SolidColorBrush(Color.FromRgb(0x58, 0x65, 0xF2));
        }
        if (DiscordWing.Visibility != wingVisible)
        {
            DiscordWing.Visibility = wingVisible;
            LayoutWings();
        }

        // Строка в раскрытой челке.
        if (_ringingFrom != null && !inCall)
        {
            DiscordInfo.Visibility = Visibility.Visible;
            DiscordInfoText.Text = $"Звонит {_ringingFrom}";
            DiscordCallButtons.Visibility = Visibility.Collapsed;
            DiscordOpenButton.Visibility = Visibility.Visible;
        }
        else if (inCall)
        {
            DiscordInfo.Visibility = Visibility.Visible;
            DiscordInfoText.Text = $"Discord · {DiscordWingTime.Text}";
            DiscordCallButtons.Visibility = Visibility.Visible;
            DiscordOpenButton.Visibility = Visibility.Collapsed;
            DiscordMuteButton.Content = _discordMuted ? "" : "";
            DiscordMuteButton.Foreground = _discordMuted ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) : Brushes.White;
            DiscordDeafenButton.Foreground = _discordDeafened ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) : Brushes.White;
            DiscordDeafenButton.Opacity = _discordDeafened ? 1 : 0.85;
        }
        else DiscordInfo.Visibility = Visibility.Collapsed;

        if (wasVisible != (DiscordInfo.Visibility == Visibility.Visible) && _expanded) UpdateTargets();
    }

    // Нажимаем горячие клавиши, заданные в самом Discord, — микрофон выключается именно в Discord.
    private void OnDiscordMute(object sender, RoutedEventArgs e)
    {
        KeySender.Send(AppSettings.Current.DiscordMuteKey);
        _discordMuted = !_discordMuted;
        UpdateDiscordUi();
    }

    private void OnDiscordDeafen(object sender, RoutedEventArgs e)
    {
        KeySender.Send(AppSettings.Current.DiscordDeafenKey);
        // Как в самом Discord: без звука — значит и без микрофона, обратно — оба включаются.
        _discordDeafened = !_discordDeafened;
        _discordMuted = _discordDeafened;
        UpdateDiscordUi();
    }

    private void ReleaseDiscordAudio() => _discordMuted = _discordDeafened = false;

    private void OnDiscordOpen(object sender, RoutedEventArgs e)
    {
        OpenPath("discord://");
        _ringingFrom = null;
        UpdateDiscordUi();
        SetExpanded(false);
    }

    // ---------- Флешки и диски ----------

    private readonly List<DriveState> _drives = new();

    private void OnDeviceChange(IntPtr wParam, IntPtr lParam)
    {
        var ev = wParam.ToInt32();
        if ((ev != 0x8000 /* DBT_DEVICEARRIVAL */ && ev != 0x8004 /* DBT_DEVICEREMOVECOMPLETE */) || lParam == IntPtr.Zero) return;
        if (System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 4) != 2 /* DBT_DEVTYP_VOLUME */) return;
        var mask = System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 12);
        var roots = Enumerable.Range(0, 26).Where(i => (mask & (1 << i)) != 0).Select(i => $"{(char)('A' + i)}:\\").ToList();
        if (ev == 0x8000) Dispatcher.BeginInvoke(() => OnDrivesArrived(roots));
        else Dispatcher.BeginInvoke(() =>
        {
            _drives.RemoveAll(d => roots.Contains(d.Root, StringComparer.OrdinalIgnoreCase));
            RenderShelf();
        });
    }

    private async void OnDrivesArrived(List<string> roots)
    {
        if (!AppSettings.Current.ShowDrives) return;
        // Диску нужно мгновение, чтобы стать готовым.
        foreach (var root in roots)
        {
            DriveState? drive = null;
            for (var i = 0; i < 10 && drive == null; i++)
            {
                drive = await Task.Run(() => DriveService.Read(root));
                if (drive == null) await Task.Delay(300);
            }
            if (drive == null) continue;
            _drives.RemoveAll(d => d.Root.Equals(drive.Root, StringComparison.OrdinalIgnoreCase));
            _drives.Add(drive);
            RenderShelf();
            ShowActivity("", drive.Label, drive.Total > 0 ? 1 - drive.Free / (double)drive.Total : null,
                $"{DriveService.Size(drive.Free)} свободно", TimeSpan.FromSeconds(4));
        }
    }

    /// <summary>Диск на полке: значок, название, свободное место и кнопка «Извлечь».</summary>
    private UIElement BuildDriveItem(DriveState drive)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var icon = ShellIcons.Load(drive.Root);
        if (icon != null) panel.Children.Add(new Image { Source = icon, Width = 32, Height = 32, Margin = new Thickness(0, 2, 0, 4) });
        panel.Children.Add(new TextBlock
        {
            Text = drive.Label, Foreground = Brushes.White, FontSize = 11, Width = 64, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{DriveService.Size(drive.Free)} своб.", Foreground = DimBrush, FontSize = 10, Width = 64,
            TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var eject = new Button
        {
            Style = (Style)FindResource("IconButton"), Content = "⏏", FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 11, Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -2, 0),
        };
        eject.Click += async (_, e) =>
        {
            e.Handled = true;
            var ok = await Task.Run(() => DriveService.Eject(drive.Root));
            if (ok)
            {
                _drives.Remove(drive);
                RenderShelf();
                ShowActivity("", "Можно извлечь", null, drive.Label, TimeSpan.FromSeconds(3));
            }
            else ShowActivity("", "Диск занят", null, "Закройте файлы с него", TimeSpan.FromSeconds(3));
        };

        var content = new Grid();
        content.Children.Add(panel);
        content.Children.Add(eject);
        var item = new Border
        {
            Width = 76, Height = 74, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0x0A, 0x84, 0xFF)),
            Child = content, Padding = new Thickness(4), Cursor = Cursors.Hand,
        };
        item.MouseLeftButtonUp += (_, e) => { if (!e.Handled) OpenPath(drive.Root); };
        return item;
    }

    // ---------- Перевод выделенного текста ----------

    private const int TranslateHotkeyId = 0x7A01;

    /// <summary>true — сочетание для перевода занято другой программой (показываем в настройках).</summary>
    public static bool TranslateHotkeyBusy { get; private set; }

    private void RegisterTranslateHotkey()
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterHotKey(_hwnd, TranslateHotkeyId);
        var combo = AppSettings.Current.TranslateHotkey;
        var ok = !AppSettings.RecordingHotkey && Hotkeys.TryParse(combo, out var mods, out var vk)
                 && RegisterHotKey(_hwnd, TranslateHotkeyId, mods | Hotkeys.MOD_NOREPEAT, vk);
        TranslateHotkeyBusy = !ok && combo.Length > 0 && !AppSettings.RecordingHotkey;
    }
    private PopupCloser? _translateCloser;
    private CancellationTokenSource? _translateCts;

    private async void TranslateSelection()
    {
        var text = await CopySelectionAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowActivity("", "Перевод", null, "Сначала выделите текст", TimeSpan.FromSeconds(2));
            return;
        }
        text = text.Trim();

        _translateCloser ??= new PopupCloser(TranslatePopup, TranslateCard, Body);
        TranslateSource.Text = text.Replace('\n', ' ').Replace("\r", "");
        TranslateResult.Text = "Перевожу…";
        TranslateResult.Opacity = 0.5;
        TranslateLangs.Text = "";
        // Окно по центру под челкой. Размер постоянный, поэтому ничего не прыгает, когда приходит перевод.
        TranslatePopup.HorizontalOffset = (Body.ActualWidth - TranslateCard.Width) / 2;
        if (!_translateCloser.IsOpen) _translateCloser.Open();

        _translateCts?.Cancel();
        var cts = _translateCts = new CancellationTokenSource();
        try
        {
            var result = await TranslateService.TranslateAsync(text, cts.Token);
            if (cts.IsCancellationRequested) return;
            TranslateResult.Text = result.Text;
            TranslateLangs.Text = $"{LanguageName(result.From)} → {LanguageName(result.To)}";
        }
        catch (OperationCanceledException) { }
        catch
        {
            TranslateResult.Text = "Не удалось перевести: нет связи с переводчиком";
        }
        finally { TranslateResult.Opacity = 1; }
    }

    private static string LanguageName(string code) => code switch
    {
        "ru" => "Русский", "en" => "Английский", "uk" => "Украинский", "de" => "Немецкий", "fr" => "Французский",
        "es" => "Испанский", "it" => "Итальянский", "ja" => "Японский", "zh-CN" or "zh" => "Китайский", "ko" => "Корейский",
        "pl" => "Польский", "tr" => "Турецкий", "pt" => "Португальский", "be" => "Белорусский", "kk" => "Казахский",
        _ => code,
    };

    private void OnTranslateCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(TranslateResult.Text); } catch { }
        _translateCloser?.Close();
        ShowActivity("", "Перевод скопирован", null, "", TimeSpan.FromSeconds(1.5));
    }

    /// <summary>
    /// Забирает выделенный текст из активного окна: жмём за пользователя Ctrl+C и потом возвращаем
    /// в буфер то, что там было.
    /// </summary>
    private async Task<string?> CopySelectionAsync()
    {
        await KeySender.WaitForModifiersReleasedAsync();
        IDataObject? saved = null;
        try
        {
            var current = Clipboard.GetDataObject();
            if (current != null)
            {
                var copy = new DataObject();
                foreach (var format in current.GetFormats(false))
                    try { if (current.GetData(format, false) is { } data) copy.SetData(format, data); } catch { }
                saved = copy;
            }
        }
        catch { }

        var before = GetClipboardSequenceNumber();
        _ignoreClipboardUntil = DateTime.Now.AddSeconds(2);
        KeySender.Send("Ctrl+C");
        for (var i = 0; i < 25 && GetClipboardSequenceNumber() == before; i++) await Task.Delay(20);
        if (GetClipboardSequenceNumber() == before) return null; // ничего не выделено

        string? text = null;
        for (var i = 0; i < 5 && text == null; i++)
        {
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch { await Task.Delay(40); }
        }
        try { if (saved != null) Clipboard.SetDataObject(saved, true); } catch { }
        return text;
    }

    private DateTime _ignoreClipboardUntil;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
