using System.IO;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace NotchWin.Services;

public sealed record AppNotification(string App, string Title, string Text, string? ImagePath);

/// <summary>
/// Уведомления Telegram и ВКонтакте. Обычные программы не могут подписаться на чужие уведомления
/// Windows, поэтому читаем базу центра уведомлений текущего пользователя (только чтение).
/// </summary>
internal sealed class NotificationWatcher
{
    private static readonly string Database = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Notifications", "wpndatabase.db");

    private static readonly string LogFile = Path.Combine(AppContext.BaseDirectory, "notifications.log");
    private static readonly string CopyFolder = Path.Combine(Path.GetTempPath(), "NotchWin-notifications");

    private long _lastId = -1;
    private string _stamp = "";

    private bool Changed()
    {
        // Время изменения у открытого Windows файла не обновляется, поэтому смотрим сами файлы:
        // длину и заголовок журнала (в нём меняется счётчик, когда журнал начинается заново).
        var stamp = string.Join("|", new[] { "", "-wal" }.Select(x =>
        {
            try
            {
                using var f = new FileStream(Database + x, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var header = new byte[32];
                var read = f.Read(header, 0, header.Length);
                return $"{f.Length}:{Convert.ToHexString(header, 0, read)}";
            }
            catch { return "-"; }
        }));
        if (stamp == _stamp && _lastId >= 0) return false;
        _stamp = stamp;
        return true;
    }
    private string? _lastError;

    public bool Available => File.Exists(Database);

    /// <summary>Новые уведомления с прошлого вызова. Первый вызов только запоминает, где мы.</summary>
    public List<AppNotification> Poll()
    {
        var result = new List<AppNotification>();
        if (!Available) return result;
        try
        {
            // Windows держит базу открытой, и свежие записи лежат в журнале -wal, которого прямое чтение
            // не видит (поэтому раньше база казалась пустой). Читаем копию базы вместе с журналом,
            // а копируем только когда файлы изменились.
            if (!Changed()) return result;
            var source = CopyDatabase();
            using var connection = new SqliteConnection($"Data Source={source};Mode=ReadOnly;Pooling=False");
            connection.Open();
            if (_lastId < 0)
            {
                using var max = connection.CreateCommand();
                max.CommandText = "SELECT IFNULL(MAX(Id), 0) FROM Notification";
                _lastId = Convert.ToInt64(max.ExecuteScalar());
                using var apps = connection.CreateCommand();
                apps.CommandText = "SELECT PrimaryId FROM NotificationHandler";
                using var appReader = apps.ExecuteReader();
                var names = new List<string>();
                while (appReader.Read()) if (!appReader.IsDBNull(0)) names.Add(appReader.GetString(0));
                Log($"старт, последний Id {_lastId}, приложения: " + string.Join(", ", names));
                return result;
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT n.Id, h.PrimaryId, n.Payload FROM Notification n " +
                "JOIN NotificationHandler h ON n.HandlerId = h.RecordId " +
                "WHERE n.Id > $last AND n.Type = 'toast' ORDER BY n.Id";
            command.Parameters.AddWithValue("$last", _lastId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                _lastId = Math.Max(_lastId, reader.GetInt64(0));
                var appId = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var payload = reader.IsDBNull(2) ? "" : ReadPayload(reader, 2);
                Log($"Id {reader.GetInt64(0)} от {appId}");
                if (Parse(appId, payload) is { } n)
                {
                    result.Add(n);
                    Log($"новое от {n.App}: {n.Title}");
                }
                else if (IsTelegram(appId)) Log("не разобрал: " + payload);
            }
            _lastError = null;
        }
        catch (Exception ex)
        {
            if (ex.Message != _lastError) Log("ошибка: " + ex.Message);
            _lastError = ex.Message;
            _stamp = "";
        }
        return result;
    }

    private static string CopyDatabase()
    {
        Directory.CreateDirectory(CopyFolder);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var from = Database + suffix;
            var to = Path.Combine(CopyFolder, "wpndatabase.db" + suffix);
            if (!File.Exists(from)) { File.Delete(to); continue; }
            using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = new FileStream(to, FileMode.Create, FileAccess.Write);
            input.CopyTo(output);
        }
        return Path.Combine(CopyFolder, "wpndatabase.db");
    }

    private static void Log(string text)
    {
        try { File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss} {text}\n"); } catch { }
    }

    private static string ReadPayload(SqliteDataReader reader, int column)
    {
        var value = reader.GetValue(column);
        return value is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : value?.ToString() ?? "";
    }

    // Официальный клиент и популярные форки (AyuGram, Kotatogram, 64Gram, Unigram).
    private static readonly string[] TelegramApps = { "telegram", "ayugram", "kotatogram", "64gram", "unigram" };

    private static bool IsTelegram(string appId) =>
        TelegramApps.Any(t => appId.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static AppNotification? Parse(string appId, string payload)
    {
        string? app = null;
        if (IsTelegram(appId)) app = "Telegram";
        else if (appId.Contains("discord", StringComparison.OrdinalIgnoreCase)) app = "Discord";
        else if (appId.Contains("VK", StringComparison.Ordinal) || payload.Contains("vk.com", StringComparison.OrdinalIgnoreCase)
                 || payload.Contains("ВКонтакте", StringComparison.OrdinalIgnoreCase)) app = "ВКонтакте";
        if (app == null) return null;

        try
        {
            var xml = XDocument.Parse(payload);
            var texts = xml.Descendants("text")
                .Where(t => (string?)t.Attribute("placement") != "attribution")
                .Select(t => t.Value.Trim()).Where(t => t.Length > 0).ToList();
            if (texts.Count == 0) return null;
            var image = xml.Descendants("image").Select(i => (string?)i.Attribute("src")).FirstOrDefault(s => !string.IsNullOrEmpty(s));
            if (image != null && image.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) image = new Uri(image).LocalPath;
            if (image != null && !File.Exists(image)) image = null;
            return new AppNotification(app, texts[0], string.Join(" ", texts.Skip(1)), image);
        }
        catch { return null; }
    }
}
