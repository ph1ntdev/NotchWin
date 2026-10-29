using System.Diagnostics;
using System.IO;

namespace NotchWin.Services;

/// <summary>
/// Быстрые заметки: в хранилище Obsidian или просто в папку на компьютере (Markdown-файлы,
/// открываются любым редактором). Где хранить — настройка «Заметки».
/// </summary>
internal static class NotesService
{
    /// <summary>Пишем в Obsidian: так выбрано в настройках, или «Авто» и Obsidian установлен.</summary>
    public static bool UsesObsidian
    {
        get
        {
            var storage = AppSettings.Current.NoteStorage;
            return storage == "obsidian" || storage != "folder" && ObsidianService.FindVault() != null;
        }
    }

    /// <summary>Папка для заметок без Obsidian: из настроек или «Документы\Заметки».</summary>
    public static string PlainFolder
    {
        get
        {
            var chosen = AppSettings.Current.NotesPath.Trim();
            return chosen.Length > 0 ? chosen
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Заметки");
        }
    }

    private static bool Daily => AppSettings.Current.NoteTarget == "daily";

    /// <summary>Строка под полем ввода: куда уйдёт заметка. null — Obsidian выбран, но не найден.</summary>
    public static string? Describe()
    {
        if (UsesObsidian)
        {
            var vault = ObsidianService.FindVault();
            if (vault == null) return null;
            var name = Path.GetFileName(vault.TrimEnd('\\'));
            return Daily
                ? $"В ежедневную заметку Obsidian «{name}» · Ctrl+Enter"
                : $"В папку «{Path.GetFileName(ObsidianService.InboxFolder(vault))}» хранилища «{name}» · Ctrl+Enter";
        }
        return Daily
            ? $"В файл за сегодня в папке «{Path.GetFileName(PlainFolder.TrimEnd('\\'))}» · Ctrl+Enter"
            : $"Отдельным файлом в папку «{Path.GetFileName(PlainFolder.TrimEnd('\\'))}» · Ctrl+Enter";
    }

    /// <summary>Сохраняет заметку. Возвращает полный путь файла или null, если Obsidian не найден.</summary>
    public static string? Save(string text)
    {
        if (UsesObsidian)
        {
            var vault = ObsidianService.FindVault();
            if (vault == null) return null;
            var relative = Daily ? ObsidianService.Append(vault, text) : ObsidianService.SaveToInbox(vault, text);
            return Path.Combine(vault, relative);
        }
        var folder = PlainFolder;
        if (!Daily) return WriteNote(folder, text);
        var file = Path.Combine(folder, $"{DateTime.Now:yyyy-MM-dd}.md");
        AppendEntry(file, text);
        return file;
    }

    /// <summary>Открывает заметку (или, если её ещё не было, хранилище/папку).</summary>
    public static void Open(string? file)
    {
        if (UsesObsidian)
        {
            var vault = ObsidianService.FindVault();
            if (vault == null) return;
            ObsidianService.Open(vault, file != null && file.StartsWith(vault, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(vault, file) : null);
            return;
        }
        try
        {
            if (file != null && File.Exists(file))
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            else
            {
                Directory.CreateDirectory(PlainFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{PlainFolder}\"") { UseShellExecute = true });
            }
        }
        catch { }
    }

    /// <summary>Дописывает строку «- 14:05 текст» в конец файла (продолжение — с отступом).</summary>
    public static void AppendEntry(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var lines = text.Trim().Replace("\r\n", "\n").Split('\n');
        var entry = $"- {DateTime.Now:HH:mm} {lines[0]}" +
                    string.Concat(lines.Skip(1).Select(l => "\n  " + l)) + "\n";
        var prefix = File.Exists(file) && new FileInfo(file).Length > 0 && !File.ReadAllText(file).EndsWith('\n') ? "\n" : "";
        File.AppendAllText(file, prefix + entry);
    }

    /// <summary>Отдельный файл в папке, имя — первая строка заметки. Возвращает полный путь.</summary>
    public static string WriteNote(string folder, string text)
    {
        Directory.CreateDirectory(folder);
        text = text.Trim().Replace("\r\n", "\n");
        var first = text.Split('\n')[0].TrimStart('#', ' ', '-');
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '#', '^', '[', ']', '|' }).ToHashSet();
        var name = new string(first.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
        if (name.Length > 60) name = name[..60].TrimEnd();
        if (name.Length == 0) name = $"Заметка {DateTime.Now:yyyy-MM-dd HH.mm}";

        var file = Path.Combine(folder, name + ".md");
        for (var i = 2; File.Exists(file); i++) file = Path.Combine(folder, $"{name} {i}.md");
        File.WriteAllText(file, text + "\n");
        return file;
    }
}
