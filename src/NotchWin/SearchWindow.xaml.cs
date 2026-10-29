using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NotchWin.Services;

namespace NotchWin;

public partial class SearchWindow : Window
{
    private static SearchWindow? _instance;

    private readonly DispatcherTimer _fileDelay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly List<SearchResult> _results = new();
    private readonly List<Border> _rows = new();
    private CancellationTokenSource? _fileSearch;
    private int _selected;
    private int _version;
    private bool _hiding;

    private static readonly Brush Selected = new SolidColorBrush(Color.FromArgb(0x55, 0x0A, 0x84, 0xFF));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));

    /// <summary>Alt+Пробел или лупа на панели: открыть поиск, а если он открыт — закрыть.</summary>
    public static void Toggle()
    {
        _instance ??= new SearchWindow();
        if (_instance.IsVisible && !_instance._hiding) _instance.HideAnimated();
        else _instance.ShowAnimated();
    }

    /// <summary>Заранее читает список приложений, чтобы первый поиск был мгновенным.</summary>
    public static void Warmup() => AppIndex.Warmup();

    public SearchWindow()
    {
        InitializeComponent();
        SmoothScroll.Attach(ResultsScroll, horizontal: false, factor: 0.5);
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // Не показываем окно в Alt+Tab.
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x80 /* WS_EX_TOOLWINDOW */);
        };
        Deactivated += (_, _) => HideAnimated();
        _fileDelay.Tick += (_, _) => { _fileDelay.Stop(); StartFileSearch(); };
    }

    private void ShowAnimated()
    {
        _hiding = false;
        AppIndex.Warmup();

        // По центру основного экрана, чуть выше середины, как в macOS.
        var area = Screens.WorkAreaDip();
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + area.Height * 0.18;

        QueryBox.Text = "";
        // Прозрачность ставим до показа, чтобы окно не мелькнуло на кадр целиком.
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Glass.Fade(hwnd, true, 160);
        Show();
        // Стекло включаем уже у показанного окна: до показа Windows его иногда не применяет.
        Glass.Apply(this, Card);
        Activate();
        QueryBox.Focus();
        Keyboard.Focus(QueryBox);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private void HideAnimated()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        _fileSearch?.Cancel();
        var hwnd = new WindowInteropHelper(this).Handle;
        Glass.Fade(hwnd, false, 120, () =>
        {
            if (!_hiding) return; // успели открыть снова
            Hide();
            _hiding = false;
            App.KeepNotchOnTop();
        });
    }

    // ---------- Поиск ----------

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        var query = QueryBox.Text;
        Placeholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _version++;
        _fileSearch?.Cancel();
        _fileDelay.Stop();

        _results.Clear();
        if (string.IsNullOrWhiteSpace(query))
        {
            ResultsArea.Visibility = Visibility.Collapsed;
            Render();
            return;
        }

        // Мгновенные результаты: калькулятор и приложения (они уже в памяти).
        if (Calculator.Try(query) is { } calc) _results.Add(calc);
        _results.AddRange(AppIndex.Search(query, 5));
        _results.Add(WebSearch.For(query));
        _selected = 0;
        ResultsArea.Visibility = Visibility.Visible;
        Render();

        // Файлы ищем чуть позже, когда пользователь перестанет печатать.
        _fileDelay.Start();
    }

    private void StartFileSearch()
    {
        var query = QueryBox.Text;
        if (query.Trim().Length < 2) return;
        var version = _version;
        var cts = _fileSearch = new CancellationTokenSource();
        Task.Run(() => FileSearch.Search(query, 6, cts.Token)).ContinueWith(t =>
        {
            if (version != _version || cts.IsCancellationRequested || t.Result.Count == 0) return;
            // Файлы встают перед поиском в интернете, выделение не сбрасываем.
            _results.InsertRange(_results.Count - 1, t.Result);
            Render();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Render()
    {
        ResultsPanel.Children.Clear();
        _rows.Clear();
        ResultKind? lastKind = null;
        for (var i = 0; i < _results.Count; i++)
        {
            var r = _results[i];
            var group = Group(r.Kind);
            if (lastKind == null || Group(lastKind.Value) != group)
                ResultsPanel.Children.Add(new TextBlock
                {
                    Text = group, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Dim,
                    Margin = new Thickness(10, lastKind == null ? 2 : 8, 0, 4),
                });
            lastKind = r.Kind;
            var row = BuildRow(r, i);
            _rows.Add(row);
            ResultsPanel.Children.Add(row);
        }
        Select(Math.Min(_selected, _results.Count - 1));
    }

    private static string Group(ResultKind kind) => kind switch
    {
        ResultKind.App => "Приложения",
        ResultKind.File or ResultKind.Folder => "Файлы и папки",
        ResultKind.Calculator => "Калькулятор",
        _ => "Интернет",
    };

    private Border BuildRow(SearchResult r, int index)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        FrameworkElement icon;
        if (r.IconKey.Length > 0)
        {
            var image = new Image { Width = 28, Height = 28 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            image.Source = ShellImage.Get(r.IconKey, img => image.Source = img);
            icon = image;
        }
        else
        {
            icon = new TextBlock
            {
                Text = r.Kind == ResultKind.Calculator ? "" : "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20,
                Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        }
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(2, 0, 0, 0);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = r.Title, Foreground = Brushes.White, FontSize = r.Kind == ResultKind.Calculator ? 18 : 14,
            FontWeight = r.Kind == ResultKind.Calculator ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock { Text = r.Subtitle, Foreground = Dim, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);

        var row = new Border
        {
            Height = 46, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 0, 10, 0),
            Background = Brushes.Transparent, Child = grid, Cursor = Cursors.Hand,
        };
        row.MouseEnter += (_, _) => Select(index);
        row.MouseLeftButtonUp += (_, _) => { Select(index); OpenSelected(false); };
        return row;
    }

    private void Select(int index)
    {
        if (_rows.Count == 0) { _selected = 0; return; }
        _selected = Math.Clamp(index, 0, _rows.Count - 1);
        for (var i = 0; i < _rows.Count; i++) _rows[i].Background = i == _selected ? Selected : Brushes.Transparent;
        _rows[_selected].BringIntoView();
    }

    private void OpenSelected(bool reveal)
    {
        if (_selected < 0 || _selected >= _results.Count) return;
        var r = _results[_selected];
        HideAnimated();
        if (reveal && r.Reveal != null) r.Reveal();
        else r.Open();
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (QueryBox.Text.Length > 0) QueryBox.Text = "";
                else HideAnimated();
                e.Handled = true;
                break;
            case Key.Down:
                Select(_selected + 1);
                e.Handled = true;
                break;
            case Key.Up:
                Select(_selected - 1);
                e.Handled = true;
                break;
            case Key.Enter:
                // Ctrl+Enter — показать файл в папке.
                OpenSelected(Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
                break;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
