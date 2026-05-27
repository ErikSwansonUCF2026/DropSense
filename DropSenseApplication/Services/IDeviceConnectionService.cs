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
using System.Diagnostics;
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
    ConnectionState State { get; }
    string? ConnectedDeviceName { get; }
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

    // ── Settings exchange ──────────────────────────────────────────────────
    // <summary>
    /// Serialises <paramref name="settings"/> into the binary wire format and
    /// writes it to the device's COMMAND_CHAR characteristic. Awaits an ACK
    /// byte (0xAA) back on the same characteristic before returning.
    /// Disconnects after sending unless <paramref name="stayConnected"/> is true.
    /// </summary>
    Task SendSettingsAsync(
        DeviceSettings settings,
        bool stayConnected = false,
        CancellationToken ct = default);

    // ── Data download ──────────────────────────────────────────────────────
    /// <summary>
    /// Requests the device to transmit its stored CSV data.
    /// Returns the path of the downloaded file on the host filesystem.
    /// Disconnects after the transfer completes to conserve device power.
    /// </summary>
    Task<string> RequestDataDownloadAsync(
        IProgress<int>? progress = null,
        bool stayConnected = false,
        CancellationToken ct = default);

    // ── Alert streaming ────────────────────────────────────────────────────
    /// <summary>
    /// Opens a persistent BLE notification subscription on DATA_CHAR.
    /// Whenever the device sends an alert packet (type byte 0x03), the raw
    /// payload bytes FOLLOWING the type byte are forwarded to
    /// <paramref name="alertReceived"/>. The connection is kept open (stayConnected
    /// = true internally). Call <see cref="StopAlertListeningAsync"/> to close.
    /// </summary>
    Task StartAlertListeningAsync(
        int checkIntervalSeconds,
        Action<byte[]> alertReceived,
        CancellationToken ct = default);

}

// ─────────────────────────────────────────────────────────────────────────────
// Implementation
// ─────────────────────────────────────────────────────────────────────────────



public class DeviceConnectionService : IDeviceConnectionService
{
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public string? ConnectedDeviceName { get; private set; }
    private static readonly Guid DROPSENSE_SERVICE_UUID = Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly Guid COMMAND_CHAR_UUID = Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly Guid DATA_CHAR_UUID = Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");

    // Outgoing Packet Types (host → device) / NACK reasons
    private const byte PacketAck = 0xAA;
    private const byte PacketNack = 0xAB;

    private const byte NackMalformedPacket = 0x01;
    private const byte NackLengthMismatch = 0x02;
    private const byte NackMissingSequence = 0x03;
    private const byte NackHandlerError = 0x04;

    // ACK timeout: how long to wait for the device to confirm it received
    // the settings payload. 5 s is generous; ideally should send
    // ACK within one connection interval (~7.5–100 ms depending on parameters).
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(5);

    public event EventHandler<ConnectionState>? ConnectionStateChanged;

    private IDevice? _connectedDevice;
    private readonly IBluetoothLE _ble;
    private readonly IAdapter _adapter;
    private readonly ISettingsService _settings;
    private readonly IFileSessionService _fileSession;

    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    // ── Alert listen state ────────────────────────────────────────────────────
    private ICharacteristic? _alertDataChar;
    private Action<byte[]>? _alertCallback;
    private CancellationTokenSource? _alertCts;

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

    // ── SendSettingsAsync ────────────────────────────────────────────
    public async Task SendSettingsAsync(
        DeviceSettings settings,
        bool stayConnected = false,
        CancellationToken ct = default)
    {
        // Serialise first so validation failures surface before BLE connection.
        var payload =
            DeviceSettingsSerializer.Serialize(settings);

        // Debug dump of actual packet being sent.
        Debug.WriteLine(
            $"[SendSettings] Payload ({payload.Length} B): " +
            $"{BitConverter.ToString(payload)}");

        await ExecuteWithConnectionAsync(
            async (device, ct) =>
            {
                // ── 1. Resolve service + characteristic ─────────────────────
                var service =
                    await device.GetServiceAsync(
                        DROPSENSE_SERVICE_UUID);

                var commandChar =
                    await service.GetCharacteristicAsync(
                        COMMAND_CHAR_UUID);

                // ── 2. Subscribe for ACK/NACK before write ─────────────────
                var ackTcs =
                    new TaskCompletionSource<byte>(
                        TaskCreationOptions
                            .RunContinuationsAsynchronously);

                void AckHandler(
                    object? s,
                    CharacteristicUpdatedEventArgs e)
                {
                    var response =
                        e.Characteristic.Value;

                    if (response == null ||
                        response.Length == 0)
                        return;

                    byte responseCode =
                        response[0];

                    Debug.WriteLine(
                        $"[SendSettings] RX: " +
                        $"{BitConverter.ToString(response)}");

                    ackTcs.TrySetResult(
                        responseCode);
                }

                commandChar.ValueUpdated += AckHandler;

                try
                {
                    await commandChar
                        .StartUpdatesAsync(ct);

                    // ── 3. Write settings payload ─────────────────────────
                    await commandChar
                        .WriteAsync(payload, ct);

                    Debug.WriteLine(
                        "[SendSettings] Payload written. " +
                        "Awaiting device ACK...");

                    // ── 4. Await ACK/NACK ────────────────────────────────
                    using var timeoutCts =
                        CancellationTokenSource
                            .CreateLinkedTokenSource(ct);

                    timeoutCts.CancelAfter(
                        AckTimeout);

                    using (timeoutCts.Token.Register(
                        () => ackTcs.TrySetCanceled()))
                    {
                        byte responseCode;

                        try
                        {
                            responseCode =
                                await ackTcs.Task;
                        }
                        catch (OperationCanceledException)
                            when (!ct.IsCancellationRequested)
                        {
                            Debug.WriteLine(
                                "[SendSettings] ACK timeout — " +
                                "payload delivered but device " +
                                "did not respond within timeout. " +
                                "Firmware may still have applied settings.");

                            return;
                        }

                        // ── NACK ──────────────────────────────────────────
                        if (responseCode ==
                            DeviceSettingsSerializer.NACK)
                        {
                            throw new InvalidOperationException(
                                "Device rejected settings payload (NACK). " +
                                "Firmware packet parser may not match " +
                                "host packet structure:\n" +
                                "Expected layout:\n" +
                                "0: CMD_SEND_SETTINGS\n" +
                                "1: CMD_FLAGS\n" +
                                "2–3: MeasurementIntervalSeconds\n" +
                                "4–7: UnixTimestampUtcSeconds\n" +
                                "8: AutoStartEnabled\n" +
                                "9: ThresholdCount\n" +
                                "10+: ThresholdSetting[]");
                        }

                        // ── Unexpected ACK byte ──────────────────────────
                        if (responseCode !=
                            DeviceSettingsSerializer.ACK)
                        {
                            Debug.WriteLine(
                                $"[SendSettings] Unexpected response: " +
                                $"0x{responseCode:X2}. " +
                                "Expected ACK (0xAA). " +
                                "Treating as success.");
                        }

                        // ── ACK ──────────────────────────────────────────
                        Debug.WriteLine(
                            "[SendSettings] Device ACK received. " +
                            "Settings applied.");
                    }
                }
                finally
                {
                    try
                    {
                        await commandChar
                            .StopUpdatesAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            "[SendSettings] StopUpdatesAsync failed " +
                            $"(non-fatal): {ex.Message}");
                    }

                    commandChar.ValueUpdated -=
                        AckHandler;
                }
            },
            stayConnected: stayConnected,
            ct: ct);
    }

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

            var stream = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
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
                stream.Flush();
                stream.Dispose();

                // allow OS file handle release
                await Task.Delay(50);
            }

            // ── 6. Move to Documents/DropSense ──────────────────────────
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var targetDir = Path.Combine(docs, "DropSense");

            Directory.CreateDirectory(targetDir);

            var finalPath = Path.Combine(targetDir, Path.GetFileName(tempPath));

            Debug.WriteLine(File.Exists(tempPath));
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    File.Move(tempPath, finalPath, true);
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(100);
                }
            }
            _fileSession.SetActiveFile(finalPath);

            // ── 7. Update state before disconnect ───────────────────────
            SetState(ConnectionState.Connected);

            return finalPath;

        }, stayConnected: stayConnected, ct);
    }

    // ── StartAlertListeningAsync ──────────────────────────────────────────────
    /// <summary>
    /// Connects (or reuses existing connection) and subscribes to DATA_CHAR
    /// notifications. Packets with type byte 0x03 are decoded and the payload
    /// (bytes after the type byte) forwarded to <paramref name="alertReceived"/>.
    /// Other packet types are silently ignored — this means alert listening and
    /// CSV download must not run concurrently (the download closes its own
    /// subscription; call StopAlertListeningAsync first if a download is needed).
    ///
    /// The connection is kept open (stayConnected=true) so the device can push
    /// alerts without the host polling. Call StopAlertListeningAsync to clean up.
    /// </summary>
    public async Task StartAlertListeningAsync(
        int checkIntervalSeconds,
        Action<byte[]> alertReceived,
        CancellationToken ct = default)
    {
        if (checkIntervalSeconds < 1)
            throw new ArgumentOutOfRangeException(
                nameof(checkIntervalSeconds),
                "Alert check interval must be at least 1 second.");

        var cts = new CancellationTokenSource();

        // Fire-and-forget onto the thread pool.
        // The loop lifetime is controlled entirely by cts.
        _ = Task.Run(
            () => RunAlertPollingLoopAsync(checkIntervalSeconds, alertReceived, cts.Token),
            cts.Token);

        return;
    }

    // ── Core polling loop ─────────────────────────────────────────────────────
    private async Task RunAlertPollingLoopAsync(
        int checkIntervalSeconds,
        Action<byte[]> alertReceived,
        CancellationToken ct)
    {
        // How long to wait after connecting for the device to push all buffered
        // alerts before assuming there are none. 3 s is sufficient for a device
        // that sends immediately on connection. If the device sends 0x04
        // (ALERT_END), the window closes early saving the remaining wait time.
        const int AlertWindowMs = 3_000;

        Debug.WriteLine(
            $"[AlertPolling] Loop started. Interval={checkIntervalSeconds}s, " +
            $"Window={AlertWindowMs}ms.");

        while (!ct.IsCancellationRequested)
        {
            // ── Wait the configured interval before the next check ────────────
            // On the very first iteration this means: "wait, then check".
            // Settings are sent before StartAlertListeningAsync is called, so
            // the device is already configured with the same interval — both
            // sides wake on the same schedule.
            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(checkIntervalSeconds),
                    ct);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[AlertPolling] Loop cancelled during wait.");
                return;
            }

            // ── Poll cycle: connect → collect → disconnect ────────────────────
            // ExecuteWithConnectionAsync handles:
            //   • EnsureConnectedAsync (fast-path via LastConnectedDeviceId)
            //   • DisconnectAsync in finally (stayConnected: false)
            //   • Propagating OperationCanceledException cleanly
            try
            {
                await ExecuteWithConnectionAsync(
                    async (device, ct) =>
                    {
                        await CollectAlertsFromDeviceAsync(
                            device, AlertWindowMs, alertReceived, ct);
                    },
                    stayConnected: false,   // disconnect automatically after each poll
                    ct: ct);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[AlertPolling] Loop cancelled during poll.");
                return;
            }
            catch (TimeoutException)
            {
                // Device not found — radio off or out of range.
                // Log and continue; the next cycle will retry.
                Debug.WriteLine("[AlertPolling] Device not found during poll. Will retry.");
            }
            catch (Exception ex)
            {
                // Non-fatal: log and continue so a transient BLE error does not
                // kill the entire alert-checking session.
                Debug.WriteLine($"[AlertPolling] Poll error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Debug.WriteLine("[AlertPolling] Loop exited cleanly.");
    }

    // ── Single poll cycle: subscribe, collect, unsubscribe ───────────────────
    private async Task CollectAlertsFromDeviceAsync(
        IDevice device,
        int windowMs,
        Action<byte[]> alertReceived,
        CancellationToken ct)
    {
        


        var service = await device.GetServiceAsync(DROPSENSE_SERVICE_UUID);
        var dataChar = await service.GetCharacteristicAsync(DATA_CHAR_UUID);

        // AlertEnd closes the window early when the device has sent all its
        // buffered alerts. Without this the host always waits the full windowMs
        // even when there is nothing to receive.
        var alertEndTcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        int alertsReceived = 0;

        // Expected sequence number for this poll cycle.
        // Assumes device starts from 0 every reconnect/poll.
        byte expectedSequence = 0;

        async Task SendAckAsync(byte sequence)
        {
            try
            {
                await dataChar.WriteAsync(
                    new[] { PacketAck, sequence },
                    ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[AlertPolling] ACK failed: {ex.Message}");
            }
        }

        async Task SendNackAsync(
            byte sequence,
            byte reason)
        {
            try
            {
                await dataChar.WriteAsync(
                    new[] { PacketNack, sequence, reason },
                    ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[AlertPolling] NACK failed: {ex.Message}");
            }
        }

        void Handler(
       object? s,
       CharacteristicUpdatedEventArgs e)
        {
            // Never block Plugin.BLE callback thread.
            _ = Task.Run(async () =>
            {
                try
                {
                    var bytes = e.Characteristic.Value;

                    if (bytes == null || bytes.Length == 0)
                        return;

                    switch (bytes[0])
                    {
                        case 0x03:
                            {
                                // Minimum packet:
                                // type + seq + length
                                if (bytes.Length < 3)
                                {
                                    Debug.WriteLine(
                                        "[AlertPolling] Malformed alert packet.");

                                    await SendNackAsync(
                                        0,
                                        NackMalformedPacket);

                                    return;
                                }

                                byte sequence = bytes[1];
                                byte payloadLength = bytes[2];

                                int expectedLength =
                                    3 + payloadLength;

                                // Validate payload length.
                                if (bytes.Length != expectedLength)
                                {
                                    Debug.WriteLine(
                                        $"[AlertPolling] Length mismatch. " +
                                        $"Expected={expectedLength}, " +
                                        $"Actual={bytes.Length}");

                                    await SendNackAsync(
                                        sequence,
                                        NackLengthMismatch);

                                    return;
                                }

                                // Detect missing/out-of-order packet.
                                if (sequence != expectedSequence)
                                {
                                    Debug.WriteLine(
                                        $"[AlertPolling] Sequence mismatch. " +
                                        $"Expected={expectedSequence}, " +
                                        $"Received={sequence}");

                                    await SendNackAsync(
                                        sequence,
                                        NackMissingSequence);

                                    return;
                                }

                                // Extract payload.
                                var payload = new byte[payloadLength];

                                Buffer.BlockCopy(
                                    bytes,
                                    3,
                                    payload,
                                    0,
                                    payloadLength);

                                // Forward validated alert.
                                alertReceived(payload);

                                alertsReceived++;
                                expectedSequence++;

                                // ACK only after successful processing.
                                await SendAckAsync(sequence);

                                break;
                            }

                        case 0x04:
                            {
                                Debug.WriteLine(
                                    $"[AlertPolling] ALERT_END received. " +
                                    $"{alertsReceived} alert(s) collected.");

                                alertEndTcs.TrySetResult(true);
                                break;
                            }

                        default:
                            {
                                // Ignore unrelated packet types.
                                Debug.WriteLine(
                                    $"[AlertPolling] Ignoring packet 0x{bytes[0]:X2}");
                                break;
                            }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[AlertPolling] Handler exception: {ex}");

                    try
                    {
                        await SendNackAsync(
                            expectedSequence,
                            NackHandlerError);
                    }
                    catch
                    {
                        // Ignore secondary failure.
                    }
                }
            }, ct);
        }

        dataChar.ValueUpdated += Handler;

        try
        {
            await dataChar.StartUpdatesAsync(ct);

            using var windowCts =
                CancellationTokenSource
                    .CreateLinkedTokenSource(ct);

            windowCts.CancelAfter(windowMs);

            try
            {
                using (windowCts.Token.Register(
                    () => alertEndTcs.TrySetCanceled()))
                {
                    await alertEndTcs.Task;
                }
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                    throw;

                if (alertsReceived > 0)
                {
                    Debug.WriteLine(
                        $"[AlertPolling] Window closed by timeout. " +
                        $"{alertsReceived} alert(s) collected.");
                }
                else
                {
                    Debug.WriteLine(
                        "[AlertPolling] Window closed — no alerts this cycle.");
                }
            }
        }
        finally
        {
            dataChar.ValueUpdated -= Handler;

            try
            {
                await dataChar.StopUpdatesAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[AlertPolling] StopUpdatesAsync failed " +
                    $"(non-fatal): {ex.Message}");
            }
        }
    }

    // ── SetState ──────────────────────────────────────────────────────────────
    private void SetState(ConnectionState newState)
    {
        State = newState;
        ConnectionStateChanged?.Invoke(this, newState);
    }

    public bool IsBluetoothOn => _ble.State == BluetoothState.On;
}
