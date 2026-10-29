using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace NotchWin;

/// <summary>
/// Окно настроек: разделы слева, справа группы строк как в macOS. Каждое изменение сразу
/// применяется (AppSettings.Update) — панель и челка перестраиваются на лету.
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;

    /// <summary>Открывает окно настроек; если оно уже открыто — просто выводит его вперёд.</summary>
    public static void ShowSingle(string? section = null)
    {
        if (_instance == null)
        {
            _instance = new SettingsWindow();
            _instance.Closed += (_, _) => _instance = null;
            _instance.Show();
        }
        if (section != null) _instance.Open(section);
        _instance.Activate();
    }

    private static readonly (string Id, string Icon, string Title)[] Sections =
    {
        ("general", "", "Общее"),
        ("notch", "", "Челка"),
        ("bar", "", "Верхняя панель"),
        ("live", "", "Уведомления"),
        ("shelf", "", "Полка и LocalSend"),
        ("notes", "", "Заметки"),
        ("timer", "", "Таймер"),
        ("keys", "", "Клавиши"),
        ("weather", "", "Погода"),
    };

    private static string _lastSection = "general";
    private string _section = "";

    public SettingsWindow()
    {
        InitializeComponent();
        foreach (var (id, icon, title) in Sections)
        {
            var item = new RadioButton { Style = (Style)FindResource("NavItem"), GroupName = "nav", Tag = id };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14,
                Width = 26, VerticalAlignment = VerticalAlignment.Center,
            });
            content.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
            item.Content = content;
            item.Checked += (_, _) => Open(id);
            Nav.Children.Add(item);
        }
        Open(_lastSection);

        SmoothScroll.Attach(PageScroll, horizontal: false, factor: 0.5);
        ContentRendered += (_, _) => Glass.Apply(this, Card);
        PreviewKeyDown += OnWindowKeyDown;
        Closed += (_, _) => AppSettings.SetRecordingHotkey(false);
    }

    private void Open(string id)
    {
        if (_section == id) return;
        _section = _lastSection = id;
        foreach (RadioButton item in Nav.Children) item.IsChecked = (string)item.Tag == id;
        PageTitle.Text = Sections.First(s => s.Id == id).Title;
        StopRecording();
        PageHost.Children.Clear();
        PageScroll.ScrollToTop();
        switch (id)
        {
            case "general": BuildGeneral(); break;
            case "notch": BuildNotch(); break;
            case "bar": BuildBar(); break;
            case "live": BuildLive(); break;
            case "shelf": BuildShelf(); break;
            case "notes": BuildNotes(); break;
            case "timer": BuildTimer(); break;
            case "keys": BuildKeys(); break;
            case "weather": BuildWeather(); break;
        }
    }

    private static AppSettings S => AppSettings.Current;

    // ---------- Разделы ----------

    private void BuildGeneral()
    {
        Group("Запуск",
            SwitchRow("Запускать вместе с Windows", S.RunAtStartup, v =>
            {
                AppSettings.Update(s => s.RunAtStartup = v);
                Autostart.Apply(v);
            }),
            SwitchRow("Прятать в полноэкранных играх и видео", S.HideInFullscreen, v => AppSettings.Update(s => s.HideInFullscreen = v)));

        var screens = Screens.All();
        if (screens.Count > 1)
        {
            var options = new List<(string, string)> { ("", "Основной") };
            options.AddRange(screens.Select((sc, i) =>
                (sc.Device, $"{i + 1}: {sc.Bounds.Right - sc.Bounds.Left}×{sc.Bounds.Bottom - sc.Bounds.Top}{(sc.Primary ? " ★" : "")}")));
            Group("Монитор", ChoiceRow("Панель и челка на мониторе", options, S.Monitor, v => AppSettings.Update(s => s.Monitor = v)));
        }
        else Group("Монитор", Info("Подключён один монитор. Когда подключите второй, здесь можно будет выбрать, где показывать панель и челку."));
    }

    private void BuildNotch()
    {
        Group("Размер и форма",
            SliderRow("Ширина", 180, 300, 2, S.NotchWidth, v => $"{v:0} px", v => AppSettings.Update(s => s.NotchWidth = v)),
            SliderRow("Скругление углов", 0, 16, 1, S.NotchRadius, v => $"{v:0} px", v => AppSettings.Update(s => s.NotchRadius = v)),
            SliderRow("Скругление раскрытой", 12, 40, 1, S.NotchExpandedRadius, v => $"{v:0} px", v => AppSettings.Update(s => s.NotchExpandedRadius = v)),
            SliderRow("Непрозрачность", 50, 100, 5, S.NotchOpacity * 100, v => $"{v:0}%", v => AppSettings.Update(s => s.NotchOpacity = v / 100)),
            SwitchRow("Светлый контур", S.NotchOutline, v => AppSettings.Update(s => s.NotchOutline = v)));

        Group("Поведение",
            SliderRow("Раскрывать при наведении через", 0, 800, 50, S.OpenDelayMs, v => v == 0 ? "сразу" : $"{v:0} мс",
                v => AppSettings.Update(s => s.OpenDelayMs = (int)v)),
            SliderRow("Сворачивать через", 100, 1500, 50, S.CloseDelayMs, v => $"{v:0} мс", v => AppSettings.Update(s => s.CloseDelayMs = (int)v)),
            ChoiceRow("Открываться на вкладке", new[]
            {
                ("media", "Музыка"), ("shelf", "Полка"), ("timer", "Таймер"), ("note", "Заметка"), ("last", "Последняя"),
            }, S.StartTab, v => AppSettings.Update(s => s.StartTab = v)),
            SwitchRow("Громкость и яркость в челке", S.ShowVolumeInNotch, v => AppSettings.Update(s => s.ShowVolumeInNotch = v),
                "Вместо окошка Windows слева сверху"),
            SwitchRow("Эквалайзер, пока играет музыка", S.ShowEqualizer, v => AppSettings.Update(s => s.ShowEqualizer = v)));

        var tabs = new StackPanel();
        Group("Вкладки", tabs);
        RenderTabs(tabs);
    }

    private static readonly Dictionary<string, (string Icon, string Name)> TabInfo = new()
    {
        ["media"] = ("", "Музыка"),
        ["shelf"] = ("", "Полка"),
        ["timer"] = ("", "Таймер"),
        ["note"] = ("", "Заметка"),
    };

    /// <summary>Список вкладок: включить или скрыть, стрелками поменять порядок.</summary>
    private void RenderTabs(StackPanel host)
    {
        host.Children.Clear();
        var order = S.Tabs.Where(TabInfo.ContainsKey).Distinct().ToList();
        order.AddRange(TabInfo.Keys.Except(order));
        for (var i = 0; i < order.Count; i++)
        {
            var id = order[i];
            var index = i;
            var (icon, name) = TabInfo[id];
            var row = new Grid { Height = 40 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14,
                Foreground = Brushes.White, Width = 28, VerticalAlignment = VerticalAlignment.Center,
            });
            var label = new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            var arrows = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            arrows.Children.Add(IconButton("", "Выше", index > 0, () => MoveTab(host, order, index, -1)));
            arrows.Children.Add(IconButton("", "Ниже", index < order.Count - 1, () => MoveTab(host, order, index, 1)));
            Grid.SetColumn(arrows, 2);
            row.Children.Add(arrows);

            var visible = new CheckBox
            {
                Style = (Style)FindResource("Switch"), IsChecked = !S.HiddenTabs.Contains(id), VerticalAlignment = VerticalAlignment.Center,
            };
            visible.Click += (_, _) =>
            {
                var on = visible.IsChecked == true;
                // Хотя бы одна вкладка должна остаться.
                if (!on && order.Count(t => !S.HiddenTabs.Contains(t)) <= 1) { visible.IsChecked = true; return; }
                AppSettings.Update(s =>
                {
                    s.HiddenTabs.Remove(id);
                    if (!on) s.HiddenTabs.Add(id);
                });
            };
            Grid.SetColumn(visible, 3);
            row.Children.Add(visible);
            AddRow(host, row);
        }
    }

    private void MoveTab(StackPanel host, List<string> order, int index, int delta)
    {
        var list = order.ToList();
        (list[index], list[index + delta]) = (list[index + delta], list[index]);
        AppSettings.Update(s => s.Tabs = list);
        RenderTabs(host);
    }

    private void BuildBar()
    {
        Group("Вид",
            SliderRow("Высота", 26, 44, 1, S.BarHeight, v => $"{v:0} px", v => AppSettings.Update(s => s.BarHeight = v),
                "Свёрнутая челка всегда такой же высоты"),
            SliderRow("Затемнение", 0, 90, 5, S.BarDim, v => $"{v:0}%", v => AppSettings.Update(s => s.BarDim = (int)v)));

        Group("Что показывать",
            SwitchRow("Название активной программы", S.BarAppName, v => AppSettings.Update(s => s.BarAppName = v)),
            SwitchRow("Поиск", S.BarSearch, v => AppSettings.Update(s => s.BarSearch = v)),
            SwitchRow("Погода", S.BarWeather, v => AppSettings.Update(s => s.BarWeather = v)),
            SwitchRow("Корзина", S.BarBin, v => AppSettings.Update(s => s.BarBin = v)),
            SwitchRow("Раскладка клавиатуры", S.BarLayout, v => AppSettings.Update(s => s.BarLayout = v)),
            SwitchRow("Громкость и микшер", S.BarVolume, v => AppSettings.Update(s => s.BarVolume = v)),
            SwitchRow("Батарея", S.BarBattery, v => AppSettings.Update(s => s.BarBattery = v)));

        Group("Значки из трея",
            ChoiceRow("Показывать", new[] { ("windows", "Как в Windows"), ("all", "Все"), ("off", "Нет") }, S.TrayIcons,
                v => AppSettings.Update(s => s.TrayIcons = v),
                "«Как в Windows»: на панели те значки, что включены в Параметрах Windows → Персонализация → Панель задач, остальные под стрелкой"),
            Info("Значки можно перетаскивать мышью: менять порядок и переносить между панелью и списком под стрелкой."),
            Info("Пока значки на нашей панели, в трее Windows их нет. После выхода из программы они вернутся на место."));

        Group("Часы",
            SwitchRow("День недели", S.ClockWeekday, v => AppSettings.Update(s => s.ClockWeekday = v)),
            SwitchRow("Дата", S.ClockDate, v => AppSettings.Update(s => s.ClockDate = v)),
            SwitchRow("Секунды", S.ClockSeconds, v => AppSettings.Update(s => s.ClockSeconds = v)));
    }

    private void BuildLive()
    {
        Group("Сообщения",
            SwitchRow("Telegram (и неофициальные клиенты)", S.NotifyTelegram, v => AppSettings.Update(s => s.NotifyTelegram = v)),
            SwitchRow("ВКонтакте", S.NotifyVk, v => AppSettings.Update(s => s.NotifyVk = v)),
            SwitchRow("Сообщения программ из трея", S.NotifyTray, v => AppSettings.Update(s => s.NotifyTray = v)),
            Info("Уведомления берутся из центра уведомлений Windows: в самой программе они должны быть включены."));

        Group("Discord",
            SwitchRow("Входящие звонки", S.NotifyDiscordCalls, v => AppSettings.Update(s => s.NotifyDiscordCalls = v)),
            SwitchRow("Время звонка сбоку от челки", S.DiscordWing, v => AppSettings.Update(s => s.DiscordWing = v)));

        Group("Батарея",
            SwitchRow("Анимация при подключении зарядки", S.ChargingAnimation, v => AppSettings.Update(s => s.ChargingAnimation = v)),
            SliderRow("Предупреждать о низком заряде", 0, 50, 5, S.LowBatteryPercent, v => v == 0 ? "выкл" : $"{v:0}%",
                v => AppSettings.Update(s => s.LowBatteryPercent = (int)v), "Второй раз — на половине этого значения"));

        Group("Флешки",
            SwitchRow("Показывать флешки на полке", S.ShowDrives, v => AppSettings.Update(s => s.ShowDrives = v)));
    }

    private void BuildShelf()
    {
        Group("Полка",
            SliderRow("Сколько файлов хранить", 0, 100, 5, S.ShelfLimit, v => v == 0 ? "без ограничения" : $"{v:0}",
                v => AppSettings.Update(s => s.ShelfLimit = (int)v), "Старые уходят первыми, закреплённые остаются"),
            SwitchRow("Класть скриншоты Win+Shift+S на полку", S.ShelfScreenshots, v => AppSettings.Update(s => s.ShelfScreenshots = v)));

        FrameworkElement? nameRow = null;
        var enabled = SwitchRow("Отправка через LocalSend", S.LocalSendEnabled, v =>
        {
            AppSettings.Update(s => s.LocalSendEnabled = v);
            if (nameRow != null) nameRow.IsEnabled = v;
        }, "Половина челки при перетаскивании файла, кнопка на полке и пункт в меню файла. "
           + "Выключите, если LocalSend не установлен на телефоне — тогда файлы просто ложатся на полку");
        nameRow = TextRow("Имя этого компьютера", S.LocalSendName, "Сейчас: " + Environment.MachineName + " (челка)",
            v => AppSettings.Update(s => s.LocalSendName = v), "Так челку видят телефон и другие устройства");
        nameRow.IsEnabled = S.LocalSendEnabled;
        Group("LocalSend", enabled, nameRow);
    }

    private void BuildNotes()
    {
        var vault = Services.ObsidianService.FindVault();
        var obsidian = Services.NotesService.UsesObsidian;
        Group("Где хранить",
            ChoiceRow("Заметки сохраняются", new[] { ("auto", "Авто"), ("folder", "В папку"), ("obsidian", "В Obsidian") },
                S.NoteStorage, v =>
                {
                    AppSettings.Update(s => s.NoteStorage = v);
                    Reopen(); // у Obsidian и обычной папки разные строки ниже
                }, vault != null ? "Авто — в Obsidian, он у вас установлен" : "Авто — в папку, Obsidian не найден"),
            obsidian
                ? FolderRow("Хранилище Obsidian", S.ObsidianVault, vault != null ? "Сейчас: " + vault : "Не найдено — выберите папку", v => AppSettings.Update(s => s.ObsidianVault = v),
                    "Пусто — то, что Obsidian открывал последним")
                : FolderRow("Папка", S.NotesPath, "Сейчас: " + Services.NotesService.PlainFolder, v => AppSettings.Update(s => s.NotesPath = v),
                    "Обычные текстовые файлы Markdown, открываются любым редактором"));

        FrameworkElement? folderRow = null;
        var target = ChoiceRow("Как сохранять", new[] { ("inbox", "Каждую отдельно"), ("daily", "Одним файлом за день") },
            S.NoteTarget, v =>
            {
                AppSettings.Update(s => s.NoteTarget = v);
                if (folderRow != null) folderRow.IsEnabled = v == "inbox";
            }, obsidian ? "За день — в ежедневную заметку Obsidian" : "За день — файл вида 2026-10-29.md, строки со временем");
        if (!obsidian)
        {
            Group("Формат", target);
            return;
        }
        folderRow = TextRow("Папка в хранилище", S.NoteFolder,
            "Сейчас: " + (vault != null ? System.IO.Path.GetFileName(Services.ObsidianService.InboxFolder(vault)) : "Inbox"),
            v => AppSettings.Update(s => s.NoteFolder = v), "Пусто — папка со словом Inbox в названии");
        folderRow.IsEnabled = S.NoteTarget == "inbox";
        Group("Формат", target, folderRow);
    }

    /// <summary>Перестраивает открытый раздел (когда от одной настройки зависят другие строки).</summary>
    private void Reopen()
    {
        var section = _section;
        var offset = PageScroll.VerticalOffset;
        _section = "";
        Dispatcher.BeginInvoke(() =>
        {
            Open(section);
            PageScroll.ScrollToVerticalOffset(offset);
        });
    }

    private void BuildTimer()
    {
        Group("Помодоро",
            SliderRow("Работа", 5, 90, 5, S.PomodoroWork, v => $"{v:0} мин", v => AppSettings.Update(s => s.PomodoroWork = (int)v)),
            SliderRow("Короткий перерыв", 1, 30, 1, S.PomodoroShortBreak, v => $"{v:0} мин", v => AppSettings.Update(s => s.PomodoroShortBreak = (int)v)),
            SliderRow("Длинный перерыв (после 4 циклов)", 5, 60, 5, S.PomodoroLongBreak, v => $"{v:0} мин",
                v => AppSettings.Update(s => s.PomodoroLongBreak = (int)v)));
        Group("Звук",
            SwitchRow("Сигнал, когда время вышло", S.TimerSound, v => AppSettings.Update(s => s.TimerSound = v)));
    }

    private void BuildKeys()
    {
        Group("Челка и панель",
            HotkeyRow("Поиск", () => S.SearchHotkey, v => AppSettings.Update(s => s.SearchHotkey = v), () => TopBarWindow.SearchHotkeyBusy),
            HotkeyRow("Перевод выделенного текста", () => S.TranslateHotkey, v => AppSettings.Update(s => s.TranslateHotkey = v),
                () => MainWindow.TranslateHotkeyBusy));
        Group("Discord",
            HotkeyRow("Выключить микрофон", () => S.DiscordMuteKey, v => AppSettings.Update(s => s.DiscordMuteKey = v)),
            HotkeyRow("Выключить звук", () => S.DiscordDeafenKey, v => AppSettings.Update(s => s.DiscordDeafenKey = v)),
            Info("Кнопки в челке нажимают эти сочетания за вас. Задайте такие же в Discord: Настройки → Горячие клавиши."));
        PageHost.Children.Add(new TextBlock
        {
            Text = "Нажмите на сочетание, чтобы записать новое. Esc — отмена, Backspace — убрать сочетание.",
            Foreground = Faint, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 10, 0, 0),
        });
    }

    private void BuildWeather()
    {
        FrameworkElement? cityRow = null;
        var auto = SwitchRow("Определять автоматически", S.WeatherUseLocation, v =>
        {
            AppSettings.Update(s => s.WeatherUseLocation = v);
            if (cityRow != null) cityRow.IsEnabled = !v;
        }, "По геопозиции Windows, а если она выключена — по интернет-подключению");
        cityRow = TextRow("Город", S.WeatherCity, "Например, Казань", v => AppSettings.Update(s => s.WeatherCity = v),
            "Пусто — примерно по интернет-подключению");
        cityRow.IsEnabled = !S.WeatherUseLocation;
        Group("Место", auto, cityRow);

        Group("Данные",
            ChoiceRow("Единицы", new[] { ("c", "°C"), ("f", "°F") }, S.Fahrenheit ? "f" : "c", v => AppSettings.Update(s => s.Fahrenheit = v == "f")),
            ChoiceRow("Источник", new[] { ("auto", "Авто"), ("Open-Meteo", "Open-Meteo"), ("MET Norway", "MET Norway"), ("wttr.in", "wttr.in") },
                S.WeatherSource, v => AppSettings.Update(s => s.WeatherSource = v), "Если источник не ответит, спросим остальные"));
    }

    // ---------- Строки ----------

    private static readonly Brush Dim = Frozen(0x99);
    private static readonly Brush Faint = Frozen(0x66);

    private static Brush Frozen(byte alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
        b.Freeze();
        return b;
    }

    /// <summary>Группа строк на светлой подложке с заголовком сверху, как в настройках macOS.</summary>
    private void Group(string title, params FrameworkElement[] rows)
    {
        PageHost.Children.Add(new TextBlock
        {
            Text = title, Foreground = Dim, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 14, 0, 6),
        });
        var panel = new StackPanel();
        foreach (var row in rows) AddRow(panel, row);
        PageHost.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 2, 14, 2), Child = panel,
        });
    }

    private static void AddRow(Panel panel, FrameworkElement row)
    {
        // Тонкая линия между строками.
        if (panel.Children.Count > 0)
            panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)) });
        panel.Children.Add(row);
    }

    /// <summary>Строка: подпись (и подсказка под ней) слева, элемент управления справа.</summary>
    private static Grid Row(string label, UIElement control, string? hint = null)
    {
        var grid = new Grid { MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 16, 8) };
        text.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        if (hint != null)
            text.Children.Add(new TextBlock { Text = hint, Foreground = Faint, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        grid.Children.Add(text);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        // Выключенная строка (например, город при автоопределении) видна приглушённой.
        grid.IsEnabledChanged += (_, _) => grid.Opacity = grid.IsEnabled ? 1 : 0.4;
        return grid;
    }

    private static FrameworkElement Info(string text) => new TextBlock
    {
        Text = text, Foreground = Faint, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10),
    };

    private FrameworkElement SwitchRow(string label, bool value, Action<bool> changed, string? hint = null)
    {
        var box = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = value };
        box.Click += (_, _) => changed(box.IsChecked == true);
        var row = Row(label, box, hint);
        // Клик по всей строке тоже переключает.
        row.Background = Brushes.Transparent;
        row.Cursor = Cursors.Hand;
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, box)) return;
            box.IsChecked = box.IsChecked != true;
            changed(box.IsChecked == true);
        };
        return row;
    }

    private static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        for (var x = d; x != null; x = x is Visual ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x == parent) return true;
        return false;
    }

    private FrameworkElement SliderRow(string label, double min, double max, double step, double value,
        Func<double, string> format, Action<double> changed, string? hint = null)
    {
        var slider = new Slider
        {
            Style = (Style)FindResource("FlatSlider"), Minimum = min, Maximum = max, Width = 170,
            Value = Math.Clamp(value, min, max), IsSnapToTickEnabled = true, TickFrequency = step,
        };
        var text = new TextBlock
        {
            Foreground = Dim, FontSize = 12, Width = 100, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Text = format(slider.Value),
        };
        slider.ValueChanged += (_, e) =>
        {
            text.Text = format(e.NewValue);
            changed(e.NewValue);
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(slider);
        panel.Children.Add(text);
        return Row(label, panel, hint);
    }

    private FrameworkElement ChoiceRow(string label, IEnumerable<(string Value, string Text)> options, string current,
        Action<string> changed, string? hint = null)
    {
        var group = Guid.NewGuid().ToString();
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (value, text) in options)
        {
            var button = new RadioButton
            {
                Style = (Style)FindResource("Segment"), Content = text, GroupName = group, IsChecked = value == current,
                Margin = new Thickness(1, 0, 1, 0),
            };
            button.Checked += (_, _) => changed(value);
            panel.Children.Add(button);
        }
        var host = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2), Child = panel,
        };
        return Row(label, host, hint);
    }

    /// <summary>Текстовое поле. Сохраняем по Enter и при уходе из поля, а не на каждую букву.</summary>
    private TextBox Field(string value, string placeholder, Action<string> changed)
    {
        var box = new TextBox { Style = (Style)FindResource("Field"), Width = 230, Text = value };
        AttachPlaceholder(box, placeholder);
        var last = value;
        box.Tag = (Action)Commit;
        void Commit()
        {
            var v = box.Text.Trim();
            if (v == last) return;
            last = v;
            changed(v);
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit();
            Keyboard.ClearFocus();
        };
        return box;
    }

    private FrameworkElement TextRow(string label, string value, string placeholder, Action<string> changed, string? hint = null) =>
        Row(label, Field(value, placeholder, changed), hint);

    private FrameworkElement FolderRow(string label, string value, string placeholder, Action<string> changed, string? hint = null)
    {
        var box = Field(value, placeholder, changed);
        var pick = new Button { Style = (Style)FindResource("Small"), Content = "Выбрать…", Margin = new Thickness(8, 0, 0, 0) };
        pick.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = label };
            if (System.IO.Directory.Exists(box.Text)) dialog.InitialDirectory = box.Text;
            if (dialog.ShowDialog(this) != true) return;
            box.Text = dialog.FolderName;
            ((Action)box.Tag)();
        };
        box.Width = 200;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
        panel.Children.Add(pick);
        return Row(label, panel, hint);
    }

    /// <summary>Серый текст-подсказка в пустом поле.</summary>
    private static void AttachPlaceholder(TextBox box, string placeholder)
    {
        box.Loaded += (_, _) =>
        {
            if (box.Template.FindName("PART_ContentHost", box) is not ScrollViewer host || host.Parent is not Border border) return;
            var grid = new Grid();
            border.Child = grid;
            var hint = new TextBlock
            {
                Text = placeholder, Foreground = Faint, FontSize = box.FontSize, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            grid.Children.Add(hint);
            grid.Children.Add(host);
            void Update() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            box.TextChanged += (_, _) => Update();
            Update();
        };
    }

    private Button IconButton(string glyph, string tip, bool enabled, Action click)
    {
        var button = new Button
        {
            Style = (Style)FindResource("Small"), Background = Brushes.Transparent, Padding = new Thickness(8, 0, 8, 0), ToolTip = tip,
            IsEnabled = enabled, Opacity = enabled ? 1 : 0.3,
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11 },
        };
        button.Click += (_, _) => click();
        return button;
    }

    // ---------- Запись сочетаний клавиш ----------

    private Button? _recording;
    private Action<string>? _recorded;
    private Action? _recordingDone;

    private FrameworkElement HotkeyRow(string label, Func<string> get, Action<string> set, Func<bool>? busy = null)
    {
        var button = new Button { Style = (Style)FindResource("Small"), MinWidth = 160 };
        var status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0),
            Text = "Занято другой программой — выберите другое",
        };
        void Refresh()
        {
            button.Content = Hotkeys.Display(get());
            button.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            status.Visibility = busy?.Invoke() == true ? Visibility.Visible : Visibility.Collapsed;
        }
        Refresh();
        button.Click += (_, _) =>
        {
            if (_recording == button) { StopRecording(); return; }
            StopRecording();
            _recording = button;
            _recorded = set;
            _recordingDone = Refresh;
            button.Content = "Нажмите сочетание…";
            button.Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
            AppSettings.SetRecordingHotkey(true);
        };
        var row = Row(label, button);
        ((StackPanel)row.Children[0]).Children.Add(status);
        return row;
    }

    private void StopRecording()
    {
        if (_recording == null) return;
        _recording = null;
        _recorded = null;
        AppSettings.SetRecordingHotkey(false);
        var done = _recordingDone;
        _recordingDone = null;
        done?.Invoke();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording == null)
        {
            if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox) { Close(); e.Handled = true; }
            return;
        }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var save = _recorded;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { StopRecording(); return; }
        string? combo = key == Key.Back && Keyboard.Modifiers == ModifierKeys.None ? "" : Hotkeys.FromKeyEvent(e);
        if (combo == null) return; // пока нажат только Ctrl, Alt или Shift
        // Сначала возвращаем глобальные сочетания, потом сохраняем — тогда новое сразу проверится на занятость.
        AppSettings.SetRecordingHotkey(false);
        save?.Invoke(combo);
        StopRecording();
    }

    // ---------- Окно ----------

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox && e.GetPosition(this).Y < 60)
            DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
