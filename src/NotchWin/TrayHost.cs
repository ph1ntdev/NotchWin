using System.IO;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.WindowsTray;
using Microsoft.Win32;

namespace NotchWin;

/// <summary>
/// Значки программ из трея Windows на нашей панели. Мы регистрируем своё окно Shell_TrayWnd
/// поверх окна Проводника (как RetroBar через библиотеку ManagedShell): программы заново
/// добавляют значки уже к нам, а копии в трее Проводника мы удаляем. При выходе программы
/// снова получают сигнал TaskbarCreated и возвращают значки на панель задач.
/// </summary>
internal static class TrayHost
{
    // Системные значки (сеть, звук, батарея и т. п.) у нас свои, их не показываем.
    private const string SystemGuidSuffix = "-23e3-4229-82c1-e41cb67d5b9c";

    private static TrayService? _service;
    private static NotificationArea? _area;
    private static readonly DispatcherTimer _keepTaskbar = new() { Interval = TimeSpan.FromSeconds(3) };
    private static readonly HashSet<NotifyIcon> _watched = new();
    private static DateTime _startedAt;

    public static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    /// <summary>Список значков поменялся (добавили, убрали, спрятали).</summary>
    public static event Action? Changed;
    /// <summary>Всплывающее сообщение программы из трея: заголовок, текст, значок.</summary>
    public static event Action<string, string, ImageSource?>? Balloon;

    public static bool Running => _area != null;

    /// <summary>Постоянный ключ значка для настроек: подпись меняется (счётчики, громкость), а программа и номер — нет.</summary>
    public static string Key(NotifyIcon icon) =>
        icon.GUID != Guid.Empty ? icon.GUID.ToString() : $"{Path.GetFileName(icon.Path)}:{icon.UID}";

    public static IEnumerable<NotifyIcon> Icons =>
        _area == null ? Enumerable.Empty<NotifyIcon>() : _area.TrayIcons.Where(i => !i.IsHidden && !IsSystem(i)).ToList();

    public static void Apply()
    {
        var on = AppSettings.Current.TrayIcons != "off";
        if (on && _area == null) Start();
        else if (!on && _area != null) Stop();
    }

    private static void Start()
    {
        try
        {
            _startedAt = DateTime.Now;
            AttachLibraryLog();
            _service = new TrayService();
            _area = new NotificationArea(Array.Empty<string>(), _service, new ExplorerTrayService());
            _area.TrayIcons.CollectionChanged += OnCollectionChanged;
            _area.NotificationBalloonShown += OnBalloon;
            _area.Initialize();
            if (_area.IsFailed) { Log("init failed"); Stop(); return; }
            foreach (var icon in _area.TrayIcons.ToList()) Watch(icon);
            // Программы, запущенные от администратора, не могут прислать значок обычному окну,
            // пока оно явно не разрешит WM_COPYDATA (Проводник делает так же). Разрешаем и просим всех
            // прислать значки ещё раз.
            ChangeWindowMessageFilterEx(_area.Handle, 0x004A, 1, IntPtr.Zero);
            SendNotifyMessage((IntPtr)0xFFFF, TaskbarCreatedMessage, IntPtr.Zero, IntPtr.Zero);
            NotifyMessageOnlyWindows();
            StartWatchdog();
            KeepTaskbarTopmost();
            _keepTaskbar.Tick -= OnKeepTaskbar;
            _keepTaskbar.Tick += OnKeepTaskbar;
            _keepTaskbar.Start();
            Log($"started, {_area.TrayIcons.Count} icons");
        }
        catch (Exception e)
        {
            Log("start error: " + e.Message);
            Stop();
        }
        Changed?.Invoke();
    }

    public static void Stop()
    {
        _keepTaskbar.Stop();
        foreach (var icon in _watched) icon.PropertyChanged -= OnIconChanged;
        _watched.Clear();
        try
        {
            if (_area != null)
            {
                _area.TrayIcons.CollectionChanged -= OnCollectionChanged;
                _area.NotificationBalloonShown -= OnBalloon;
                _area.Dispose();
            }
        }
        catch { }
        // NotificationArea.Dispose закрывает наше окно трея и рассылает TaskbarCreated:
        // программы вернут значки в трей Windows. Если запуск не удался, закрываем окно сами.
        try { if (_area == null || _area.IsFailed) _service?.Dispose(); } catch { }
        _area = null;
        _service = null;
        Changed?.Invoke();
    }

    /// <summary>Проводник перезапустился и разослал TaskbarCreated: забираем значки заново.</summary>
    public static void OnTaskbarCreated()
    {
        // Свою же рассылку при запуске игнорируем.
        if (_area == null || (DateTime.Now - _startedAt).TotalSeconds < 5) return;
        Log("explorer restarted");
        Stop();
        Start();
    }

    private static bool IsSystem(NotifyIcon icon) =>
        icon.GUID != Guid.Empty && icon.GUID.ToString().EndsWith(SystemGuidSuffix, StringComparison.OrdinalIgnoreCase);

    private static void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (NotifyIcon icon in e.NewItems) Watch(icon);
        if (e.OldItems != null)
            foreach (NotifyIcon icon in e.OldItems)
            {
                icon.PropertyChanged -= OnIconChanged;
                _watched.Remove(icon);
            }
        RaiseChanged();
    }

    private static void Watch(NotifyIcon icon)
    {
        if (!_watched.Add(icon)) return;
        Log($"icon: {icon.Title} | {icon.Path} | hidden={icon.IsHidden} system={IsSystem(icon)}");
        icon.PropertyChanged += OnIconChanged;
        // Убираем копию из трея Проводника позже, не внутри события коллекции: синхронный вызов
        // в Проводник успевает принять значки других программ, а добавлять их в коллекцию
        // во время её же события нельзя — такие значки терялись.
        if (!IsSystem(icon))
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_watched.Contains(icon)) RemoveFromExplorer(icon);
            });
    }

    private static void OnIconChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NotifyIcon.IsHidden) or nameof(NotifyIcon.Path)) RaiseChanged();
    }

    private static bool _pending;
    private static void RaiseChanged()
    {
        if (_pending) return;
        _pending = true;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => { _pending = false; Changed?.Invoke(); });
    }

    private static void OnBalloon(object? sender, NotificationBalloonEventArgs e)
    {
        var b = e.Balloon;
        if (b == null || string.IsNullOrWhiteSpace(b.Title + b.Info)) return;
        e.Handled = true;
        if (AppSettings.Current.NotifyTray) Balloon?.Invoke(b.Title ?? "", b.Info ?? "", b.Icon ?? b.NotifyIcon?.Icon);
    }

    // ---------- Какие значки Windows показывает на панели, а какие прячет под стрелку ----------

    private static Dictionary<string, bool>? _promoted;
    private static DateTime _promotedAt;

    /// <summary>
    /// Windows 11 хранит выбор «показывать на панели задач» в HKCU\Control Panel\NotifyIconSettings.
    /// Если записи нет (Windows 10 или новая программа), считаем значок видимым.
    /// </summary>
    public static bool IsPromoted(NotifyIcon icon)
    {
        if (_promoted == null || (DateTime.Now - _promotedAt).TotalSeconds > 5)
        {
            _promotedAt = DateTime.Now;
            _promoted = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
                if (root == null) { _promoted = null; return true; }
                foreach (var name in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(name);
                    if (key?.GetValue("ExecutablePath") is not string exe) continue;
                    var file = Path.GetFileName(exe);
                    var promoted = key.GetValue("IsPromoted") is int p && p != 0;
                    _promoted[file] = _promoted.TryGetValue(file, out var was) && was || promoted;
                }
            }
            catch { _promoted = null; return true; }
        }
        if (_promoted == null || string.IsNullOrEmpty(icon.Path)) return true;
        return !_promoted.TryGetValue(Path.GetFileName(icon.Path), out var on) || on;
    }

    // ---------- Трей Проводника ----------

    private static IntPtr ExplorerTray()
    {
        var own = _area?.Handle ?? IntPtr.Zero;
        var hwnd = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        while (hwnd != IntPtr.Zero && hwnd == own) hwnd = FindWindowEx(IntPtr.Zero, hwnd, "Shell_TrayWnd", null);
        return hwnd;
    }

    private static void OnKeepTaskbar(object? sender, EventArgs e) => KeepTaskbarTopmost();

    /// <summary>
    /// ManagedShell опускает панель задач Windows в самый низ, чтобы наше окно трея было выше.
    /// Панель задач у нас остаётся на экране, поэтому ставим её сразу под наше окно: так она
    /// снова поверх обычных окон, а значки по-прежнему приходят к нам.
    /// </summary>
    private static void KeepTaskbarTopmost()
    {
        var own = _area?.Handle ?? IntPtr.Zero;
        var taskbar = ExplorerTray();
        if (own == IntPtr.Zero || taskbar == IntPtr.Zero) return;
        if ((GetWindowLong(taskbar, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0) return;
        SetWindowPos(taskbar, own, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>Программа отдала значок нам — убираем его старую копию из трея Проводника.</summary>
    private static void RemoveFromExplorer(NotifyIcon icon)
    {
        var tray = ExplorerTray();
        if (tray == IntPtr.Zero) return;
        var data = new ShellTrayData
        {
            Magic = 0x34753423,
            Message = 2, // NIM_DELETE
            Nid = new NotifyIconData32
            {
                hWnd = (uint)icon.HWnd.ToInt64(),
                uID = icon.UID,
                uFlags = icon.GUID != Guid.Empty ? 0x20u : 0u, // NIF_GUID
                guidItem = icon.GUID,
            },
        };
        data.Nid.cbSize = Marshal.SizeOf<NotifyIconData32>();
        var size = Marshal.SizeOf<ShellTrayData>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(data, buffer, false);
            var cds = new CopyDataStruct { dwData = (IntPtr)1, cbData = size, lpData = buffer };
            SendMessageTimeout(tray, 0x004A, _area?.Handle ?? IntPtr.Zero, ref cds, 0x0002, 500, out _);
        }
        catch { }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>
    /// Часть программ держит значок на невидимом «служебном» окне (message-only). Такие окна не получают
    /// общую рассылку TaskbarCreated, поэтому отправляем её каждому из них отдельно.
    /// </summary>
    private static void NotifyMessageOnlyWindows()
    {
        var count = 0;
        var hwnd = IntPtr.Zero;
        var own = Environment.ProcessId;
        while ((hwnd = FindWindowEx((IntPtr)(-3) /* HWND_MESSAGE */, hwnd, null, null)) != IntPtr.Zero && count < 5000)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == own) continue;
            PostMessage(hwnd, TaskbarCreatedMessage, IntPtr.Zero, IntPtr.Zero);
            count++;
        }
        Log($"TaskbarCreated sent to {count} message-only windows");
    }

    // ---------- Если программу закрыли не через «Выход» ----------

    private static bool _watchdogStarted;

    /// <summary>
    /// Запускает отдельный маленький процесс (наш же exe с ключом --tray-watchdog), который ждёт
    /// завершения программы и рассылает TaskbarCreated. Так значки вернутся в трей Windows, даже
    /// если программу закрыли вместе с консолью или через диспетчер задач. Запускаем через WMI,
    /// чтобы процесс не был «дочерним» и не завершился вместе с нами.
    /// </summary>
    private static void StartWatchdog()
    {
        if (_watchdogStarted) return;
        _watchdogStarted = true;
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            using var cls = new System.Management.ManagementClass("Win32_Process");
            var args = cls.GetMethodParameters("Create");
            args["CommandLine"] = $"\"{exe}\" --tray-watchdog {Environment.ProcessId}";
            cls.InvokeMethod("Create", args, null);
        }
        catch (Exception e) { Log("watchdog: " + e.Message); }
    }

    /// <summary>Режим сторожа: дождаться выхода программы и вернуть значки Windows.</summary>
    public static void RunWatchdog(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            process.WaitForExit();
        }
        catch { }
        Thread.Sleep(300);
        SendNotifyMessage((IntPtr)0xFFFF, TaskbarCreatedMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private static bool _libraryLog;

    /// <summary>Подробный журнал самой библиотеки (что прислала каждая программа) — tray-debug.log рядом с exe.</summary>
    private static void AttachLibraryLog()
    {
        if (_libraryLog) return;
        _libraryLog = true;
        try
        {
            var log = new ManagedShell.Common.Logging.Observers.FileLog(Path.Combine(AppContext.BaseDirectory, "tray-debug.log"));
            log.Open();
            ManagedShell.Common.Logging.ShellLogger.Severity = ManagedShell.Common.Logging.LogSeverity.Debug;
            ManagedShell.Common.Logging.ShellLogger.Attach(log, false);
        }
        catch (Exception e) { Log("library log: " + e.Message); }
    }

    private static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "tray.log"), $"{DateTime.Now:HH:mm:ss} {text}\n"); } catch { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData32
    {
        public int cbSize;
        public uint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public uint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public uint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellTrayData
    {
        public int Magic;
        public uint Message;
        public NotifyIconData32 Nid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x8;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int msg, int action, IntPtr info);
    [DllImport("user32.dll")] private static extern bool SendNotifyMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, ref CopyDataStruct lParam, uint flags, uint timeout, out IntPtr result);
}
