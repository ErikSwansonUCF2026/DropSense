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
using Plugin.BLE.Windows;
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
    bool IsBluetoothOn { get; }

    event EventHandler<ConnectionState> ConnectionStateChanged;

    /// <summary>Establishes a connection; disconnects automatically after transfer if <paramref name="stayConnected"/> is false.</summary>
    Task ConnectAsync(IDevice device, bool stayConnected = false, CancellationToken ct = default);

    /// <summary>Establishes a connection; Attempting to use previous deviceID, if available, for auto-reconnect. Executes the provided operation while connected, then disconnects.</summary>
    Task ExecuteWithConnectionAsync(
        Func<IDevice, CancellationToken, Task> operation,
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
    Task<string> RequestDataDownloadAsync(IProgress<int>? progress = null, bool stayconnected = false, CancellationToken ct = default);

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
    private static readonly Guid DROPSENSE_SERVICE_UUID = Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly Guid COMMAND_CHAR_UUID = Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly Guid DATA_CHAR_UUID = Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");


    public event EventHandler<ConnectionState>? ConnectionStateChanged;

    private IDevice? _connectedDevice;
    private readonly IBluetoothLE _ble;
    private readonly IAdapter _adapter;
    private readonly ISettingsService _settings;
    private readonly IFileSessionService _fileSession;

    private readonly SemaphoreSlim _connectionLock = new(1, 1);


    public DeviceConnectionService(ISettingsService settings, IFileSessionService fileSession)
    {
        _ble = CrossBluetoothLE.Current;
        _adapter = CrossBluetoothLE.Current.Adapter;
        _settings = settings;
        _fileSession = fileSession;

        _ble.StateChanged += (s, e) =>
        {
            ConnectionStateChanged?.Invoke(this, State);
        };
    }

    public async Task ExecuteWithConnectionAsync(
    Func<IDevice, CancellationToken, Task> operation,
    bool stayConnected = false,
    CancellationToken ct = default)
{
    var device = await EnsureConnectedAsync(ct);

    if (device == null)
        throw new Exception("No device available.");

    try
    {
        await operation(device, ct);
    }
    finally
    {
        if (!stayConnected)
            await DisconnectAsync();
    }
}



    // Overload for operations that return a result
    public async Task<T> ExecuteWithConnectionAsync<T>(
    Func<IDevice, CancellationToken, Task<T>> operation,
    bool stayConnected = false,
    CancellationToken ct = default)
    {
        var device = await EnsureConnectedAsync(ct);

        if (device == null)
            throw new Exception("No device available.");

        try
        {
            return await operation(device, ct);
        }
        finally
        {
            if (!stayConnected)
                await DisconnectAsync();
        }
    }

    private async Task<IDevice> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_ble.State != BluetoothState.On)
        {
            SetState(ConnectionState.Disconnected);
            throw new InvalidOperationException("Bluetooth is not enabled.");
        }

        if (_connectedDevice != null && _connectedDevice.State == DeviceState.Connected)
            return _connectedDevice;

        _connectedDevice = null;
        // ─────────────────────────────────────────────
        // 1. TRY LAST CONNECTED DEVICE (FAST PATH)
        // ─────────────────────────────────────────────
        var lastId = _settings.LastConnectedDeviceId;

        if (!string.IsNullOrWhiteSpace(lastId) && Guid.TryParse(lastId, out var guid))
        {
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    var device = await _adapter.ConnectToKnownDeviceAsync(
                        guid,
                        new ConnectParameters(false, true),
                        ct);

                    if (device != null && device.State == DeviceState.Connected)
                    {
                        _connectedDevice = device;

                        _settings.LastConnectedDeviceName = device.Name;

                        SetState(ConnectionState.Connected);
                        return device;
                    }
                }
                catch
                {
                    // ignore and retry once
                }

                await Task.Delay(1000, ct);
            }
        }

        // ─────────────────────────────────────────────
        // 2. FALLBACK: SCAN FOR FIRST MATCH
        // ─────────────────────────────────────────────
        var found = await DiscoverFirstMatchingDeviceAsync(ct);

        if (found == null)
            throw new TimeoutException("No DropSense device found.");

        await _adapter.ConnectToDeviceAsync(found, cancellationToken: ct);

        if (found.State != DeviceState.Connected)
            throw new Exception("Device failed to enter connected state.");

        _connectedDevice = found;

        _settings.LastConnectedDeviceId = found.Id.ToString();
        _settings.LastConnectedDeviceName = found.Name;

        SetState(ConnectionState.Connected);

        return found;
    }

    private async Task<IDevice?> DiscoverFirstMatchingDeviceAsync(CancellationToken ct)
    {
        var results = await ScanAsync(
            d => !string.IsNullOrWhiteSpace(d.Name) &&
                 d.Name.Contains("DropSense", StringComparison.OrdinalIgnoreCase),
            timeoutSeconds: 12,
            stopOnFirstMatch: true,
            ct: ct);

        return results.FirstOrDefault();
    }

    private async Task<IEnumerable<IDevice>> DiscoverDevicesAsync(CancellationToken ct = default)
    {
        SetState(ConnectionState.Connecting);

        if (_ble.State != BluetoothState.On)
            return Enumerable.Empty<IDevice>();

        var results = await ScanAsync(
            d => !string.IsNullOrWhiteSpace(d.Name) &&
                 d.Name.Contains("DropSense", StringComparison.OrdinalIgnoreCase),
            timeoutSeconds: 10,
            stopOnFirstMatch: false,
            ct: ct);

        SetState(ConnectionState.Disconnected);

        return results.Distinct();
    }

    private async Task<List<IDevice>> ScanAsync(
    Func<IDevice, bool>? filter,
    int timeoutSeconds,
    bool stopOnFirstMatch,
    CancellationToken ct)
    {
        var results = new List<IDevice>();
        var tcs = new TaskCompletionSource<bool>();

        void Handler(object? s, DeviceEventArgs e)
        {
            var device = e.Device;
            if (device == null)
                return;

            if (filter == null || filter(device))
            {
                results.Add(device);

                if (stopOnFirstMatch)
                    tcs.TrySetResult(true);
            }
        }

        _adapter.DeviceDiscovered += Handler;

        try
        {
            await _adapter.StartScanningForDevicesAsync(cancellationToken: ct);

            var delayTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), ct);

            if (stopOnFirstMatch)
                using (ct.Register(() => tcs.TrySetCanceled()))
                {
                    await Task.WhenAny(tcs.Task, delayTask);
                }
            else
                await delayTask;

            await _adapter.StopScanningForDevicesAsync();
        }
        finally
        {
            _adapter.DeviceDiscovered -= Handler;
        }

        return results;
    }

    public async Task ConnectAsync(IDevice device, bool stayConnected = false, CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct);

        // Open BLE / socket connection to deviceId
        try
        {
            SetState(ConnectionState.Connecting);

            if (_ble.State != BluetoothState.On)
                throw new InvalidOperationException("Bluetooth is not enabled.");

            // Resolve device from known devices or system cache using its GUID/ID
            var parameters = new ConnectParameters(
            autoConnect: false,
            forceBleTransport: true
             );

            await _adapter.ConnectToDeviceAsync(device, cancellationToken: ct);


            _connectedDevice = device;
            ConnectedDeviceName = device.Name;


            SetState(ConnectionState.Connected);
        }
        catch
        {
            // If anything fails, ensure state is reset
            SetState(ConnectionState.Disconnected);
            _connectedDevice = null;
            ConnectedDeviceName = null;

            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            if (_connectedDevice != null)
            {
                await _adapter.DisconnectDeviceAsync(_connectedDevice);
                _connectedDevice = null;
            }
            ConnectedDeviceName = null;
        }
        finally
        {
            _connectionLock.Release();   // always runs, even if DisconnectDeviceAsync throws
        }
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

    public async Task<string> RequestDataDownloadAsync(IProgress<int>? progress = null, bool stayConnected = false, CancellationToken ct = default)
    {

        return await ExecuteWithConnectionAsync<string>(async (device, ct) =>
        {
            SetState(ConnectionState.Transferring);

            // ── 1. Resolve service + characteristic ─────────────────────
            var service = await device.GetServiceAsync(DROPSENSE_SERVICE_UUID);
            var commandChar = await service.GetCharacteristicAsync(COMMAND_CHAR_UUID);
            var dataChar = await service.GetCharacteristicAsync(DATA_CHAR_UUID);

            // ── 2. Prepare temp file ────────────────────────────────────
            var tempPath = Path.Combine(FileSystem.CacheDirectory,
                $"dropsense_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            await using var stream = File.OpenWrite(tempPath);

            int totalBytes = 0;
            int expectedBytes = -1;
            bool headerReceived = false;
            var tcs = new TaskCompletionSource<bool>();

            // ── 3. Subscribe to incoming data ───────────────────────────
            void Handler(object? s, CharacteristicUpdatedEventArgs e)
            {
                try
                {
                    var bytes = e.Characteristic.Value;

                    if (bytes == null || bytes.Length == 0)
                        return;

                    byte packetType = bytes[0];

                    switch (packetType)
                    {
                        // ── HEADER ─────────────────────────────
                        case 0x01:
                            if (bytes.Length >= 5)
                            {
                                expectedBytes = BitConverter.ToInt32(bytes, 1);
                                headerReceived = true;
                            }
                            break;

                        // ── DATA ───────────────────────────────
                        case 0x02:
                            if (!headerReceived)
                                return; // ignore until header received

                            stream.Write(bytes, 1, bytes.Length - 1);
                            totalBytes += (bytes.Length - 1);

                            if (expectedBytes > 0)
                            {
                                int percent = Math.Min(100,
                                    (int)((double)totalBytes / expectedBytes * 100));

                                progress?.Report(percent);
                            }
                            break;

                        // ── EOF ────────────────────────────────
                        case 0xFF:
                            stream.Flush();
                            tcs.TrySetResult(true);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }

            dataChar.ValueUpdated += Handler;

            try
            {
                await dataChar.StartUpdatesAsync();

                // ── 4. Send download request ─────────────────────────────
                var command = Encoding.UTF8.GetBytes("DOWNLOAD_CSV");
                await commandChar.WriteAsync(command);

                // ── 5. Wait for completion or cancellation ───────────────
                using (ct.Register(() => tcs.TrySetCanceled()))
                {
                    await tcs.Task;
                }

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

            // ── 7. Update state before disconnect ───────────────────────
            SetState(ConnectionState.Connected);

            return finalPath;

        }, stayConnected: stayConnected, ct);
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

    public bool IsBluetoothOn => _ble.State == BluetoothState.On;
}
