// DropSense — Services/IDeviceConnectionService.cs
// ══════════════════════════════════════════════════════════════════════════════
// ADD TO PROJECT: Step 2
// ══════════════════════════════════════════════════════════════════════════════
// Manages the bidirectional BT/Wi-Fi communication channel between the
// application and the DropSense embedded device.
//
// Design principle: minimise radio on-time and CPU activity on the device.
//   • Disconnect after each discrete operation (download, settings push) unless
//     actively monitoring (alert listening mode).
//   • Use short, low-overhead payloads.
//   • Report progress so the UI can reflect transfer state without polling.
//
// WHEN THIS FILE IS ADDED:
//   1. Uncomment the IDeviceConnectionService registration in MauiProgram.cs
//   2. Uncomment the _connectionService field/constructor arg in App.xaml.cs
//   3. Uncomment the _connectionService field/constructor arg in DashboardViewModel.cs
//   4. Uncomment RegisterRoutes() entry for ConnectionPage in AppShell.xaml.cs (Step 7)
//   5. Add DeviceSettings.cs to the project (Step 3 — but the model can be added now)

using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.Text;

namespace DropSense.Services;

// ─────────────────────────────────────────────────────────────────────────────
// Connection State Enum
// ─────────────────────────────────────────────────────────────────────────────

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Transferring,
    Error
}

// ─────────────────────────────────────────────────────────────────────────────
// Interface
// ─────────────────────────────────────────────────────────────────────────────

public interface IDeviceConnectionService
{
    ConnectionState State               { get; }
    string?         ConnectedDeviceName { get; }

    event EventHandler<ConnectionState> ConnectionStateChanged;

    /// <summary>Scans for available DropSense devices and returns their identifiers.</summary>
    Task<IEnumerable<IDevice>> DiscoverDevicesAsync(CancellationToken ct = default);

    /// <summary>Establishes a connection; disconnects automatically after transfer if <paramref name="stayConnected"/> is false.</summary>
    Task ConnectAsync(IDevice device, bool stayConnected = false, CancellationToken ct = default);

    /// <summary>Establishes a connection; Attempting to use previous deviceID, if available, for auto-reconnect. Executes the provided operation while connected, then disconnects.</summary>
    Task ExecuteWithConnectionAsync(
        Func<IDevice, Task> operation,
        bool stayConnected = false,
        CancellationToken ct = default);

    /// <summary>Terminates the active connection gracefully.</summary>
    Task DisconnectAsync();

    // ── Step 3: Settings exchange ──────────────────────────────────────────────────
    // These methods are present in the interface now so the contract is complete;
    // they are called only from DeviceSettingsViewModel which is added at Step 3.
   //Task SendSettingsAsync(DeviceSettings settings, CancellationToken ct = default);
    //Task<DeviceSettings> RequestSettingsAsync(CancellationToken ct = default);

    // ── Step 4: Data download ──────────────────────────────────────────────────────
    /// <summary>
    /// Requests the device to transmit its stored CSV data.
    /// Returns the path of the downloaded file on the host filesystem.
    /// Disconnects after the transfer completes to conserve device power.
    /// </summary>
    Task<string> RequestDataDownloadAsync(IProgress<int>? progress = null, CancellationToken ct = default);

    // ── Step 6: Alert streaming ────────────────────────────────────────────────────
    /// <summary>
    /// Opens a persistent alert-listening channel.
    /// The service will raise ConnectionStateChanged and invoke the
    /// alertReceived callback whenever a new alert payload arrives.
    /// Call DisconnectAsync() to close the channel when no longer needed.
    /// </summary>
    // Uncomment at Step 6:
    // Task StartAlertListeningAsync(Action<byte[]> alertReceived, CancellationToken ct = default);
}

// ─────────────────────────────────────────────────────────────────────────────
// Implementation
// ─────────────────────────────────────────────────────────────────────────────



public class DeviceConnectionService : IDeviceConnectionService
{
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public string?  ConnectedDeviceName { get; private set; }
    public Guid DROPSENSE_SERVICE_UUID { get; private set; }
    public Guid COMMAND_CHAR_UUID { get; private set; }
    public Guid DATA_CHAR_UUID { get; private set; }

    public event EventHandler<ConnectionState>? ConnectionStateChanged;

    private IDevice? _connectedDevice;
    private readonly IBluetoothLE _ble;
    private readonly IAdapter _adapter;
    private readonly ISettingsService _settings;
    private readonly IFileSessionService _fileSession;
    public DeviceConnectionService(ISettingsService settings, IFileSessionService fileSession)
    {
        _ble = CrossBluetoothLE.Current;
        _adapter = CrossBluetoothLE.Current.Adapter;
        _settings = settings;
        _fileSession = fileSession;

    }

    public async Task ExecuteWithConnectionAsync(
        Func<IDevice, Task> operation,
        bool stayConnected = false,
        CancellationToken ct = default)
    {
        try
        {
            var device = await EnsureConnectedAsync(ct);

            if (device == null)
                throw new Exception("No device available.");

            await operation(device);

            if (!stayConnected)
                await DisconnectAsync();
        }
        catch
        {
            await DisconnectAsync();
            throw;
        }
    }

    // Overload for operations that return a result
    public async Task<T> ExecuteWithConnectionAsync<T>(
    Func<IDevice, Task<T>> operation,
    bool stayConnected = false,
    CancellationToken ct = default)
    {
        try
        {
            var device = await EnsureConnectedAsync(ct);

            if (device == null)
                throw new Exception("No device available.");

            var result = await operation(device);

            if (!stayConnected)
                await DisconnectAsync();

            return result;
        }
        catch
        {
            await DisconnectAsync();
            throw;
        }
    }

    private async Task<IDevice?> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_ble.State != BluetoothState.On)
        {
            SetState(ConnectionState.Disconnected);
            throw new InvalidOperationException("Bluetooth is not enabled on this device.");
        }
        if (_connectedDevice != null)
            return _connectedDevice;

        // 1️⃣ Try stored device first
        var savedId = _settings.LastConnectedDeviceId;

        if (!string.IsNullOrWhiteSpace(savedId))
        {
            try
            {
                var guid = Guid.Parse(savedId);

                var parameters = new ConnectParameters(false, true);

                var device = await _adapter.ConnectToKnownDeviceAsync(guid, parameters, ct);

                if (device != null)
                {
                    _connectedDevice = device;
                    SetState(ConnectionState.Connected);

                    return device;
                }
            }
            catch
            {
                // fallback to scan
            }
        }

        // Fallback: scan
        var found = await DiscoverFirstMatchingDeviceAsync(ct);

        if (found == null)
            return null;

        await _adapter.ConnectToDeviceAsync(found, cancellationToken: ct);

        _connectedDevice = found;

        // Persist for next time
        _settings.LastConnectedDeviceId = found.Id.ToString();
        _settings.LastConnectedDeviceName = found.Name;

        SetState(ConnectionState.Connected);

        return found;
    }

    private async Task<IDevice?> DiscoverFirstMatchingDeviceAsync(CancellationToken ct)
    {
        IDevice? match = null;

        void Handler(object? s, DeviceEventArgs e)
        {
            if (e.Device?.Name?.Contains("DropSense", StringComparison.OrdinalIgnoreCase) == true)
            {
                match = e.Device;
            }
        }

        _adapter.DeviceDiscovered += Handler;

        try
        {
            await _adapter.StartScanningForDevicesAsync(cancellationToken: ct);
            await Task.Delay(3000, ct);
            await _adapter.StopScanningForDevicesAsync();
        }
        finally
        {
            _adapter.DeviceDiscovered -= Handler;
        }

        return match;
    }

    public async Task<IEnumerable<IDevice>> DiscoverDevicesAsync(CancellationToken ct = default)
    {
        SetState(ConnectionState.Connecting);
        var devices = new List<string>();

        var discovered = new List<IDevice>();


        void Handler(object? sender, DeviceEventArgs e)
        {
            if (e.Device?.Name != null)
                discovered.Add(e.Device);
        }

        _adapter.DeviceDiscovered += Handler;

        try
        {
            if (_ble.State != BluetoothState.On)
            {
                
                return Enumerable.Empty<IDevice>();
            }

            await _adapter.StartScanningForDevicesAsync(cancellationToken: ct);

            // Current scan window (10 seconds) Shorten when advertizing frequency is confirmed.
            await Task.Delay(TimeSpan.FromSeconds(10), ct);

            await _adapter.StopScanningForDevicesAsync();
        }
        finally
        {
            _adapter.DeviceDiscovered -= Handler;
        }

    // TEMP FILTER (DropSense devices only)
    const string FILTER = "DropSense";

    var filtered = discovered
        .Where(d => !string.IsNullOrWhiteSpace(d.Name) &&
                    d.Name.Contains(FILTER, StringComparison.OrdinalIgnoreCase))
        .Distinct()
        .ToList();

    SetState(ConnectionState.Disconnected);

    return filtered;
    }

    public async Task ConnectAsync(IDevice device, bool stayConnected = false, CancellationToken ct = default)
    {
        SetState(ConnectionState.Connecting);

        // Open BLE / socket connection to deviceId
        try
        {
            if (_ble.State != BluetoothState.On)
                throw new InvalidOperationException("Bluetooth is not enabled.");

            // Resolve device from known devices or system cache using its GUID/ID
            var parameters = new ConnectParameters(
            autoConnect: false,
            forceBleTransport: true
             );
            
            _connectedDevice = device;

            // Perform handshake / firmware version check Implement when Embedded Code supports.
            //var service = await device.GetServiceAsync(DROPSENSE_SERVICE_UUID);
            //var versionChar = await service.GetCharacteristicAsync(FIRMWARE_CHAR_UUID);
            //var versionBytes = await versionChar.ReadAsync();
            //string firmware = Encoding.UTF8.GetString(versionBytes);

            SetState(ConnectionState.Connected);
            ConnectedDeviceName = device.Name;

            // Auto Disconnect (Remove When Direct Connection Testing no longer needed.
            if (!stayConnected)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(20));

                    if (_connectedDevice != null)
                        await DisconnectAsync();
                });
            }
        }
        catch
        {
            // If anything fails, ensure state is reset
            SetState(ConnectionState.Disconnected);
            _connectedDevice = null;
            ConnectedDeviceName = null;

            throw;
        }

    }

    public async Task DisconnectAsync()
    {
        if (_connectedDevice != null)
        {
            await _adapter.DisconnectDeviceAsync(_connectedDevice);
            _connectedDevice = null;
        }

        ConnectedDeviceName = null;
        SetState(ConnectionState.Disconnected);
    }
    /*
    public async Task SendSettingsAsync(DeviceSettings settings, CancellationToken ct = default)
    {
        // TODO: Serialise settings to the agreed wire format (confirm with embedded team)
        // TODO: Transmit over the active connection
        // TODO: Await ACK from device
        // TODO: Disconnect after success if stayConnected was false
        throw new NotImplementedException();
    }

    public async Task<DeviceSettings> RequestSettingsAsync(CancellationToken ct = default)
    {
        // TODO: Send settings-request command
        // TODO: Receive and deserialise response into DeviceSettings
        // TODO: Disconnect after receipt if stayConnected was false
        throw new NotImplementedException();
    }
    */

    public async Task<string> RequestDataDownloadAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        SetState(ConnectionState.Transferring);
        return await ExecuteWithConnectionAsync<string>(async device =>
        {
            // ── 1. Resolve service + characteristic ─────────────────────
            var service = await device.GetServiceAsync(DROPSENSE_SERVICE_UUID);
            var commandChar = await service.GetCharacteristicAsync(COMMAND_CHAR_UUID);
            var dataChar = await service.GetCharacteristicAsync(DATA_CHAR_UUID);

            // ── 2. Prepare temp file ────────────────────────────────────
            var tempPath = Path.Combine(FileSystem.CacheDirectory, $"dropsense_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            using var stream = File.OpenWrite(tempPath);

            int totalBytes = 0;
            const int expectedBytes = 50_000; // TEMP placeholder (adjust later)

            // ── 3. Subscribe to incoming data ───────────────────────────
            void Handler(object? s, CharacteristicUpdatedEventArgs e)
            {
                var bytes = e.Characteristic.Value;

                if (bytes == null || bytes.Length == 0)
                    return;

                stream.Write(bytes, 0, bytes.Length);
                totalBytes += bytes.Length;

                // Report progress (rough estimate for now)
                int percent = Math.Min(100, (int)((double)totalBytes / expectedBytes * 100));
                progress?.Report(percent);
            }

            dataChar.ValueUpdated += Handler;

            try
            {
                await dataChar.StartUpdatesAsync();

                // ── 4. Send download request command ─────────────────────
                // Protocol placeholder — replace with your embedded command
                var command = Encoding.UTF8.GetBytes("DOWNLOAD_CSV");
                await commandChar.WriteAsync(command);

                // ── 5. Wait for transfer completion ──────────────────────
                // TEMP: wait fixed duration (replace with EOF signal later)
                await Task.Delay(3000, ct);

                progress?.Report(100);
            }
            finally
            {
                await dataChar.StopUpdatesAsync();
                dataChar.ValueUpdated -= Handler;
            }

            // ── 6. Move to Documents/DropSense ──────────────────────────
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var targetDir = Path.Combine(docs, "DropSense");

            Directory.CreateDirectory(targetDir);

            var finalPath = Path.Combine(targetDir, Path.GetFileName(tempPath));

            File.Move(tempPath, finalPath, overwrite: true);
            _fileSession.SetActiveFile(finalPath);

            // ── 7. Update connection state before disconnect ────────────
            SetState(ConnectionState.Connected);

            // ── 8. Return file path ─────────────────────────────────────
            return finalPath;

        }, stayConnected: false, ct);
    }

    // Step 6 — uncomment when alert listening is needed:
    // public async Task StartAlertListeningAsync(Action<byte[]> alertReceived, CancellationToken ct = default)
    // {
    //     // TODO: SetState(ConnectionState.Connected) with stayConnected = true
    //     // TODO: Open a persistent notification/characteristic subscription (BLE notify)
    //     //       or a long-poll socket that the device pushes alert payloads on
    //     // TODO: On each incoming payload, invoke alertReceived(payload)
    //     // TODO: The device should be in low-power idle between alert events
    //     throw new NotImplementedException();
    // }

    private void SetState(ConnectionState newState)
    {
        State = newState;
        ConnectionStateChanged?.Invoke(this, newState);
    }
}
