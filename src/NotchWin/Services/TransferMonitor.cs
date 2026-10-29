using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace NotchWin.Services;

public sealed record TransferState(string Kind, string Title, double? Progress, string Detail, bool Finished);

/// <summary>
/// Следит за загрузками браузеров (временные файлы в папке «Загрузки») и за окном копирования Проводника.
/// Вызывается раз в секунду из фонового потока.
/// </summary>
internal sealed class TransferMonitor
{
    private static readonly string[] PartialExtensions = { ".crdownload", ".part", ".partial", ".download" };
    private readonly string _downloads = GetDownloadsFolder();
    private readonly Dictionary<string, long> _lastSizes = new();
    private readonly Dictionary<string, DateTime> _lastGrowth = new();
    private HashSet<string> _previous = new();

    /// <summary>Текущая передача (загрузка или копирование) или null.</summary>
    public TransferState? Poll()
    {
        return PollCopy() ?? PollDownloads();
    }

    private TransferState? PollDownloads()
    {
        if (!Directory.Exists(_downloads)) return null;
        var current = new HashSet<string>();
        TransferState? state = null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_downloads))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!PartialExtensions.Contains(ext)) continue;
                current.Add(file);
                var size = new FileInfo(file).Length;
                _lastSizes.TryGetValue(file, out var prev);
                _lastSizes[file] = size;
                var speed = prev > 0 ? Math.Max(0, size - prev) : 0;
                if (size != prev) _lastGrowth[file] = DateTime.Now;
                // Брошенные недокачанные файлы не показываем: только те, что растут прямо сейчас.
                if (!_lastGrowth.TryGetValue(file, out var grew) || prev == 0 || (DateTime.Now - grew).TotalSeconds > 4)
                    continue;
                var name = Path.GetFileNameWithoutExtension(file);
                // Пока браузер не узнал имя файла, он называет его «Unconfirmed 123» / «Не подтверждено 123».
                if (name.StartsWith("Unconfirmed", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Не подтвержден", StringComparison.OrdinalIgnoreCase)) name = "Новый файл";
                state = new TransferState("download", name, null, $"{FormatSize(size)} · {FormatSize(speed)}/с", false);
            }

            // Временный файл пропал, а готовый появился: загрузка завершена.
            if (state == null)
            {
                foreach (var gone in _previous.Except(current))
                {
                    var final = Path.Combine(Path.GetDirectoryName(gone)!, Path.GetFileNameWithoutExtension(gone));
                    if (File.Exists(final) && _lastGrowth.ContainsKey(gone))
                        state = new TransferState("download", Path.GetFileName(final), 1, "Загружено", true);
                }
            }
        }
        catch { }

        foreach (var key in _lastSizes.Keys.Except(current).ToList()) { _lastSizes.Remove(key); _lastGrowth.Remove(key); }
        _previous = current;
        return state;
    }

    private bool _copyActive;

    private TransferState? PollCopy()
    {
        // Проводник держит это окно скрытым и после копирования, поэтому считаем только видимое.
        var hwnd = FindWindow("OperationStatusWindow", null);
        if (hwnd != IntPtr.Zero && !IsWindowVisible(hwnd)) hwnd = IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            if (!_copyActive) return null;
            _copyActive = false;
            return new TransferState("copy", "Копирование", 1, "Готово", true);
        }

        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var bar = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ProgressBar));
            double? progress = null;
            if (bar != null && bar.TryGetCurrentPattern(RangeValuePattern.Pattern, out var p))
            {
                var range = (RangeValuePattern)p;
                var max = range.Current.Maximum - range.Current.Minimum;
                if (max > 0) progress = (range.Current.Value - range.Current.Minimum) / max;
            }
            _copyActive = true;
            var title = root.Current.Name;
            return new TransferState("copy", string.IsNullOrWhiteSpace(title) ? "Копирование" : title, progress,
                progress is double v ? $"{Math.Round(v * 100)}%" : "", false);
        }
        catch { return null; }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} ГБ",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} МБ",
        >= 1L << 10 => $"{bytes / 1024.0:0} КБ",
        _ => $"{bytes} Б",
    };

    private static string GetDownloadsFolder()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path) == 0) return path;
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out string path);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string cls, string? title);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
}
