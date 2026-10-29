using System.Data;
using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace NotchWin.Services;

internal enum ResultKind { App, File, Folder, Calculator, Web }

internal sealed record SearchResult(ResultKind Kind, string Title, string Subtitle, string IconKey, Action Open, Action? Reveal = null);

/// <summary>Список установленных приложений (как в меню «Пуск»), включая приложения из Microsoft Store.</summary>
internal static class AppIndex
{
    private sealed record App(string Name, string Path, string Lower, string Initials);

    private static List<App> _apps = new();
    private static DateTime _loadedAt = DateTime.MinValue;
    private static bool _loading;

    /// <summary>Загружает список в фоне. Повторно — не чаще раза в 5 минут (вдруг что-то установили).</summary>
    public static void Warmup()
    {
        if (_loading || (DateTime.Now - _loadedAt).TotalMinutes < 5) return;
        _loading = true;
        var thread = new Thread(() =>
        {
            try { _apps = Load(); _loadedAt = DateTime.Now; }
            catch { }
            finally { _loading = false; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); // Shell.Application работает только в STA
        thread.Start();
    }

    private static List<App> Load()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic folder = shell.NameSpace("shell:AppsFolder");
        var list = new List<App>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in folder.Items())
        {
            string name = item.Name;
            string path = item.Path;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path) || !seen.Add(name + "|" + path)) continue;
            // Ссылки на сайты и справку из меню «Пуск» в поиске приложений не нужны.
            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            var initials = string.Concat(name.Split(' ', '-', '.').Where(w => w.Length > 0).Select(w => char.ToLowerInvariant(w[0])));
            list.Add(new App(name, path, name.ToLowerInvariant(), initials));
        }
        return list;
    }

    public static IEnumerable<SearchResult> Search(string query, int max)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return Array.Empty<SearchResult>();
        // Набрали не в той раскладке («сркщьу» вместо «chrome») — тоже найдём.
        var alt = KeyboardLayout.Swap(q);

        return _apps
            .Select(a => (App: a, Score: Math.Max(Score(a, q), Score(a, alt) - 5)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.App.Name.Length)
            .Take(max)
            .Select(x => new SearchResult(ResultKind.App, x.App.Name, "Приложение", "shell:AppsFolder\\" + x.App.Path,
                () => Launch(x.App.Path)));
    }

    private static int Score(App a, string q)
    {
        if (q.Length == 0) return 0;
        var penalty = a.Lower.Contains("uninstall") || a.Lower.Contains("удален") ? 30 : 0;
        if (a.Lower == q) return 120 - penalty;
        if (a.Lower.StartsWith(q)) return 100 - penalty;
        if (a.Lower.Contains(" " + q) || a.Lower.Contains("-" + q)) return 80 - penalty;
        if (q.Length >= 2 && a.Initials.StartsWith(q)) return 70 - penalty;
        if (q.Length >= 3 && a.Lower.Contains(q)) return 50 - penalty;
        return 0;
    }

    private static void Launch(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + path) { UseShellExecute = true }); }
        catch { }
    }
}

/// <summary>Поиск файлов через индекс Windows Search: тот же, что у поиска в проводнике, поэтому быстрый.</summary>
internal static class FileSearch
{
    private static readonly string Scope = "file:" + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/');

    public static List<SearchResult> Search(string query, int max, CancellationToken token)
    {
        var results = new List<SearchResult>();
        var q = Regex.Replace(query.Trim(), "['%_\\[\\]\"]", "");
        if (q.Length < 2) return results;

        var sql = $"SELECT TOP {max} System.ItemPathDisplay, System.ItemNameDisplay, System.ItemType " +
                  $"FROM SystemIndex WHERE SCOPE='{Scope}' AND System.FileName LIKE '%{q}%' " +
                  "ORDER BY System.DateModified DESC";
        try
        {
            using var connection = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            connection.Open();
            using var command = new OleDbCommand(sql, connection);
            using var reader = command.ExecuteReader();
            while (reader.Read() && !token.IsCancellationRequested)
            {
                var path = reader[0] as string;
                var name = reader[1] as string ?? Path.GetFileName(path);
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name)) continue;
                var folder = string.Equals(reader[2] as string, "Directory", StringComparison.OrdinalIgnoreCase) || Directory.Exists(path);
                var parent = Path.GetDirectoryName(path) ?? "";
                results.Add(new SearchResult(folder ? ResultKind.Folder : ResultKind.File, name, ShortenPath(parent), path,
                    () => Open(path), () => Reveal(path)));
            }
        }
        catch { /* служба поиска выключена — просто без файлов */ }
        return results;
    }

    private static string ShortenPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { }
    }

    private static void Reveal(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch { }
    }
}

/// <summary>Калькулятор прямо в строке поиска: например, «2+2*3» или «(15+5)/4».</summary>
internal static class Calculator
{
    private static readonly Regex Expression = new(@"^[\d\s+\-*/().,%]+$");

    public static SearchResult? Try(string query)
    {
        var q = query.Trim();
        if (q.Length < 3 || !Expression.IsMatch(q) || !q.Any(char.IsDigit) || !q.Skip(1).Any(c => "+-*/%".Contains(c))) return null;
        try
        {
            var value = Convert.ToDouble(new DataTable().Compute(q.Replace(',', '.'), null), CultureInfo.InvariantCulture);
            if (double.IsNaN(value) || double.IsInfinity(value)) return null;
            var text = value.ToString("0.##########", CultureInfo.CurrentCulture);
            return new SearchResult(ResultKind.Calculator, "= " + text, "Enter — скопировать результат", "",
                () => { try { System.Windows.Clipboard.SetText(text); } catch { } });
        }
        catch { return null; }
    }
}

internal static class WebSearch
{
    public static SearchResult For(string query) =>
        new(ResultKind.Web, $"Искать «{query.Trim()}» в Яндексе", "Откроется в браузере", "", () =>
        {
            try { Process.Start(new ProcessStartInfo("https://yandex.ru/search/?text=" + Uri.EscapeDataString(query.Trim())) { UseShellExecute = true }); }
            catch { }
        });
}

/// <summary>Перевод текста, набранного не в той раскладке (йцукен ↔ qwerty).</summary>
internal static class KeyboardLayout
{
    private const string En = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
    private const string Ru = "йцукенгшщзхъфывапролджэячсмитьбюё";

    public static string Swap(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var e = En.IndexOf(chars[i]);
            if (e >= 0) { chars[i] = Ru[e]; continue; }
            var r = Ru.IndexOf(chars[i]);
            if (r >= 0) chars[i] = En[r];
        }
        return new string(chars);
    }
}
