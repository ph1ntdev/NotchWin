using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace NotchWin.Services;

public sealed record BluetoothItem(string Name, bool Connected);

public sealed record WifiNetwork(string Ssid, int Bars, bool Secured, bool Connected, WiFiAvailableNetwork Raw);

/// <summary>Wi-Fi и Bluetooth: включение, список сетей, подключение.</summary>
internal sealed class RadioService
{
    private WiFiAdapter? _adapter;
    private bool _accessRequested;
    // Сразу после подключения Windows ещё пару секунд не отдаёт профиль сети — помним её сами.
    private string? _justConnected;
    private DateTime _justConnectedAt;

    public async Task<(bool? WifiOn, bool? BluetoothOn)> GetRadioStatesAsync()
    {
        try
        {
            await EnsureAccessAsync();
            bool? wifi = null, bt = null;
            foreach (var radio in await Radio.GetRadiosAsync())
            {
                if (radio.Kind == RadioKind.WiFi) wifi = radio.State == RadioState.On;
                if (radio.Kind == RadioKind.Bluetooth) bt = radio.State == RadioState.On;
            }
            return (wifi, bt);
        }
        catch { return (null, null); }
    }

    public async Task SetRadioAsync(RadioKind kind, bool on)
    {
        try
        {
            await EnsureAccessAsync();
            foreach (var radio in await Radio.GetRadiosAsync())
                if (radio.Kind == kind)
                    await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
        }
        catch { }
    }

    /// <summary>Имя текущего подключения и есть ли интернет.</summary>
    public static (string Name, bool Online, bool IsWifi) GetConnection()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile == null) return ("Нет подключения", false, true);
            var online = profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            var name = profile.IsWlanConnectionProfile
                ? profile.WlanConnectionProfileDetails.GetConnectedSsid()
                : profile.ProfileName;
            return (name, online, profile.IsWlanConnectionProfile);
        }
        catch { return ("Неизвестно", false, true); }
    }

    /// <summary>Сканирует сети. Возвращает null, если Windows не дала доступ (нужно разрешить геолокацию).</summary>
    public async Task<List<WifiNetwork>?> ScanAsync()
    {
        try
        {
            var adapter = await GetAdapterAsync();
            if (adapter == null) return null;
            await adapter.ScanAsync();
            // Сеть, к которой подключён именно этот адаптер (интернет может идти и по кабелю).
            string? connectedSsid = null;
            try
            {
                var profile = await adapter.NetworkAdapter.GetConnectedProfileAsync();
                if (profile?.IsWlanConnectionProfile == true)
                    connectedSsid = profile.WlanConnectionProfileDetails.GetConnectedSsid();
            }
            catch { }
            if (connectedSsid == null && _justConnected != null && (DateTime.Now - _justConnectedAt).TotalSeconds < 20)
                connectedSsid = _justConnected;
            return adapter.NetworkReport.AvailableNetworks
                .Where(n => !string.IsNullOrEmpty(n.Ssid))
                .GroupBy(n => n.Ssid)
                .Select(g => g.OrderByDescending(n => n.SignalBars).First())
                .OrderByDescending(n => n.Ssid == connectedSsid)
                .ThenByDescending(n => n.SignalBars)
                .Select(n => new WifiNetwork(n.Ssid, n.SignalBars,
                    n.SecuritySettings.NetworkAuthenticationType is not
                        (NetworkAuthenticationType.Open80211 or NetworkAuthenticationType.None),
                    n.Ssid == connectedSsid, n))
                .ToList();
        }
        catch { return null; }
    }

    /// <summary>Подключение. Без пароля сработает для открытых и уже сохранённых сетей.</summary>
    public async Task<bool> ConnectAsync(WifiNetwork network, string? password)
    {
        try
        {
            var adapter = await GetAdapterAsync();
            if (adapter == null) return false;
            var result = string.IsNullOrEmpty(password)
                ? await adapter.ConnectAsync(network.Raw, WiFiReconnectionKind.Automatic)
                : await adapter.ConnectAsync(network.Raw, WiFiReconnectionKind.Automatic,
                    new PasswordCredential { Password = password });
            var ok = result.ConnectionStatus == WiFiConnectionStatus.Success;
            if (ok)
            {
                _justConnected = network.Ssid;
                _justConnectedAt = DateTime.Now;
            }
            return ok;
        }
        catch { return false; }
    }

    public async Task DisconnectAsync()
    {
        var adapter = await GetAdapterAsync();
        _justConnected = null;
        adapter?.Disconnect();
    }

    /// <summary>Сопряжённые Bluetooth-устройства, подключённые первыми.</summary>
    public static async Task<List<BluetoothItem>> GetBluetoothDevicesAsync()
    {
        var result = new Dictionary<string, BluetoothItem>();
        try
        {
            foreach (var info in await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true)))
            {
                using var device = await BluetoothDevice.FromIdAsync(info.Id);
                var connected = device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
                result[info.Name] = new BluetoothItem(info.Name, connected);
            }
            foreach (var info in await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)))
            {
                if (result.TryGetValue(info.Name, out var existing) && existing.Connected) continue;
                using var device = await BluetoothLEDevice.FromIdAsync(info.Id);
                var connected = device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
                if (!result.ContainsKey(info.Name) || connected)
                    result[info.Name] = new BluetoothItem(info.Name, connected);
            }
        }
        catch { }
        return result.Values.OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
    }

    private async Task<WiFiAdapter?> GetAdapterAsync()
    {
        if (_adapter != null) return _adapter;
        if (await WiFiAdapter.RequestAccessAsync() != WiFiAccessStatus.Allowed) return null;
        var adapters = await WiFiAdapter.FindAllAdaptersAsync();
        _adapter = adapters.FirstOrDefault();
        return _adapter;
    }

    private async Task EnsureAccessAsync()
    {
        if (_accessRequested) return;
        _accessRequested = true;
        await Radio.RequestAccessAsync();
    }
}
