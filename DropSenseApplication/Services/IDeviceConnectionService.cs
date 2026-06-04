// DropSense — Services/IDeviceConnectionService.cs
//
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
// ── PROTOCOL DICTIONARY ────────────────────────────────────────────────────────
//
// SERVICE UUID
//   DROPSENSE_SERVICE_UUID   6E400001-B5A3-F393-E0A9-E50E24DCCA9E
//
// CHARACTERISTICS
//   COMMAND_CHAR_UUID        6E400002-B5A3-F393-E0A9-E50E24DCCA9E
//     Properties : Write-with-response, Notify
//     Direction  : Host → Device (commands/settings)
//                  Device → Host (ACK/NACK responses via notification)
//
//   DATA_CHAR_UUID           6E400003-B5A3-F393-E0A9-E50E24DCCA9E
//     Properties : Notify, Write-with-response
//     Direction  : Device → Host (CSV packets, alert packets)
//                  Host   → Device (ACK/NACK per alert packet)
//
// ── COMMANDS (host writes to COMMAND_CHAR) ──────────────────────────────────
//
//   CmdDownloadCsv     = 0x01   Host requests full CSV transfer.
//                               Payload: [0x01, 0x00 (flags)]
//   CmdSendSettings    = 0x02   Host pushes device configuration.
//                               Payload: [0x02, flags, interval_lo, interval_hi,
//                                         threshold_count, threshold[]…]
//   CmdRequestAlerts   = 0x03   Host requests all buffered alerts from device.
//                               Payload: [0x03, 0x00 (flags)]
//                               Device responds with PktAlert packets on DATA_CHAR
//                               followed by PktAlertEnd.
//
// ── DATA PACKETS (device sends to DATA_CHAR) ────────────────────────────────
//
//   PktCsvHeader   = 0x01   [0x01, total_bytes_b0..b3 (int32 LE)]
//                           Sent once before CSV data. Min 5 bytes.
//   PktCsvData     = 0x02   [0x02, payload…]
//                           CSV chunk. Up to 511 bytes of payload (512 MTU − 1).
//   PktCsvEof      = 0xFF   [0xFF]
//                           End of CSV stream. Single byte.
//
//   PktAlert       = 0x03   [0x03, seq (uint8), length (uint8), payload…]
//                           Single alert record.
//                           Payload (8 bytes): channel, severity, value (f32 LE),
//                                              condition_flags, reserved.
//   PktAlertEnd    = 0x04   [0x04]
//                           All buffered alerts have been sent. Single byte.
//                           Allows host to disconnect without waiting for timeout.
//
// ── ACK / NACK (device → COMMAND_CHAR, response to commands) ────────────────
//
//   RespAck        = 0xAA   Command accepted and applied (SendSettings only).
//   RespNack       = 0xAB   Command rejected (SendSettings only).
//                           Note: alert polling uses separate per-packet
//                           ACK/NACK on DATA_CHAR — see below.
//
// ── ALERT ACK / NACK (host writes to DATA_CHAR, per alert packet) ────────────
//
//   PktAck         = 0xAA   [0xAA, seq]  Alert received and processed.
//   PktNack        = 0xAB   [0xAB, seq, reason]  Transmission error — resend.
//                           Note: parse-level errors still ACK (device buffer
//                           must advance); error logging is host-side only.
//
//   NACK reason codes:
//     NackReasonMalformed = 0x01   Packet < 3 bytes.
//     NackReasonLength    = 0x02   Declared length ≠ actual packet size.
//     NackReasonSequence  = 0x03   Sequence number out of order.
//     NackReasonInternal  = 0x04   Unexpected handler exception.
//
// ══════════════════════════════════════════════════════════════════════════════
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

    /// <summary>
    /// Scans for available DropSense devices and returns their identifiers.
    /// Sets state to Connecting during the scan, Disconnected on return.
    /// </summary>
    Task<IEnumerable<IDevice>> DiscoverDevicesAsync(CancellationToken ct = default);

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
    /// Starts a background polling loop that fires every
    /// <paramref name="checkIntervalSeconds"/> seconds. Each cycle connects,
    /// sends CmdRequestAlerts (0x03) to COMMAND_CHAR, collects all PktAlert
    /// packets from DATA_CHAR, ACKs each one, then disconnects.
    /// <para>
    /// Must only be called AFTER <see cref="SendSettingsAsync"/> so the device
    /// has its configured interval before the first poll fires.
    /// </para>
    /// Cancel the returned <see cref="CancellationTokenSource"/> to stop the loop.
    /// </summary>
    CancellationTokenSource StartAlertPollingAsync(
        int checkIntervalSeconds,
        Action<byte[]> alertReceived);
}

// ─────────────────────────────────────────────────────────────────────────────
// Implementation
// ─────────────────────────────────────────────────────────────────────────────
public class DeviceConnectionService : IDeviceConnectionService
{
    // ── GATT identifiers ─────────────────────────────────────────────────────

    private static readonly Guid ServiceUuid =
        Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");

    private static readonly Guid CommandCharUuid =
        Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");

    private static readonly Guid DataCharUuid =
        Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");

    // ── Commands (host → COMMAND_CHAR) ───────────────────────────────────────

    private const byte CmdDownloadCsv = 0x01;
    private const byte CmdSendSettings = 0x02;
    private const byte CmdRequestAlerts = 0x03;   // explicit alert request
    private const byte CmdFlagNone = 0x00;   // reserved flags byte, always 0

    // ── DATA_CHAR incoming packet types (device → host) ──────────────────────

    private const byte PktCsvHeader = 0x01;
    private const byte PktCsvData = 0x02;
    private const byte PktAlert = 0x03;
    private const byte PktAlertEnd = 0x04;
    private const byte PktCsvEof = 0xFF;

    // ── COMMAND_CHAR response bytes (device → host, settings ACK/NACK) ───────

    private const byte RespAck = 0xAA;
    private const byte RespNack = 0xAB;

    // ── Alert packet ACK/NACK (host → DATA_CHAR, per alert packet) ───────────

    private const byte PktAck = 0xAA;
    private const byte PktNack = 0xAB;

    // NACK reason codes (third byte of a PktNack packet)
    private const byte NackReasonMalformed = 0x01;
    private const byte NackReasonLength = 0x02;
    private const byte NackReasonSequence = 0x03;
    private const byte NackReasonInternal = 0x04;

    // ── Timing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Time the host waits for an ACK after writing settings.
    /// Generous at 5 s; a well-implemented device should ACK within one
    /// connection interval (~7.5–100 ms).
    /// </summary>
    private static readonly TimeSpan SettingsAckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Per-poll window the host stays subscribed on DATA_CHAR waiting for
    /// alert packets. Closed early by PktAlertEnd; this is the fallback for
    /// firmware that does not send PktAlertEnd.
    /// </summary>
    private const int AlertWindowMs = 3_000;

    // ── State ─────────────────────────────────────────────────────────────────

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string? ConnectedDeviceName { get; private set; }
    public bool IsBluetoothOn => _ble.State == BluetoothState.On;

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

    // ── Constructor ───────────────────────────────────────────────────────────
    public DeviceConnectionService(ISettingsService settings, IFileSessionService fileSession)
    {
        _ble = CrossBluetoothLE.Current;
        _adapter = CrossBluetoothLE.Current.Adapter;
        _settings = settings;
        _fileSession = fileSession;

        // Mirror radio state changes onto ConnectionStateChanged so the UI
        // can react to Bluetooth being switched off at the OS level.
        _ble.StateChanged += (_, _) => ConnectionStateChanged?.Invoke(this, State);
    }


    // ══════════════════════════════════════════════════════════════════════════
    // Connection helpers
    // ══════════════════════════════════════════════════════════════════════════

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
        // Guard radio state before acquiring the lock — no point waiting if BT is off.
        if (_ble.State != BluetoothState.On)
        {
            SetState(ConnectionState.Disconnected);
            throw new InvalidOperationException("Bluetooth is not enabled.");
        }

        await _connectionLock.WaitAsync(ct);
        try
        {
            // Re-check inside the lock — another caller may have connected while we waited.
            if (_connectedDevice != null && _connectedDevice.State == DeviceState.Connected)
                return _connectedDevice;

            _connectedDevice = null;

            // 1. TRY LAST CONNECTED DEVICE (FAST PATH)
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
            }   // ← this brace was missing; closes the if-block so the fallback
                //   sits at the correct level inside the lock's try

            // 2. FALLBACK: SCAN FOR FIRST MATCH
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
        finally
        {
            _connectionLock.Release();
        }
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

    public async Task<IEnumerable<IDevice>> DiscoverDevicesAsync(CancellationToken ct = default)
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

    public async Task ConnectAsync(
        IDevice device, 
        bool stayConnected = false, 
        CancellationToken ct = default)
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
            SetState(ConnectionState.Disconnected);
        }
        finally
        {
            _connectionLock.Release();   // always runs, even if DisconnectDeviceAsync throws
        }
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
                        ServiceUuid);

                var commandChar =
                    await service.GetCharacteristicAsync(
                        CommandCharUuid);

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
                        SettingsAckTimeout);

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
            var service = await device.GetServiceAsync(ServiceUuid);
            var commandChar = await service.GetCharacteristicAsync(CommandCharUuid);
            var dataChar = await service.GetCharacteristicAsync(DataCharUuid);

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
                await commandChar.WriteAsync(new[] { CmdDownloadCsv, CmdFlagNone }, ct);

                // ── 5. Wait for completion, cancellation, or timeout ────────
                using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                downloadCts.CancelAfter(TimeSpan.FromSeconds(30)); // tune to max expected transfer

                using (downloadCts.Token.Register(() => tcs.TrySetCanceled()))
                {
                    try
                    {
                        await tcs.Task;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Inner timeout fired — EOF never arrived from device.
                        // Stream contains whatever was received; surface as a timeout.
                        throw new TimeoutException(
                            "CSV download timed out — device did not send PktCsvEof (0xFF) " +
                            $"within the {30}s window. Check firmware sends the EOF packet.");
                    }
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
    public CancellationTokenSource StartAlertPollingAsync(
        int checkIntervalSeconds,
        Action<byte[]> alertReceived)
    {
        if (checkIntervalSeconds < 1)
            throw new ArgumentOutOfRangeException(
                nameof(checkIntervalSeconds),
                "Alert check interval must be at least 1 second.");

        var cts = new CancellationTokenSource();

        // Fire-and-forget — lifecycle controlled entirely by cts.
        _ = Task.Run(
            () => RunAlertPollingLoopAsync(checkIntervalSeconds, alertReceived, cts.Token),
            cts.Token);

        return cts;
    }

    // ── Polling loop ──────────────────────────────────────────────────────────

    private async Task RunAlertPollingLoopAsync(
        int checkIntervalSeconds,
        Action<byte[]> alertReceived,
        CancellationToken ct)
    {
        Debug.WriteLine(
            $"[AlertPolling] Loop started — interval={checkIntervalSeconds}s, " +
            $"window={AlertWindowMs}ms.");

        while (!ct.IsCancellationRequested)
        {
            // Wait first so the device has its configured interval before the
            // first poll. Settings are sent before this method is called.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[AlertPolling] Cancelled during wait.");
                return;
            }

            // Each cycle: connect → request → collect → disconnect.
            // ExecuteWithConnectionAsync handles connect + disconnect (stayConnected:false).
            try
            {
                await ExecuteWithConnectionAsync(
                    (device, ct) => CollectAlertsFromDeviceAsync(device, alertReceived, ct),
                    stayConnected: false,
                    ct: ct);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[AlertPolling] Cancelled during poll.");
                return;
            }
            catch (TimeoutException)
            {
                Debug.WriteLine("[AlertPolling] Device not found — will retry next cycle.");
            }
            catch (Exception ex)
            {
                // Transient BLE errors must not kill the loop.
                Debug.WriteLine($"[AlertPolling] Poll error ({ex.GetType().Name}): {ex.Message}");
            }
        }

        Debug.WriteLine("[AlertPolling] Loop exited.");
    }

    // ── Single poll cycle ─────────────────────────────────────────────────────

    private async Task CollectAlertsFromDeviceAsync(
        IDevice device,
        Action<byte[]> alertReceived,
        CancellationToken ct)
    {
        var service = await device.GetServiceAsync(ServiceUuid);
        var commandChar = await service.GetCharacteristicAsync(CommandCharUuid);
        var dataChar = await service.GetCharacteristicAsync(DataCharUuid);

        // alertEndTcs resolves when PktAlertEnd is received, closing the window early.
        var alertEndTcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        int alertsThisCycle = 0;
        byte expectedSequence = 0;   // device resets sequence to 0 each reconnect

        // ── ACK / NACK helpers ─────────────────────────────────────────────────
        // Note: ACK is sent for every structurally valid packet regardless of
        // whether AlertEvent.TryParse() can decode the payload. Parse failures
        // are handled host-side so the device buffer always advances.
        async Task AckAsync(byte sequence)
        {
            try { await dataChar.WriteAsync(new[] { PktAck, sequence }, ct); }
            catch (Exception ex)
            { Debug.WriteLine($"[AlertPolling] ACK write failed: {ex.Message}"); }
        }

        async Task NackAsync(byte sequence, byte reason)
        {
            try { await dataChar.WriteAsync(new[] { PktNack, sequence, reason }, ct); }
            catch (Exception ex)
            { Debug.WriteLine($"[AlertPolling] NACK write failed: {ex.Message}"); }
        }

        // ── DATA_CHAR notification handler ─────────────────────────────────────
        // Offloaded to the thread pool so the Plugin.BLE callback thread is
        // never blocked by WriteAsync (ACK/NACK) or alertReceived().
        void OnAlertPacket(object? _, CharacteristicUpdatedEventArgs e)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var bytes = e.Characteristic.Value;
                    if (bytes is null || bytes.Length == 0) return;

                    switch (bytes[0])
                    {
                        case PktAlert:
                            {
                                // Minimum: type(1) + seq(1) + length(1) = 3 bytes
                                if (bytes.Length < 3)
                                {
                                    Debug.WriteLine("[AlertPolling] Malformed alert — packet too short.");
                                    await NackAsync(0, NackReasonMalformed);
                                    return;
                                }

                                byte seq = bytes[1];
                                byte payloadLength = bytes[2];
                                int expectedSize = 3 + payloadLength;

                                if (bytes.Length != expectedSize)
                                {
                                    Debug.WriteLine(
                                        $"[AlertPolling] Length mismatch — declared={expectedSize}, " +
                                        $"actual={bytes.Length}.");
                                    await NackAsync(seq, NackReasonLength);
                                    return;
                                }

                                if (seq != expectedSequence)
                                {
                                    Debug.WriteLine(
                                        $"[AlertPolling] Sequence mismatch — expected={expectedSequence}, " +
                                        $"received={seq}.");
                                    await NackAsync(seq, NackReasonSequence);
                                    return;
                                }

                                // Extract alert payload (bytes after type, seq, length).
                                var payload = new byte[payloadLength];
                                Buffer.BlockCopy(bytes, 3, payload, 0, payloadLength);

                                // Forward to AlertService.AddRawAlertAsync.
                                // ACK is sent before forwarding so the device buffer
                                // advances immediately — parse failures are host-side only.
                                await AckAsync(seq);

                                alertReceived(payload);
                                alertsThisCycle++;
                                expectedSequence++;
                                break;
                            }

                        case PktAlertEnd:
                            {
                                Debug.WriteLine(
                                    $"[AlertPolling] PktAlertEnd — {alertsThisCycle} alert(s) this cycle.");
                                alertEndTcs.TrySetResult(true);
                                break;
                            }

                        default:
                            Debug.WriteLine(
                                $"[AlertPolling] Unexpected packet type 0x{bytes[0]:X2} — ignored.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AlertPolling] Handler exception: {ex.Message}");
                    try { await NackAsync(expectedSequence, NackReasonInternal); } catch { /* ignore */ }
                }
            }, ct);
        }

        // ── Subscribe, request, collect, unsubscribe ───────────────────────────
        dataChar.ValueUpdated += OnAlertPacket;
        try
        {
            await dataChar.StartUpdatesAsync(ct);

            // Send explicit alert request to the device (CmdRequestAlerts + flags).
            // The device responds by pushing all buffered PktAlert packets on
            // DATA_CHAR, followed by PktAlertEnd.
            await commandChar.WriteAsync(new[] { CmdRequestAlerts, CmdFlagNone }, ct);

            Debug.WriteLine("[AlertPolling] CmdRequestAlerts sent — awaiting alerts…");

            // Wait for PktAlertEnd or the window timeout.
            using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            windowCts.CancelAfter(AlertWindowMs);

            try
            {
                using (windowCts.Token.Register(() => alertEndTcs.TrySetCanceled()))
                    await alertEndTcs.Task;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Window timeout — normal end for firmware without PktAlertEnd.
                Debug.WriteLine(
                    alertsThisCycle > 0
                        ? $"[AlertPolling] Window timeout — {alertsThisCycle} alert(s) collected."
                        : "[AlertPolling] Window timeout — no alerts this cycle.");
            }
        }
        finally
        {
            dataChar.ValueUpdated -= OnAlertPacket;
            try { await dataChar.StopUpdatesAsync(); }
            catch (Exception ex)
            { Debug.WriteLine($"[AlertPolling] StopUpdatesAsync failed (non-fatal): {ex.Message}"); }
        }
        // DisconnectAsync is called by ExecuteWithConnectionAsync's finally block.
    }

    // ── SetState ──────────────────────────────────────────────────────────────
    private void SetState(ConnectionState newState)
    {
        State = newState;
        ConnectionStateChanged?.Invoke(this, newState);
    }

}
