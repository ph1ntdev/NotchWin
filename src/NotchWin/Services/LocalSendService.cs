using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotchWin.Services;

public sealed record LocalSendDevice(string Alias, string DeviceModel, string DeviceType, string Ip, int Port, string Protocol);

/// <summary>
/// Отправка файлов на телефоны и компьютеры с LocalSend (замена AirDrop).
/// Говорим по открытому протоколу LocalSend v2: ищем устройства в своей сети и отдаём им файлы.
/// Получатель подтверждает приём у себя, как обычно в LocalSend.
/// </summary>
internal static class LocalSendService
{
    private const int DefaultPort = 53317;
    // Новые версии LocalSend (в логе iPhone отвечал TLS-ошибкой 116 «certificate_required») требуют
    // сертификат и от отправителя. Делаем свой самоподписанный один раз и храним рядом с настройками.
    // Отпечаток в LocalSend — SHA-256 сертификата строчными шестнадцатеричными цифрами.
    private static readonly X509Certificate2 Certificate = LoadCertificate();
    private static readonly string Fingerprint = Convert.ToHexString(SHA256.HashData(Certificate.RawData)).ToLowerInvariant();

    private static X509Certificate2 LoadCertificate()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotchWin", "localsend.pfx");
        const X509KeyStorageFlags flags = X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable;
        try
        {
            if (File.Exists(file)) return new X509Certificate2(File.ReadAllBytes(file), (string?)null, flags);
        }
        catch { /* испорченный файл — сделаем новый */ }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=LocalSend User", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        // Ключ, созданный в памяти, Windows (Schannel) использовать не умеет — пропускаем через PFX.
        var pfx = created.Export(X509ContentType.Pfx);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, pfx);
        }
        catch { }
        return new X509Certificate2(pfx, (string?)null, flags);
    }

    // Устройства LocalSend используют самоподписанные сертификаты, это нормально для локальной сети.
    // Windows (Schannel) и LocalSend на телефоне не всегда договариваются о шифровании с первого раза
    // (у iPhone в логе было «непредвиденное сообщение»). Поэтому пробуем разные версии TLS
    // и запоминаем для каждого устройства ту, что сработала.
    private static readonly SslProtocols[] TlsModes = { SslProtocols.None, SslProtocols.Tls12, SslProtocols.Tls13 };
    private static readonly HttpClient[] Scan = TlsModes.Select(m => CreateClient(TimeSpan.FromSeconds(6), m)).ToArray();
    private static readonly HttpClient[] Transfer = TlsModes.Select(m => CreateClient(Timeout.InfiniteTimeSpan, m)).ToArray();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> TlsFor = new();

    /// <summary>Порядок попыток для устройства: сначала то, что сработало в прошлый раз.</summary>
    private static IEnumerable<int> TlsOrder(string ip) =>
        TlsFor.TryGetValue(ip, out var known) ? new[] { known }.Concat(Enumerable.Range(0, TlsModes.Length).Where(i => i != known))
            : Enumerable.Range(0, TlsModes.Length);

    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null; e = e.InnerException) parts.Add($"{e.GetType().Name}: {e.Message} (0x{e.HResult:X8})");
        return string.Join(" <- ", parts);
    }

    private static void Log(string text)
    {
        try { File.AppendAllText(LogFile, $"{DateTime.Now:HH:mm:ss} {text}\n"); } catch { }
    }

    private static HttpClient CreateClient(TimeSpan timeout, SslProtocols tls)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                EnabledSslProtocols = tls,
                ClientCertificates = new X509CertificateCollection { Certificate },
                LocalCertificateSelectionCallback = (_, _, _, _, _) => Certificate,
            },
            MaxConnectionsPerServer = 4,
            // Соединяемся строго через сетевую карту той же подсети, иначе VPN перехватывает и такие адреса.
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    if (IPAddress.TryParse(context.DnsEndPoint.Host, out var ip) && LocalFor(ip) is { } local)
                        socket.Bind(new IPEndPoint(local, 0));
                    await socket.ConnectAsync(context.DnsEndPoint, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    // Имя для других устройств: из настроек, иначе имя компьютера.
    private static string Alias => string.IsNullOrWhiteSpace(AppSettings.Current.LocalSendName)
        ? Environment.MachineName + " (челка)" : AppSettings.Current.LocalSendName.Trim();

    private static object Info => new
    {
        alias = Alias,
        version = "2.0",
        deviceModel = "Windows",
        deviceType = "desktop",
        fingerprint = Fingerprint,
        port = DefaultPort,
        protocol = "https",
        download = false,
    };

    private static readonly string LogFile = Path.Combine(AppContext.BaseDirectory, "localsend.log");

    /// <summary>
    /// Ищет устройства двумя способами сразу: объявление по мультикасту (так ищет сам LocalSend)
    /// и проверка порта 53317 у всех адресов своей подсети. Занимает 2–3 секунды.
    /// </summary>
    public static async Task<List<LocalSendDevice>> DiscoverAsync(CancellationToken token)
    {
        var log = new System.Text.StringBuilder($"{DateTime.Now:HH:mm:ss} поиск\n");
        var found = new Dictionary<string, LocalSendDevice>();
        var nets = LocalNetworks().ToList();
        var own = nets.Select(n => n.Address).ToHashSet();
        foreach (var n in nets) log.AppendLine($"  сеть {n.Address}/{n.Prefix} ({n.Name})");

        var multicast = ListenMulticastAsync(nets, token, log);

        // Сначала быстро стучимся в порт, и только тем, кто открыл, шлём HTTP-запрос.
        var targets = nets.SelectMany(n => Hosts(n.Address, n.Prefix)).Where(ip => !own.Contains(ip)).Distinct().ToList();
        using var limit = new SemaphoreSlim(128);
        var probes = targets.Select(async ip =>
        {
            await limit.WaitAsync(token);
            bool open;
            try { open = await PortOpenAsync(ip, token); }
            finally { limit.Release(); }
            if (!open) return;
            var device = await TryRegisterAsync(ip.ToString(), "https", token, log)
                         ?? await TryRegisterAsync(ip.ToString(), "http", token, log);
            if (device != null) lock (found) found[device.Ip] = device;
        });
        try { await Task.WhenAll(probes); } catch (OperationCanceledException) { }

        foreach (var device in await multicast)
            lock (found) found.TryAdd(device.Ip, device);

        log.AppendLine($"  найдено: {string.Join(", ", found.Values.Select(d => $"{d.Alias} {d.Ip}"))}");
        try { File.AppendAllText(LogFile, log.ToString()); } catch { }
        return found.Values.OrderBy(d => d.Alias).ToList();
    }

    private static async Task<bool> PortOpenAsync(IPAddress ip, CancellationToken token)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        if (LocalFor(ip) is { } local) client.Client.Bind(new IPEndPoint(local, 0));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(900);
        try { await client.ConnectAsync(ip, DefaultPort, cts.Token); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Шлём объявление в группу 224.0.0.167:53317. Устройства, которые не смогли ответить по HTTP,
    /// отвечают в ту же группу; заодно ловим объявления тех, кто сам сейчас ищет.
    /// </summary>
    private static async Task<List<LocalSendDevice>> ListenMulticastAsync(List<LocalNet> nets, CancellationToken token,
        System.Text.StringBuilder log)
    {
        var result = new List<LocalSendDevice>();
        var group = IPAddress.Parse("224.0.0.167");
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, DefaultPort));
            foreach (var n in nets)
                try { udp.JoinMulticastGroup(group, n.Address); } catch { }

            var announce = JsonSerializer.SerializeToUtf8Bytes(new
            {
                alias = Alias, version = "2.0", deviceModel = "Windows", deviceType = "desktop",
                fingerprint = Fingerprint, port = DefaultPort, protocol = "https", download = false,
                announce = true, announcement = true,
            });
            foreach (var n in nets)
            {
                try
                {
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, n.Address.GetAddressBytes());
                    await udp.SendAsync(announce, new IPEndPoint(group, DefaultPort));
                }
                catch { }
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(2500);
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try { packet = await udp.ReceiveAsync(cts.Token); }
                catch (OperationCanceledException) { break; }
                try
                {
                    var json = JsonNode.Parse(packet.Buffer);
                    if (json == null || (string?)json["fingerprint"] == Fingerprint) continue;
                    var ip = packet.RemoteEndPoint.Address.ToString();
                    var port = (int?)json["port"] ?? DefaultPort;
                    result.Add(new LocalSendDevice((string?)json["alias"] ?? ip, (string?)json["deviceModel"] ?? "",
                        (string?)json["deviceType"] ?? "", ip, port, (string?)json["protocol"] ?? "https"));
                    log.AppendLine($"  мультикаст от {ip}");
                }
                catch { }
            }
        }
        catch (Exception ex) { log.AppendLine("  мультикаст: " + ex.Message); }
        return result;
    }

    private static async Task<LocalSendDevice?> TryRegisterAsync(string ip, string protocol, CancellationToken token,
        System.Text.StringBuilder log)
    {
        foreach (var mode in protocol == "https" ? TlsOrder(ip).ToArray() : new[] { 0 })
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(4000);
                using var response = await Scan[mode].PostAsJsonAsync($"{protocol}://{ip}:{DefaultPort}/api/localsend/v2/register", Info, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    lock (log) log.AppendLine($"  {ip} {protocol}: {(int)response.StatusCode}");
                    return null;
                }
                if (protocol == "https") TlsFor[ip] = mode;
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token));
                if (json == null || (string?)json["fingerprint"] == Fingerprint) return null;
                return new LocalSendDevice((string?)json["alias"] ?? ip, (string?)json["deviceModel"] ?? "",
                    (string?)json["deviceType"] ?? "", ip, DefaultPort, protocol);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                lock (log) log.AppendLine($"  {ip} {protocol} {TlsModes[mode]}: {Describe(ex)}");
            }
        }
        return null;
    }

    private sealed record LocalNet(IPAddress Address, int Prefix, string Name);

    /// <summary>Свой адрес в той же подсети, что и ip (через него и надо соединяться).</summary>
    private static IPAddress? LocalFor(IPAddress ip)
    {
        foreach (var n in LocalNetworks())
        {
            var mask = uint.MaxValue << (32 - n.Prefix);
            if ((ToUInt(n.Address) & mask) == (ToUInt(ip) & mask)) return n.Address;
        }
        return null;
    }

    private static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static readonly string[] VirtualAdapters = { "tun", "tap", "vpn", "wintun", "wireguard", "hyper-v", "virtual", "vethernet", "wsl", "zerotier", "tailscale" };

    /// <summary>Настоящие локальные сети (Wi-Fi, кабель). VPN-туннели и виртуальные адаптеры пропускаем.</summary>
    private static IEnumerable<LocalNet> LocalNetworks() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet
                        or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT)
            .Where(n => !VirtualAdapters.Any(v => n.Name.Contains(v, StringComparison.OrdinalIgnoreCase)
                                                 || n.Description.Contains(v, StringComparison.OrdinalIgnoreCase)))
            .Where(n => n.GetIPProperties().GatewayAddresses.Count > 0)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.PrefixLength >= 16)
                .Select(a => new LocalNet(a.Address, a.PrefixLength, n.Name)));

    /// <summary>Все адреса подсети, но не больше /22 (1022 адреса) вокруг своего.</summary>
    private static IEnumerable<IPAddress> Hosts(IPAddress address, int prefix)
    {
        prefix = Math.Clamp(prefix, 22, 30);
        var b = address.GetAddressBytes();
        var value = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        var mask = uint.MaxValue << (32 - prefix);
        var network = value & mask;
        var count = ~mask;
        for (uint i = 1; i < count; i++)
        {
            var v = network + i;
            yield return new IPAddress(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        }
    }

    /// <summary>
    /// Отправляет файлы. progress получает (доля 0..1, подпись). Возвращает текст результата.
    /// </summary>
    public static async Task<(bool Ok, string Message)> SendAsync(LocalSendDevice device, IReadOnlyList<string> paths,
        Action<double, string> progress, CancellationToken token)
    {
        var files = paths.Where(File.Exists).Select(p => (Id: Guid.NewGuid().ToString("N"), Path: p, Size: new FileInfo(p).Length)).ToList();
        if (files.Count == 0) return (false, "Папки LocalSend не принимает, только файлы");

        var baseUrl = $"{device.Protocol}://{device.Ip}:{device.Port}/api/localsend/v2";
        var body = new
        {
            info = Info,
            files = files.ToDictionary(f => f.Id, f => new
            {
                id = f.Id,
                fileName = Path.GetFileName(f.Path),
                size = f.Size,
                fileType = MimeType(f.Path),
            }),
        };

        progress(0, $"Ждём подтверждения на «{device.Alias}»");
        // Подбираем версию TLS, с которой устройство отвечает (первый запрос ещё ничего не передаёт).
        HttpResponseMessage? prepareResponse = null;
        var client = Transfer[0];
        Exception? lastError = null;
        foreach (var mode in device.Protocol == "https" ? TlsOrder(device.Ip).ToArray() : new[] { 0 })
        {
            try
            {
                prepareResponse = await Transfer[mode].PostAsJsonAsync($"{baseUrl}/prepare-upload", body, token);
                client = Transfer[mode];
                TlsFor[device.Ip] = mode;
                break;
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                Log($"отправка на {device.Ip} {TlsModes[mode]}: {Describe(ex)}");
            }
        }
        if (prepareResponse == null) throw lastError ?? new HttpRequestException("Устройство не отвечает");
        using var prepare = prepareResponse;
        switch ((int)prepare.StatusCode)
        {
            case 204: return (true, "Получатель уже всё принял");
            case 403: return (false, $"«{device.Alias}» отклонил приём");
            case 409: return (false, $"«{device.Alias}» занят другой передачей");
            case 401: return (false, $"«{device.Alias}» требует PIN — отправьте из самого LocalSend");
        }
        if (!prepare.IsSuccessStatusCode) return (false, $"Ошибка {(int)prepare.StatusCode}");

        var answer = JsonNode.Parse(await prepare.Content.ReadAsStringAsync(token))!;
        var session = (string)answer["sessionId"]!;
        var tokens = answer["files"]!.AsObject();

        long total = files.Sum(f => f.Size), sent = 0;
        foreach (var f in files)
        {
            if (!tokens.TryGetPropertyValue(f.Id, out var fileToken) || fileToken == null) continue; // получатель отказался от файла
            var url = $"{baseUrl}/upload?sessionId={Uri.EscapeDataString(session)}&fileId={f.Id}&token={Uri.EscapeDataString((string)fileToken!)}";
            await using var stream = new ProgressStream(File.OpenRead(f.Path), read =>
            {
                sent += read;
                progress(total == 0 ? 1 : (double)sent / total, Path.GetFileName(f.Path));
            });
            using var content = new StreamContent(stream, 256 * 1024);
            content.Headers.ContentLength = f.Size;
            using var upload = await client.PostAsync(url, content, token);
            if (!upload.IsSuccessStatusCode) return (false, $"Передача прервалась ({(int)upload.StatusCode})");
        }
        return (true, files.Count == 1 ? $"Отправлено на «{device.Alias}»" : $"{files.Count} файлов отправлено на «{device.Alias}»");
    }

    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mp3" => "audio/mpeg",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".apk" => "application/vnd.android.package-archive",
        _ => "application/octet-stream",
    };

    /// <summary>Поток, который сообщает, сколько байт уже прочитано (для прогресса).</summary>
    private sealed class ProgressStream(Stream inner, Action<long> onRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            onRead(n);
            return n;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var n = await inner.ReadAsync(buffer, token);
            onRead(n);
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
