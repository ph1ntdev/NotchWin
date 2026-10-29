using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Windows.Devices.Geolocation;

namespace NotchWin.Services;

public sealed record WeatherHour(string Time, double Temp, string Icon);
public sealed record WeatherDay(string Name, double Max, double Min, string Icon);

public sealed record WeatherReport(
    string City, double Temp, double Feels, string Text, string Icon,
    double Wind, int Humidity, IReadOnlyList<WeatherHour> Hours, IReadOnlyList<WeatherDay> Days, string Source);

/// <summary>
/// Погода из бесплатного Open-Meteo (без ключей и регистрации).
/// Место: геопозиция Windows, если её нет — примерно по IP, иначе город из настроек.
/// </summary>
internal static class WeatherService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly CultureInfo Ru = new("ru-RU");

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // MET Norway требует понятный User-Agent, иначе отвечает 403.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NotchWin/1.0 (personal desktop widget)");
        return http;
    }

    /// <summary>Последняя ошибка — показываем её в окне погоды, чтобы было понятно, что не так.</summary>
    public static string? LastError { get; private set; }

    public static async Task<WeatherReport?> GetAsync()
    {
        try
        {
            var settings = AppSettings.Current;
            (double, double, string)? place = null;
            if (settings.WeatherUseLocation)
            {
                // Геопозиция Windows может не ответить вовсе — ждём не больше 8 секунд.
                place = await WithTimeout(GetLocationAsync(), 8);
                place ??= await WithTimeout(GetIpLocationAsync(), 8);
            }
            if (string.IsNullOrWhiteSpace(settings.WeatherCity))
                place ??= await WithTimeout(GetIpLocationAsync(), 8); // город не указан — берём по подключению
            else place ??= await GeocodeAsync(settings.WeatherCity);
            if (place == null)
            {
                Fail(string.IsNullOrWhiteSpace(settings.WeatherCity)
                    ? "Не удалось определить место — укажите город в настройках"
                    : $"Не нашёл город «{settings.WeatherCity}»");
                return null;
            }
            var (lat, lon, city) = place.Value;
            // Некоторые провайдеры обрывают соединение с отдельными сервисами погоды,
            // поэтому пробуем по очереди несколько бесплатных источников без ключей.
            var sources = new (string Name, Func<Task<WeatherReport>> Load)[]
            {
                ("Open-Meteo", () => GetOpenMeteoAsync(lat, lon, city)),
                ("MET Norway", () => GetMetNoAsync(lat, lon, city)),
                ("wttr.in", () => GetWttrAsync(lat, lon, city)),
            };
            // Источник из настроек спрашиваем первым, остальные остаются запасными.
            var preferred = settings.WeatherSource;
            sources = sources.OrderBy(x => x.Name == preferred ? 0 : 1).ToArray();
            Exception? last = null;
            foreach (var (name, load) in sources)
            {
                try
                {
                    var report = await load();
                    LastError = null;
                    return report;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Fail($"{name} не ответил: {ex.GetBaseException().Message}");
                }
            }
            throw last!;
        }
        catch (Exception ex)
        {
            Fail(ex is HttpRequestException or TaskCanceledException or IOException
                ? "Ни один сервер погоды не ответил. Похоже, их блокирует провайдер или сеть."
                : "Ошибка: " + ex.Message, ex);
            return null;
        }
    }

    private static void Fail(string message, Exception? ex = null)
    {
        LastError = message;
        try
        {
            // Журнал рядом с программой: по нему можно понять, что пошло не так.
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "weather.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{(ex != null ? Environment.NewLine + ex : "")}{Environment.NewLine}");
        }
        catch { }
    }

    private static async Task<T?> WithTimeout<T>(Task<T?> task, int seconds) where T : struct
    {
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        return done == task ? await task : null;
    }

    // ---------- Место ----------

    private static async Task<(double, double, string)?> GetLocationAsync()
    {
        try
        {
            if (await Geolocator.RequestAccessAsync() != GeolocationAccessStatus.Allowed) return null;
            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            var pos = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(6));
            var lat = pos.Coordinate.Point.Position.Latitude;
            var lon = pos.Coordinate.Point.Position.Longitude;
            return (lat, lon, await ReverseGeocodeAsync(lat, lon));
        }
        catch { return null; }
    }

    /// <summary>Примерное место по IP-адресу, если геопозиция Windows выключена.</summary>
    private static async Task<(double, double, string)?> GetIpLocationAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://ipwho.is/?lang=ru"));
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return null;
            var city = root.TryGetProperty("city", out var c) ? c.GetString() : null;
            return (root.GetProperty("latitude").GetDouble(), root.GetProperty("longitude").GetDouble(),
                string.IsNullOrWhiteSpace(city) ? "Здесь" : city);
        }
        catch { return null; }
    }

    private static async Task<string> ReverseGeocodeAsync(double lat, double lon)
    {
        try
        {
            var url = $"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={F(lat)}&longitude={F(lon)}&localityLanguage=ru";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var root = doc.RootElement;
            foreach (var key in new[] { "city", "locality", "principalSubdivision" })
                if (root.TryGetProperty(key, out var v) && v.GetString() is { Length: > 0 } s) return s;
        }
        catch { }
        return "Здесь";
    }

    private static async Task<(double, double, string)?> GeocodeAsync(string city)
    {
        try { return await GeocodeOpenMeteoAsync(city); }
        catch { return await GeocodeNominatimAsync(city); }
    }

    /// <summary>Поиск города через OpenStreetMap — запасной вариант.</summary>
    private static async Task<(double, double, string)?> GeocodeNominatimAsync(string city)
    {
        var url = $"https://nominatim.openstreetmap.org/search?format=json&limit=1&accept-language=ru&q={Uri.EscapeDataString(city.Trim())}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (doc.RootElement.GetArrayLength() == 0) return null;
        var r = doc.RootElement[0];
        var name = r.TryGetProperty("name", out var n) ? n.GetString() : null;
        return (double.Parse(r.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture),
            double.Parse(r.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(name) ? city : name);
    }

    private static async Task<(double, double, string)?> GeocodeOpenMeteoAsync(string city)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city.Trim())}&count=1&language=ru&format=json";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var r = results[0];
        return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(),
            r.TryGetProperty("name", out var n) ? n.GetString() ?? city : city);
    }

    // ---------- Open-Meteo ----------

    private static async Task<WeatherReport> GetOpenMeteoAsync(double lat, double lon, string city)
    {
        var url = "https://api.open-meteo.com/v1/forecast" +
                  $"?latitude={F(lat)}&longitude={F(lon)}" +
                  "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,relative_humidity_2m,is_day" +
                  "&hourly=temperature_2m,weather_code,is_day" +
                  "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
                  "&timezone=auto&forecast_days=4&wind_speed_unit=ms";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var root = doc.RootElement;

        var cur = root.GetProperty("current");
        var code = cur.GetProperty("weather_code").GetInt32();
        var day = cur.GetProperty("is_day").GetInt32() == 1;
        var (text, icon) = Wmo(code, day);

        // Ближайшие 6 часов, начиная с текущего.
        var hours = new List<WeatherHour>();
        var hourly = root.GetProperty("hourly");
        var times = hourly.GetProperty("time");
        var nowHour = DateTime.Parse(cur.GetProperty("time").GetString()!, CultureInfo.InvariantCulture);
        nowHour = nowHour.Date.AddHours(nowHour.Hour);
        for (var i = 0; i < times.GetArrayLength() && hours.Count < 6; i++)
        {
            var t = DateTime.Parse(times[i].GetString()!, CultureInfo.InvariantCulture);
            if (t < nowHour) continue;
            var (_, hIcon) = Wmo(hourly.GetProperty("weather_code")[i].GetInt32(), hourly.GetProperty("is_day")[i].GetInt32() == 1);
            hours.Add(new WeatherHour(hours.Count == 0 ? "Сейчас" : t.ToString("HH:mm"),
                hourly.GetProperty("temperature_2m")[i].GetDouble(), hIcon));
        }

        var days = new List<WeatherDay>();
        var daily = root.GetProperty("daily");
        var dTimes = daily.GetProperty("time");
        for (var i = 0; i < dTimes.GetArrayLength(); i++)
        {
            var date = DateTime.Parse(dTimes[i].GetString()!, CultureInfo.InvariantCulture);
            days.Add(new WeatherDay(DayName(date, i), daily.GetProperty("temperature_2m_max")[i].GetDouble(),
                daily.GetProperty("temperature_2m_min")[i].GetDouble(), Wmo(daily.GetProperty("weather_code")[i].GetInt32(), true).Icon));
        }

        return new WeatherReport(city, cur.GetProperty("temperature_2m").GetDouble(),
            cur.GetProperty("apparent_temperature").GetDouble(), text, icon,
            cur.GetProperty("wind_speed_10m").GetDouble(), (int)cur.GetProperty("relative_humidity_2m").GetDouble(),
            hours, days, "Open-Meteo");
    }

    // ---------- MET Norway (api.met.no) ----------

    private static async Task<WeatherReport> GetMetNoAsync(double lat, double lon, string city)
    {
        var url = $"https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={lat.ToString("0.##", CultureInfo.InvariantCulture)}&lon={lon.ToString("0.##", CultureInfo.InvariantCulture)}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var series = doc.RootElement.GetProperty("properties").GetProperty("timeseries");

        var points = new List<(DateTime Time, double Temp, double Wind, double Humidity, string Symbol)>();
        foreach (var item in series.EnumerateArray())
        {
            var time = item.GetProperty("time").GetDateTime().ToLocalTime();
            var data = item.GetProperty("data");
            var details = data.GetProperty("instant").GetProperty("details");
            string symbol = "";
            foreach (var key in new[] { "next_1_hours", "next_6_hours", "next_12_hours" })
                if (data.TryGetProperty(key, out var next) && next.TryGetProperty("summary", out var sum))
                { symbol = sum.GetProperty("symbol_code").GetString() ?? ""; break; }
            points.Add((time, details.GetProperty("air_temperature").GetDouble(),
                details.TryGetProperty("wind_speed", out var w) ? w.GetDouble() : 0,
                details.TryGetProperty("relative_humidity", out var h) ? h.GetDouble() : 0, symbol));
        }
        if (points.Count == 0) throw new InvalidDataException("пустой прогноз");

        var now = points.First(p => p.Time >= DateTime.Now.AddMinutes(-60));
        var (text, icon) = MetNo(now.Symbol);
        var hours = points.Where(p => p.Time >= now.Time).Take(6)
            .Select((p, i) => new WeatherHour(i == 0 ? "Сейчас" : p.Time.ToString("HH:mm"), p.Temp, MetNo(p.Symbol).Icon)).ToList();
        var days = points.GroupBy(p => p.Time.Date).Take(4)
            .Select((g, i) => new WeatherDay(DayName(g.Key, i), g.Max(p => p.Temp), g.Min(p => p.Temp),
                MetNo(g.OrderBy(p => Math.Abs(p.Time.Hour - 13)).First().Symbol).Icon)).ToList();

        return new WeatherReport(city, now.Temp, FeelsLike(now.Temp, now.Wind), text, icon, now.Wind, (int)now.Humidity,
            hours, days, "MET Norway");
    }

    /// <summary>Ощущаемая температура с учётом ветра (упрощённая формула, как у метеослужб).</summary>
    private static double FeelsLike(double t, double windMs)
    {
        var v = windMs * 3.6;
        if (t > 10 || v < 5) return t;
        return 13.12 + 0.6215 * t - 11.37 * Math.Pow(v, 0.16) + 0.3965 * t * Math.Pow(v, 0.16);
    }

    private static (string Text, string Icon) MetNo(string symbol)
    {
        var night = symbol.EndsWith("_night") || symbol.EndsWith("_polartwilight");
        var core = symbol.Split('_')[0];
        if (core.Contains("thunder")) return ("Гроза", Storm);
        if (core.Contains("sleet")) return ("Мокрый снег", Snow);
        if (core.Contains("snow")) return (core.StartsWith("heavy") ? "Снегопад" : core.StartsWith("light") ? "Небольшой снег" : "Снег", Snow);
        if (core.Contains("rain")) return (core.StartsWith("heavy") ? "Ливень" : core.StartsWith("light") ? "Небольшой дождь" : "Дождь", Rain);
        return core switch
        {
            "clearsky" => ("Ясно", night ? Moon : Sun),
            "fair" => ("Преимущественно ясно", night ? Moon : Sun),
            "partlycloudy" => ("Переменная облачность", night ? Cloud : Partly),
            "cloudy" => ("Пасмурно", Cloud),
            "fog" => ("Туман", Fog),
            _ => ("—", Cloud),
        };
    }

    // ---------- wttr.in ----------

    private static async Task<WeatherReport> GetWttrAsync(double lat, double lon, string city)
    {
        var url = $"https://wttr.in/{F(lat)},{F(lon)}?format=j1&lang=ru";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var root = doc.RootElement;
        var cur = root.GetProperty("current_condition")[0];
        var code = int.Parse(cur.GetProperty("weatherCode").GetString()!, CultureInfo.InvariantCulture);
        var hourNow = DateTime.Now.Hour;
        var (text, icon) = Wwo(code, hourNow is >= 7 and < 20);
        if (cur.TryGetProperty("lang_ru", out var ru) && ru.GetArrayLength() > 0 && ru[0].GetProperty("value").GetString() is { Length: > 0 } desc)
            text = char.ToUpper(desc[0]) + desc[1..];

        double D(JsonElement e, string key) => double.Parse(e.GetProperty(key).GetString()!, CultureInfo.InvariantCulture);

        var hours = new List<WeatherHour>();
        var days = new List<WeatherDay>();
        var index = 0;
        foreach (var day in root.GetProperty("weather").EnumerateArray())
        {
            var date = DateTime.Parse(day.GetProperty("date").GetString()!, CultureInfo.InvariantCulture);
            var middayCode = 113;
            foreach (var h in day.GetProperty("hourly").EnumerateArray())
            {
                var hour = int.Parse(h.GetProperty("time").GetString()!, CultureInfo.InvariantCulture) / 100;
                var hCode = int.Parse(h.GetProperty("weatherCode").GetString()!, CultureInfo.InvariantCulture);
                if (hour == 12) middayCode = hCode;
                if (hours.Count >= 6 || (index == 0 && hour + 3 <= hourNow)) continue;
                hours.Add(new WeatherHour(hours.Count == 0 ? "Сейчас" : $"{hour:00}:00", D(h, "tempC"), Wwo(hCode, hour is >= 7 and < 20).Icon));
            }
            days.Add(new WeatherDay(DayName(date, index), D(day, "maxtempC"), D(day, "mintempC"), Wwo(middayCode, true).Icon));
            index++;
        }

        return new WeatherReport(city, D(cur, "temp_C"), D(cur, "FeelsLikeC"), text, icon,
            Math.Round(D(cur, "windspeedKmph") / 3.6, 1), (int)D(cur, "humidity"), hours, days, "wttr.in");
    }

    private static (string Text, string Icon) Wwo(int code, bool day) => code switch
    {
        113 => ("Ясно", day ? Sun : Moon),
        116 => ("Переменная облачность", day ? Partly : Cloud),
        119 or 122 => ("Пасмурно", Cloud),
        143 or 248 or 260 => ("Туман", Fog),
        200 or 386 or 389 or 392 or 395 => ("Гроза", Storm),
        179 or 182 or 185 or 227 or 230 or (>= 317 and <= 338) or (>= 368 and <= 377) => ("Снег", Snow),
        _ when code >= 176 => ("Дождь", Rain),
        _ => ("—", Cloud),
    };

    // ---------- Значки и подписи ----------
    // Значки — одноцветные символы шрифта Segoe UI Symbol, чтобы не выбиваться из стиля панели.

    private const string Sun = "☀", Moon = "☾", Partly = "⛅", Cloud = "☁",
        Rain = "☂", Snow = "❄", Storm = "⚡", Fog = "≈";

    private static (string Text, string Icon) Wmo(int code, bool day) => code switch
    {
        0 => ("Ясно", day ? Sun : Moon),
        1 => ("Преимущественно ясно", day ? Sun : Moon),
        2 => ("Переменная облачность", day ? Partly : Cloud),
        3 => ("Пасмурно", Cloud),
        45 or 48 => ("Туман", Fog),
        51 or 53 or 55 or 56 or 57 => ("Морось", Rain),
        61 or 80 => ("Небольшой дождь", Rain),
        63 or 81 => ("Дождь", Rain),
        65 or 82 => ("Ливень", Rain),
        66 or 67 => ("Ледяной дождь", Rain),
        71 or 85 => ("Небольшой снег", Snow),
        73 or 77 => ("Снег", Snow),
        75 or 86 => ("Снегопад", Snow),
        95 or 96 or 99 => ("Гроза", Storm),
        _ => ("—", Cloud),
    };

    private static string DayName(DateTime date, int index) => index switch
    {
        0 => "Сегодня",
        1 => "Завтра",
        _ => Ru.TextInfo.ToTitleCase(date.ToString("dddd", Ru)),
    };

    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    public static string Temp(double t)
    {
        if (AppSettings.Current.Fahrenheit) t = t * 9 / 5 + 32;
        var r = (int)Math.Round(t);
        return r > 0 ? $"+{r}°" : $"{r}°";
    }
}
