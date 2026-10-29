using System.IO;
using System.Text.Json;

namespace NotchWin;

/// <summary>Настройки программы. Хранятся в %LOCALAPPDATA%\NotchWin\settings.json.</summary>
public sealed class AppSettings
{
    // ---------- Общее ----------

    /// <summary>Запускать вместе с Windows.</summary>
    public bool RunAtStartup { get; set; }

    /// <summary>Монитор для панели и челки (имя устройства вида \\.\DISPLAY2). Пусто — основной.</summary>
    public string Monitor { get; set; } = "";

    /// <summary>Прятать панель и челку, когда игра или видео на весь экран.</summary>
    public bool HideInFullscreen { get; set; } = true;

    // ---------- Челка ----------

    public double NotchWidth { get; set; } = 230;
    public double NotchRadius { get; set; } = 10;
    public double NotchExpandedRadius { get; set; } = 30;
    public double NotchOpacity { get; set; } = 1;
    /// <summary>Тонкий светлый контур челки (виден на тёмных обоях).</summary>
    public bool NotchOutline { get; set; } = true;
    /// <summary>Вкладки в нужном порядке. Скрытые лежат в HiddenTabs.</summary>
    public List<string> Tabs { get; set; } = new() { "media", "shelf", "timer", "note" };
    public List<string> HiddenTabs { get; set; } = new();
    /// <summary>С какой вкладки открываться: media, shelf, timer, note или last (где закрыли).</summary>
    public string StartTab { get; set; } = "media";
    public int OpenDelayMs { get; set; }
    public int CloseDelayMs { get; set; } = 300;
    /// <summary>Громкость и яркость показываются в челке вместо окошка Windows.</summary>
    public bool ShowVolumeInNotch { get; set; } = true;
    public bool ShowEqualizer { get; set; } = true;

    // ---------- Верхняя панель ----------

    public double BarHeight { get; set; } = 32;
    /// <summary>Затемнение панели в процентах.</summary>
    public int BarDim { get; set; } = 55;
    public bool BarAppName { get; set; } = true;
    public bool BarSearch { get; set; } = true;
    public bool BarWeather { get; set; } = true;
    public bool BarBin { get; set; } = true;
    public bool BarLayout { get; set; } = true;
    public bool BarVolume { get; set; } = true;
    public bool BarBattery { get; set; } = true;
    /// <summary>Значки программ из трея на панели: windows — как выбрано в Windows (остальные под стрелкой), all — все, off — не показывать.</summary>
    public string TrayIcons { get; set; } = "windows";
    /// <summary>Куда пользователь сам перетащил значок: true — на панель, false — под стрелку. Ключ — TrayHost.Key.</summary>
    public Dictionary<string, bool> TrayInBar { get; set; } = new();
    /// <summary>Порядок значков (сначала панель, потом под стрелкой).</summary>
    public List<string> TrayOrder { get; set; } = new();
    public bool ClockSeconds { get; set; }
    public bool ClockDate { get; set; } = true;
    public bool ClockWeekday { get; set; } = true;

    // ---------- Живые уведомления ----------

    public bool NotifyTelegram { get; set; } = true;
    public bool NotifyVk { get; set; } = true;
    /// <summary>Всплывающие сообщения программ из трея (пока значки трея на нашей панели, Windows их не покажет).</summary>
    public bool NotifyTray { get; set; } = true;
    public bool NotifyDiscordCalls { get; set; } = true;
    /// <summary>Время звонка Discord в крыле челки.</summary>
    public bool DiscordWing { get; set; } = true;
    public bool ChargingAnimation { get; set; } = true;
    /// <summary>Предупреждать о заряде ниже этого порога (и ещё раз на половине). 0 — не предупреждать.</summary>
    public int LowBatteryPercent { get; set; } = 20;
    public bool ShowDrives { get; set; } = true;

    // ---------- Полка и LocalSend ----------

    /// <summary>Сколько файлов держать на полке (закреплённые не считаются). 0 — без ограничения.</summary>
    public int ShelfLimit { get; set; }
    public bool ShelfScreenshots { get; set; } = true;
    /// <summary>Отправка через LocalSend: зона при перетаскивании, кнопка и пункт меню на полке.</summary>
    public bool LocalSendEnabled { get; set; } = true;
    /// <summary>Как челку видят другие устройства в LocalSend. Пусто — имя компьютера.</summary>
    public string LocalSendName { get; set; } = "";

    // ---------- Заметки ----------

    /// <summary>Где хранить быстрые заметки: auto (Obsidian, если он есть), obsidian или folder (обычная папка).</summary>
    public string NoteStorage { get; set; } = "auto";
    /// <summary>Папка для заметок без Obsidian. Пусто — «Документы\Заметки».</summary>
    public string NotesPath { get; set; } = "";

    /// <summary>Хранилище Obsidian. Пусто — то, что Obsidian открывал последним.</summary>
    public string ObsidianVault { get; set; } = "";
    /// <summary>inbox — каждая заметка отдельным файлом, daily — строкой в файл за сегодня.</summary>
    public string NoteTarget { get; set; } = "inbox";
    /// <summary>Папка для заметок внутри хранилища. Пусто — папка со словом Inbox в имени.</summary>
    public string NoteFolder { get; set; } = "";

    // ---------- Таймер ----------

    public int PomodoroWork { get; set; } = 25;
    public int PomodoroShortBreak { get; set; } = 5;
    public int PomodoroLongBreak { get; set; } = 15;
    public bool TimerSound { get; set; } = true;

    // ---------- Клавиши ----------

    /// <summary>Сочетание для поиска. Пусто — только значок лупы.</summary>
    public string SearchHotkey { get; set; } = "Alt+Space";
    public string TranslateHotkey { get; set; } = "Ctrl+Alt+T";

    /// <summary>
    /// Сочетания, которые пользователь задал в Discord (Настройки → Горячие клавиши) для микрофона и звука.
    /// Челка нажимает их сама, когда жмут кнопки в челке.
    /// </summary>
    public string DiscordMuteKey { get; set; } = "Ctrl+Shift+Alt+M";
    public string DiscordDeafenKey { get; set; } = "Ctrl+Shift+Alt+D";

    // ---------- Погода ----------

    /// <summary>Город для погоды, если геопозиция выключена или недоступна. Пусто — место по интернет-подключению.</summary>
    public string WeatherCity { get; set; } = "";

    /// <summary>Сначала пытаться определить место по геопозиции Windows.</summary>
    public bool WeatherUseLocation { get; set; } = true;

    public bool Fahrenheit { get; set; }

    /// <summary>Какой сервис спрашивать первым: auto, Open-Meteo, MET Norway, wttr.in.</summary>
    public string WeatherSource { get; set; } = "auto";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotchWin", "settings.json");

    public static AppSettings Current { get; private set; } = Load();

    /// <summary>Настройки сохранили — пора перечитать то, что от них зависит (например, погоду).</summary>
    public static event Action? Changed;

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    /// <summary>
    /// Меняет настройку и сразу применяет её (событие Changed). Файл пишется чуть позже,
    /// чтобы ползунок не сохранял его на каждый пиксель.
    /// </summary>
    public static void Update(Action<AppSettings> change)
    {
        change(Current);
        Changed?.Invoke();
        _saveTimer ??= CreateSaveTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static System.Windows.Threading.DispatcherTimer? _saveTimer;

    private static System.Windows.Threading.DispatcherTimer CreateSaveTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => { timer.Stop(); Current.WriteFile(); };
        return timer;
    }

    /// <summary>Сохранить файл сразу, если запись ещё ждёт (при выходе).</summary>
    public static void Flush()
    {
        try
        {
            if (_saveTimer is not { IsEnabled: true }) return;
            _saveTimer.Stop();
        }
        catch { /* вызвали не из окна (аварийный выход) — просто пишем файл */ }
        Current.WriteFile();
    }

    /// <summary>Пока в настройках записывают сочетание, глобальные горячие клавиши отключены,
    /// иначе нажатие перехватит сама программа.</summary>
    public static bool RecordingHotkey { get; private set; }
    public static event Action? RecordingHotkeyChanged;

    public static void SetRecordingHotkey(bool on)
    {
        if (RecordingHotkey == on) return;
        RecordingHotkey = on;
        RecordingHotkeyChanged?.Invoke();
    }

    private void WriteFile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void Save()
    {
        WriteFile();
        Current = this;
        Changed?.Invoke();
    }
}
