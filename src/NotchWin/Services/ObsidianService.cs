using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace NotchWin.Services;

/// <summary>
/// Быстрые заметки в Obsidian: дописываем строку в сегодняшнюю ежедневную заметку хранилища,
/// которое Obsidian открывал последним. Сам Obsidian для этого не нужно запускать.
/// </summary>
internal static class ObsidianService
{
    private static readonly string Config = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obsidian", "obsidian.json");

    /// <summary>Путь к хранилищу или null, если Obsidian не найден.</summary>
    public static string? FindVault()
    {
        // Хранилище, выбранное в настройках, важнее последнего открытого.
        var chosen = AppSettings.Current.ObsidianVault;
        if (!string.IsNullOrWhiteSpace(chosen) && Directory.Exists(chosen)) return chosen;
        try
        {
            if (!File.Exists(Config)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(Config));
            if (!doc.RootElement.TryGetProperty("vaults", out var vaults)) return null;
            string? best = null;
            long bestTs = -1;
            foreach (var v in vaults.EnumerateObject())
            {
                var path = v.Value.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (path == null || !Directory.Exists(path)) continue;
                var ts = v.Value.TryGetProperty("ts", out var t) ? t.GetInt64() : 0;
                if (v.Value.TryGetProperty("open", out var o) && o.ValueKind == JsonValueKind.True) ts = long.MaxValue;
                if (ts > bestTs) { bestTs = ts; best = path; }
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>Файл сегодняшней ежедневной заметки с учётом настроек плагина «Ежедневные заметки».</summary>
    public static string DailyNotePath(string vault)
    {
        var folder = "";
        var format = "YYYY-MM-DD";
        try
        {
            var settings = Path.Combine(vault, ".obsidian", "daily-notes.json");
            if (File.Exists(settings))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settings));
                if (doc.RootElement.TryGetProperty("folder", out var f)) folder = f.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("format", out var fm) && fm.GetString() is { Length: > 0 } s) format = s;
            }
        }
        catch { }
        var name = FormatMoment(format, DateTime.Now);
        return Path.Combine(vault, folder.Trim('/', '\\'), name + ".md");
    }

    /// <summary>Дописывает заметку. Возвращает путь файла внутри хранилища.</summary>
    public static string Append(string vault, string text)
    {
        var file = DailyNotePath(vault);
        NotesService.AppendEntry(file, text);
        return Path.GetRelativePath(vault, file);
    }

    /// <summary>
    /// Сохраняет заметку отдельным файлом в папку Inbox хранилища (если папка уже есть с другим
    /// именем, например «02 - Inbox», — в неё). Имя файла — первая строка. Возвращает путь внутри хранилища.
    /// </summary>
    public static string SaveToInbox(string vault, string text) =>
        Path.GetRelativePath(vault, NotesService.WriteNote(InboxFolder(vault), text));

    /// <summary>
    /// Папка для заметок: из настроек, а если не задана — папка со словом inbox в имени
    /// (например «02 - Inbox»; папка с номером/приставкой важнее голой «Inbox»).
    /// </summary>
    public static string InboxFolder(string vault)
    {
        var chosen = AppSettings.Current.NoteFolder.Trim().Trim('/', '\\');
        if (chosen.Length > 0) return Path.Combine(vault, chosen);
        return Directory.EnumerateDirectories(vault)
            .Where(d => Path.GetFileName(d).Contains("inbox", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => Path.GetFileName(d).Equals("inbox", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault()
            ?? Path.Combine(vault, "Inbox");
    }

    public static void Open(string vault, string? relativeFile)
    {
        var uri = $"obsidian://open?vault={Uri.EscapeDataString(Path.GetFileName(vault.TrimEnd('\\', '/')))}";
        if (relativeFile != null) uri += "&file=" + Uri.EscapeDataString(relativeFile.Replace('\\', '/'));
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>Самые ходовые токены формата дат moment.js, которым пользуется Obsidian.</summary>
    private static string FormatMoment(string format, DateTime date)
    {
        var ru = new System.Globalization.CultureInfo("ru-RU");
        var map = new (string Token, Func<string> Value)[]
        {
            ("YYYY", () => date.Year.ToString("0000")),
            ("YY", () => (date.Year % 100).ToString("00")),
            ("MMMM", () => ru.DateTimeFormat.GetMonthName(date.Month)),
            ("MMM", () => ru.DateTimeFormat.GetAbbreviatedMonthName(date.Month)),
            ("MM", () => date.Month.ToString("00")),
            ("DD", () => date.Day.ToString("00")),
            ("dddd", () => ru.DateTimeFormat.GetDayName(date.DayOfWeek)),
            ("ddd", () => ru.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek)),
            ("M", () => date.Month.ToString()),
            ("D", () => date.Day.ToString()),
        };
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < format.Length;)
        {
            if (format[i] == '[')
            {
                var end = format.IndexOf(']', i);
                if (end > i) { sb.Append(format, i + 1, end - i - 1); i = end + 1; continue; }
            }
            var matched = false;
            foreach (var (token, value) in map)
            {
                if (string.CompareOrdinal(format, i, token, 0, token.Length) != 0) continue;
                sb.Append(value());
                i += token.Length;
                matched = true;
                break;
            }
            if (!matched) sb.Append(format[i++]);
        }
        return sb.ToString();
    }
}
