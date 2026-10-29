using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotchWin.Services;

public sealed record Translation(string Text, string From, string To);

/// <summary>
/// Перевод выделенного текста. Русский переводим на английский, всё остальное — на русский.
/// Бесплатный адрес Google Переводчика, запасной вариант — MyMemory.
/// </summary>
internal static class TranslateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    static TranslateService() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) NotchWin/1.0");

    public static async Task<Translation> TranslateAsync(string text, CancellationToken token)
    {
        try
        {
            var first = await GoogleAsync(text, "ru", token);
            return first.From == "ru" ? await GoogleAsync(text, "en", token) : first;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            return await MyMemoryAsync(text, token);
        }
    }

    private static async Task<Translation> GoogleAsync(string text, string to, CancellationToken token)
    {
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&dt=t&tl=" + to +
                  "&q=" + Uri.EscapeDataString(text);
        var json = JsonNode.Parse(await Http.GetStringAsync(url, token))!.AsArray();
        var sb = new StringBuilder();
        foreach (var part in json[0]!.AsArray())
            sb.Append((string?)part?[0]);
        var from = (string?)json[2] ?? "auto";
        return new Translation(sb.ToString().Trim(), from, to);
    }

    private static async Task<Translation> MyMemoryAsync(string text, CancellationToken token)
    {
        var russian = text.Any(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё');
        var (from, to) = russian ? ("ru", "en") : ("en", "ru");
        var url = $"https://api.mymemory.translated.net/get?langpair={from}|{to}&q=" +
                  Uri.EscapeDataString(text.Length > 480 ? text[..480] : text);
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, token));
        var result = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "";
        return new Translation(System.Net.WebUtility.HtmlDecode(result), from, to);
    }
}
