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
using System.Runtime.CompilerServices;
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

    /// <summary>Establishes a connection to an explicitly supplied device.</summary>
    Task ConnectAsync(IDevice device, bool stayConnected = false, CancellationToken ct = default);

    /// <summary>
    /// Ensures a connection is live (reusing the last-known device where possible),
    /// executes <paramref name="operation"/>, then disconnects unless
    /// <paramref name="stayConnected"/> is true.
    /// </summary>
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

    // ── Settings exchange ──────────────────────────────────────────────────────
    /// <summary>
    /// Serialises <paramref name="settings"/> into the binary wire format and
    /// writes it to the device's COMMAND_CHAR characteristic. Awaits an ACK
    /// byte (0xAA) back on the same characteristic before returning.
    /// Disconnects after sending unless <paramref name="stayConnected"/> is true.
    /// </summary>
    Task SendSettingsAsync(
        DeviceSettings settings,
        bool stayConnected = false,
        CancellationToken ct = default);

    // ── Data download ──────────────────────────────────────────────────────────
    /// <summary>
    /// Requests the device to transmit its stored CSV data.
    /// Returns the path of the downloaded file on the host filesystem.
    /// Disconnects after the transfer completes to conserve device power.
    /// </summary>
    Task<string> RequestDataDownloadAsync(
        IProgress<int>? progress = null,
        bool stayConnected = false,
        CancellationToken ct = default);

    // ── Alert polling ──────────────────────────────────────────────────────────
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
        IAlertService alertService);

    Task InitializeAsync();
}

// ─────────────────────────────────────────────────────────────────────────────
// Implementation
// ─────────────────────────────────────────────────────────────────────────────

public class DeviceConnectionService : IDeviceConnectionService
{
    // ── GATT identifiers ──────────────────────────────────────────────────────

    private static readonly Guid ServiceUuid =
        Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");

    private static readonly Guid CommandCharUuid =
        Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");

    private static readonly Guid DataCharUuid =
        Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");

    // ── Commands (host → COMMAND_CHAR) ────────────────────────────────────────

    private const byte CmdDownloadCsv = 0x01;
    private const byte CmdSendSettings = 0x02;
    private const byte CmdRequestAlerts = 0x03;
    private const byte CmdFlagNone = 0x00;

    // ── DATA_CHAR incoming packet types (device → host) ───────────────────────

    private const byte PktCsvHeader = 0x01;
    private const byte PktCsvData = 0x02;
    private const byte PktAlert = 0x03;
    private const byte PktAlertEnd = 0x04;
    private const byte PktCsvEof = 0xFF;

    // ── COMMAND_CHAR response bytes (device → host, settings ACK/NACK) ────────

    private const byte RespAck = 0xAA;
    private const byte RespNack = 0xAB;

    // ── Alert packet ACK/NACK (host → DATA_CHAR, per alert packet) ────────────

    private const byte PktAck = 0xAA;
    private const byte PktNack = 0xAB;

    private const byte NackReasonMalformed = 0x01;
    private const byte NackReasonLength = 0x02;
    private const byte NackReasonSequence = 0x03;
    private const byte NackReasonInternal = 0x04;

    // ── Timing ────────────────────────────────────────────────────────────────

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

    /// <summary>
    /// Maximum time allowed for a full CSV download before the transfer is
    /// abandoned with a TimeoutException. Tune to the largest expected file size.
    /// </summary>
    private const int DownloadTimeoutSeconds = 30;

    // ── State ─────────────────────────────────────────────────────────────────

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string? ConnectedDeviceName { get; private set; }
    public bool IsBluetoothOn => _ble.State == BluetoothState.On;

    public event EventHandler<ConnectionState>? ConnectionStateChanged;
    public event Action<byte[]>? AlertReceived;

    private CancellationTokenSource? _alertPollingCts;
    private IDevice? _connectedDevice;
    private readonly IBluetoothLE _ble;
    private readonly IAdapter _adapter;
    private readonly ISettingsService _settings;
    private readonly IFileSessionService _fileSession;
    private readonly IAlertService _alertService;
    private readonly IDebugLogService _debugLogService;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _pollingLock = new(1, 1);


    // ── Constructor ───────────────────────────────────────────────────────────

    public DeviceConnectionService(ISettingsService settings, IFileSessionService fileSession, 
        IAlertService alertService, IDebugLogService debugLogService)
    {
        _ble = CrossBluetoothLE.Current;
        _adapter = CrossBluetoothLE.Current.Adapter;
        _settings = settings;
        _fileSession = fileSession;
        _alertService = alertService;
        _debugLogService = debugLogService;


        // Mirror OS-level radio state changes onto ConnectionStateChanged so the
        // UI can react to Bluetooth being switched off mid-session.
        _ble.StateChanged += (_, _) =>
        {
            Debug.WriteLine($"[BLE] Radio state changed → {_ble.State}");
            ConnectionStateChanged?.Invoke(this, State);
        };
    }
    public async Task InitializeAsync()
    {
        _debugLogService.Attach();

        // ── Restart alert polling if it was active before the app was closed ──
        bool pollingWasEnabled = Preferences.Get("alert_polling_enabled", defaultValue: false);

        if (pollingWasEnabled)
        {
            Debug.WriteLine("[AppInitializer] alert_polling_enabled=true — restarting polling.");

            int interval = Preferences.Get("alert_polling_interval_seconds", defaultValue: 30);
            StartAlertPollingAsync(interval, _alertService);
        }
        else
        {
            Debug.WriteLine("[AppInitializer] alert_polling_enabled=false — polling not started.");
        }

        await Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Public connection helpers
    // ══════════════════════════════════════════════════════════════════════════

    public async Task ExecuteWithConnectionAsync(
        Func<IDevice, CancellationToken, Task> operation,
        bool stayConnected = false,
        CancellationToken ct = default)
    {
        var device = await EnsureConnectedAsync(ct);

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

    // Overload for operations that return a result.
    public async Task<T> ExecuteWithConnectionAsync<T>(
        Func<IDevice, CancellationToken, Task<T>> operation,
        bool stayConnected = false,
        CancellationToken ct = default)
    {
        var device = await EnsureConnectedAsync(ct);

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
   

    public async Task ConnectAsync(
        IDevice device,
        bool stayConnected = false,
        CancellationToken ct = default)
    {
        Debug.WriteLine($"[ConnectAsync] Connecting to '{device.Name}' ({device.Id})…");

        await _connectionLock.WaitAsync(ct);
        try
        {
            SetState(ConnectionState.Connecting);

            if (_ble.State != BluetoothState.On)
                throw new InvalidOperationException("Bluetooth is not enabled.");

            await _adapter.ConnectToDeviceAsync(
                device,
                new ConnectParameters(autoConnect: false, forceBleTransport: true),
                cancellationToken: ct);

            _connectedDevice = device;
            ConnectedDeviceName = device.Name;

            SetState(ConnectionState.Connected);
            Debug.WriteLine($"[ConnectAsync] Connected to '{device.Name}'.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ConnectAsync] Failed: {ex.GetType().Name} — {ex.Message}");
            SetState(ConnectionState.Error);
            _connectedDevice = null;
            ConnectedDeviceName = null;
            SetState(ConnectionState.Disconnected);
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        Debug.WriteLine("[DisconnectAsync] Disconnecting…");

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
            Debug.WriteLine("[DisconnectAsync] Disconnected.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DisconnectAsync] Error during disconnect: {ex.GetType().Name} — {ex.Message}");
            // State is set to Disconnected regardless — the radio link is gone.
            _connectedDevice = null;
            ConnectedDeviceName = null;
            SetState(ConnectionState.Disconnected);
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Private connection helpers
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<IDevice> EnsureConnectedAsync(CancellationToken ct)
    {
        // Guard radio state before competing for the lock — fast-fail with no contention.
        if (_ble.State != BluetoothState.On)
        {
            SetState(ConnectionState.Disconnected);
            throw new InvalidOperationException("Bluetooth is not enabled.");
        }

        await _connectionLock.WaitAsync(ct);
        try
        {
            // Re-check inside the lock: a concurrent caller may have already
            // connected while this one was waiting.
            if (_connectedDevice != null && _connectedDevice.State == DeviceState.Connected)
            {
                Debug.WriteLine($"[EnsureConnected] Reusing existing connection to '{_connectedDevice.Name}'.");
                return _connectedDevice;
            }

            _connectedDevice = null;

            // 1. TRY LAST CONNECTED DEVICE (FAST PATH) ────────────────────────
            var lastId = _settings.LastConnectedDeviceId;

            if (!string.IsNullOrWhiteSpace(lastId) && Guid.TryParse(lastId, out var guid))
            {
                Debug.WriteLine($"[EnsureConnected] Attempting fast-path reconnect to {guid}…");

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
                            Debug.WriteLine($"[EnsureConnected] Fast-path success: '{device.Name}'.");
                            return device;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[EnsureConnected] Fast-path attempt {i + 1} failed: {ex.GetType().Name} — {ex.Message}");
                    }

                    if (i < 1)
                        await Task.Delay(1000, ct);
                }

                Debug.WriteLine("[EnsureConnected] Fast-path exhausted — falling back to scan.");
            }

            // 2. FALLBACK: SCAN FOR FIRST MATCH ───────────────────────────────
            Debug.WriteLine("[EnsureConnected] Scanning for DropSense device…");

            var found = await DiscoverFirstMatchingDeviceAsync(ct);

            if (found == null)
                throw new TimeoutException(
                    "No DropSense device found during scan. " +
                    "Ensure the device is powered on and advertising.");

            Debug.WriteLine($"[EnsureConnected] Found '{found.Name}' ({found.Id}) — connecting…");

            await _adapter.ConnectToDeviceAsync(found, cancellationToken: ct);

            if (found.State != DeviceState.Connected)
                throw new InvalidOperationException(
                    $"Device '{found.Name}' did not enter Connected state after ConnectToDeviceAsync. " +
                    $"Actual state: {found.State}.");

            _connectedDevice = found;
            _settings.LastConnectedDeviceId = found.Id.ToString();
            _settings.LastConnectedDeviceName = found.Name;

            SetState(ConnectionState.Connected);
            Debug.WriteLine($"[EnsureConnected] Scan-path success: '{found.Name}'.");

            return found;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[EnsureConnected] Unhandled exception: {ex.GetType().Name} — {ex.Message}");
            SetState(ConnectionState.Error);
            SetState(ConnectionState.Disconnected);
            throw;
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
        Debug.WriteLine("[DiscoverDevices] Starting full scan…");
        SetState(ConnectionState.Connecting);

        if (_ble.State != BluetoothState.On)
        {
            Debug.WriteLine("[DiscoverDevices] Bluetooth is off — aborting scan.");
            SetState(ConnectionState.Disconnected);
            return Enumerable.Empty<IDevice>();
        }

        var results = await ScanAsync(
            d => !string.IsNullOrWhiteSpace(d.Name) &&
                 d.Name.Contains("DropSense", StringComparison.OrdinalIgnoreCase),
            timeoutSeconds: 10,
            stopOnFirstMatch: false,
            ct: ct);

        SetState(ConnectionState.Disconnected);
        Debug.WriteLine($"[DiscoverDevices] Scan complete — {results.Count} device(s) found.");

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
            if (device == null) return;

            if (filter == null || filter(device))
            {
                results.Add(device);
                Debug.WriteLine($"[Scan] Discovered: '{device.Name}' ({device.Id})");

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
                    await Task.WhenAny(tcs.Task, delayTask);
            else
                await delayTask;

            await _adapter.StopScanningForDevicesAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[Scan] Error during scan: {ex.GetType().Name} — {ex.Message}");
            throw;
        }
        finally
        {
            _adapter.DeviceDiscovered -= Handler;
        }

        return results;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // SendSettingsAsync
    // ══════════════════════════════════════════════════════════════════════════

    public async Task SendSettingsAsync(
        DeviceSettings settings,
        bool stayConnected = false,
        CancellationToken ct = default)
    {
        // Serialise first so validation failures surface before any BLE connection.
        var payload = DeviceSettingsSerializer.Serialize(settings);

        Debug.WriteLine(
            $"[SendSettings] Payload ({payload.Length} B): {BitConverter.ToString(payload)}");

        await ExecuteWithConnectionAsync(
            async (device, linkedCt) =>
            {
                // ── 1. Resolve service + characteristic ───────────────────────
                var service = await device.GetServiceAsync(ServiceUuid);
                var commandChar = await service.GetCharacteristicAsync(CommandCharUuid);

                // ── 2. Subscribe for ACK/NACK before write ────────────────────
                var ackTcs = new TaskCompletionSource<byte>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                void AckHandler(object? s, CharacteristicUpdatedEventArgs e)
                {
                    var response = e.Characteristic.Value;
                    if (response == null || response.Length == 0) return;

                    byte code = response[0];
                    Debug.WriteLine($"[SendSettings] RX: {BitConverter.ToString(response)} (0x{code:X2})");
                    ackTcs.TrySetResult(code);
                }

                commandChar.ValueUpdated += AckHandler;
                try
                {
                    await commandChar.StartUpdatesAsync(linkedCt);

                    // ── 3. Write settings payload ─────────────────────────────
                    await commandChar.WriteAsync(payload, linkedCt);
                    Debug.WriteLine("[SendSettings] Payload written — awaiting ACK…");

                    // ── 4. Await ACK/NACK with timeout ────────────────────────
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCt);
                    timeoutCts.CancelAfter(SettingsAckTimeout);

                    using (timeoutCts.Token.Register(() => ackTcs.TrySetCanceled()))
                    {
                        byte responseCode;
                        try
                        {
                            responseCode = await ackTcs.Task;
                        }
                        catch (OperationCanceledException) when (!linkedCt.IsCancellationRequested)
                        {
                            // Timeout — not a caller cancel. The payload was delivered
                            // but the device did not ACK within the window.
                            Debug.WriteLine(
                                "[SendSettings] ACK timeout — payload delivered but device did not " +
                                $"respond within {SettingsAckTimeout.TotalSeconds} s. " +
                                "Firmware may still have applied the settings.");
                            return;
                        }

                        if (responseCode == DeviceSettingsSerializer.NACK)
                        {
                            throw new InvalidOperationException(
                                "Device rejected settings payload (NACK 0xAB). " +
                                "Firmware packet parser may not match host packet structure.\n" +
                                "Expected wire layout:\n" +
                                "  [0] CMD_SEND_SETTINGS (0x02)\n" +
                                "  [1] CMD_FLAGS         (0x00)\n" +
                                "  [2–3] MeasurementIntervalSeconds (uint16 LE)\n" +
                                "  [4–7] UnixTimestampUtcSeconds    (int32 LE)\n" +
                                "  [8]   AutoStartEnabled           (0x00 / 0x01)\n" +
                                "  [9]   ThresholdCount\n" +
                                "  [10+] ThresholdSetting[]");
                        }

                        if (responseCode != DeviceSettingsSerializer.ACK)
                        {
                            Debug.WriteLine(
                                $"[SendSettings] Unexpected response byte 0x{responseCode:X2} " +
                                "(expected ACK 0xAA) — treating as success.");
                        }
                        else
                        {
                            Debug.WriteLine("[SendSettings] ACK received — settings applied.");
                        }
                    }
                }
                catch (Exception ex) when (ex is not InvalidOperationException
                                               and not OperationCanceledException)
                {
                    Debug.WriteLine(
                        $"[SendSettings] Unexpected error: {ex.GetType().Name} — {ex.Message}");
                    throw;
                }
                finally
                {
                    try { await commandChar.StopUpdatesAsync(); }
                    catch (Exception ex)
                    { Debug.WriteLine($"[SendSettings] StopUpdatesAsync failed (non-fatal): {ex.Message}"); }

                    commandChar.ValueUpdated -= AckHandler;
                }
            },

            stayConnected: stayConnected,
            ct: ct);

       
        }
    

    // ══════════════════════════════════════════════════════════════════════════
    // RequestDataDownloadAsync
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<string> RequestDataDownloadAsync(
        IProgress<int>? progress = null,
        bool stayConnected = false,
        CancellationToken ct = default)
    {
        Debug.WriteLine("[Download] RequestDataDownloadAsync started.");

        return await ExecuteWithConnectionAsync<string>(async (device, linkedCt) =>
        {
            SetState(ConnectionState.Transferring);

            // ── 1. Resolve service + characteristics ──────────────────────────
            var service = await device.GetServiceAsync(ServiceUuid);
            var commandChar = await service.GetCharacteristicAsync(CommandCharUuid);
            var dataChar = await service.GetCharacteristicAsync(DataCharUuid);

            Debug.WriteLine("[Download] Service and characteristics resolved.");

            // ── 2. Prepare temp file ──────────────────────────────────────────
            var tempPath = Path.Combine(
                FileSystem.CacheDirectory,
                $"dropsense_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            int totalBytes = 0;
            int expectedBytes = -1;
            bool headerReceived = false;
            var tcs = new TaskCompletionSource<bool>();

            // Explicit block so stream.DisposeAsync() is called HERE,
            // before File.Move — not at the end of the lambda.
            {
                await using var stream = File.Open(
                    tempPath, FileMode.Create, FileAccess.Write, FileShare.None);

                Debug.WriteLine($"[Download] Temp file opened: {tempPath}");

                // ── 3. Data notification handler ──────────────────────────────
                void Handler(object? s, CharacteristicUpdatedEventArgs e)
                {
                    try
                    {
                        var bytes = e.Characteristic.Value;
                        if (bytes == null || bytes.Length == 0) return;

                        switch (bytes[0])
                        {
                            case PktCsvHeader:
                                if (bytes.Length >= 5)
                                {
                                    expectedBytes = BitConverter.ToInt32(bytes, 1);
                                    headerReceived = true;
                                    Debug.WriteLine(
                                        $"[Download] Header received — expected {expectedBytes} bytes.");
                                }
                                else
                                {
                                    Debug.WriteLine(
                                        $"[Download] Header packet too short ({bytes.Length} B) — ignored.");
                                }
                                break;

                            case PktCsvData:
                                if (!headerReceived)
                                {
                                    Debug.WriteLine("[Download] Data packet received before header — ignored.");
                                    return;
                                }

                                stream.Write(bytes, 1, bytes.Length - 1);
                                totalBytes += bytes.Length - 1;

                                if (expectedBytes > 0)
                                {
                                    int pct = Math.Min(100,
                                        (int)((double)totalBytes / expectedBytes * 100));
                                    progress?.Report(pct);
                                }
                                break;

                            case PktCsvEof:
                                Debug.WriteLine(
                                    $"[Download] EOF received — {totalBytes} bytes written.");
                                tcs.TrySetResult(true);
                                break;

                            default:
                                Debug.WriteLine(
                                    $"[Download] Unexpected packet type 0x{bytes[0]:X2} — ignored.");
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(
                            $"[Download] Handler exception: {ex.GetType().Name} — {ex.Message}");
                        tcs.TrySetException(ex);
                    }
                }

                // ── Registration is the first statement inside try so the
                //    finally block is guaranteed to unregister on every exit path.
                try
                {
                    dataChar.ValueUpdated += Handler;

                    await dataChar.StartUpdatesAsync(linkedCt);
                    Debug.WriteLine("[Download] DATA_CHAR notifications subscribed.");

                    // ── 4. Send download request ──────────────────────────────
                    await commandChar.WriteAsync(
                        new[] { CmdDownloadCsv, CmdFlagNone }, linkedCt);

                    Debug.WriteLine(
                        $"[Download] CmdDownloadCsv sent — waiting up to {DownloadTimeoutSeconds} s…");

                    // ── 5. Wait for EOF, timeout, or cancellation ─────────────
                    using var downloadCts =
                        CancellationTokenSource.CreateLinkedTokenSource(linkedCt);
                    downloadCts.CancelAfter(TimeSpan.FromSeconds(DownloadTimeoutSeconds));

                    using (downloadCts.Token.Register(() => tcs.TrySetCanceled()))
                    {
                        try
                        {
                            await tcs.Task;
                        }
                        catch (OperationCanceledException) when (!linkedCt.IsCancellationRequested)
                        {
                            throw new TimeoutException(
                                $"CSV download timed out after {DownloadTimeoutSeconds} s — " +
                                "device did not send PktCsvEof (0xFF). " +
                                $"Received {totalBytes} bytes before timeout. " +
                                "Check firmware sends the EOF packet.");
                        }
                    }

                    progress?.Report(100);
                    Debug.WriteLine("[Download] Transfer complete.");
                }
                catch (Exception ex) when (ex is not TimeoutException
                                               and not OperationCanceledException)
                {
                    Debug.WriteLine(
                        $"[Download] Error during transfer: {ex.GetType().Name} — {ex.Message}");
                    throw;
                }
                finally
                {
                    dataChar.ValueUpdated -= Handler;
                    try { await dataChar.StopUpdatesAsync(); }
                    catch (Exception ex)
                    { Debug.WriteLine($"[Download] StopUpdatesAsync failed (non-fatal): {ex.Message}"); }
                }

            } // ← stream.DisposeAsync() here — OS handle released before File.Move

            // ── 6. Move to Documents/DropSense ────────────────────────────────
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var targetDir = Path.Combine(docs, "DropSense");
            Directory.CreateDirectory(targetDir);

            var finalPath = Path.Combine(targetDir, Path.GetFileName(tempPath));

            bool moved = false;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    File.Move(tempPath, finalPath, overwrite: true);
                    moved = true;
                    Debug.WriteLine($"[Download] File moved to: {finalPath}");
                    break;
                }
                catch (IOException ex) when (i < 2)
                {
                    Debug.WriteLine(
                        $"[Download] File.Move attempt {i + 1} failed: {ex.Message} — retrying…");
                    await Task.Delay(150);
                }
            }

            if (!moved)
            {
                // Data was fully received — don't surface a move failure as a
                // download failure. Serve from cache and log prominently.
                Debug.WriteLine(
                    $"[Download] WARNING: File.Move failed after 3 attempts. " +
                    $"Serving from cache: {tempPath}");
                finalPath = tempPath;
            }

            _fileSession.SetActiveFile(finalPath);

            // ── 7. Restore state before ExecuteWithConnectionAsync disconnects ─
            SetState(ConnectionState.Connected);

            Debug.WriteLine($"[Download] RequestDataDownloadAsync complete — path: {finalPath}");
            return finalPath;

        }, stayConnected: stayConnected, ct);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Alert polling
    // ══════════════════════════════════════════════════════════════════════════

    public CancellationTokenSource StartAlertPollingAsync(
    int checkIntervalSeconds,
    IAlertService alertService)
    {
        if (checkIntervalSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(checkIntervalSeconds));

        // ─────────────────────────────────────────────
        // 🚨 GUARD: prevent multiple polling loops
        // ─────────────────────────────────────────────
        lock (_pollingLock)
        {
            if (_alertPollingCts is not null)
                StopAlertPolling();

            // ── Persist intent so AppInitializer can restart on next launch ──
            Preferences.Set("alert_polling_enabled", true);

            var cts = new CancellationTokenSource();
            _alertPollingCts = cts;

            _ = Task.Run(async () =>
            {
                // ── Immediate first collection before the timed loop begins ──
                try
                {
                    await ExecuteWithConnectionAsync(
                        (device, linkedCt) =>
                            CollectAlertsFromDeviceAsync(device, alertService, linkedCt),
                        stayConnected: false,
                        ct: cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine("[AlertPolling] Cancelled during initial collection.");
                    return;
                }
                catch (TimeoutException ex)
                {
                    Debug.WriteLine(
                        $"[AlertPolling] Device not found during initial collection ({ex.Message}) — continuing to loop.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[AlertPolling] Initial collection error ({ex.GetType().Name}): {ex.Message} — continuing to loop.");
                }
        _alertPollingCts = cts;

                await RunAlertPollingLoopAsync(checkIntervalSeconds, alertService, cts.Token);
            }, cts.Token);

            Debug.WriteLine($"[AlertPolling] Started — interval={checkIntervalSeconds}s");

            return cts;
        }
    }

    public void StopAlertPolling()
    {
        if (_alertPollingCts is null)
            return;

        Preferences.Set("alert_polling_enabled", false); // ← persist intent

        _alertPollingCts.Cancel();
        _alertPollingCts.Dispose();
        _alertPollingCts = null;

        Debug.WriteLine("[AlertPolling] Stopped.");
    }

    public void StopAlertPolling()
    {
        if (_alertPollingCts is null)
            return;

        _alertPollingCts.Cancel();
        _alertPollingCts.Dispose();
        _alertPollingCts = null;

        Debug.WriteLine("[AlertPolling] Stopped.");
    }

    private async Task RunAlertPollingLoopAsync(
        int checkIntervalSeconds,
        IAlertService alertService,
        CancellationToken ct)
    {
        Debug.WriteLine(
            $"[AlertPolling] Loop running — interval={checkIntervalSeconds}s, " +
            $"window={AlertWindowMs}ms.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[AlertPolling] Cancelled during inter-poll wait.");
                return;
            }

            try
            {
                await ExecuteWithConnectionAsync(
                    (device, linkedCt) =>
                        CollectAlertsFromDeviceAsync(device, alertService, linkedCt),
                    stayConnected: false,
                    ct: ct);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[AlertPolling] Cancelled during poll.");
                return;
            }
            catch (TimeoutException ex)
            {
                Debug.WriteLine(
                    $"[AlertPolling] Device not found ({ex.Message}) — will retry next cycle.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[AlertPolling] Poll error ({ex.GetType().Name}): {ex.Message} — continuing.");
            }
        }

        Debug.WriteLine("[AlertPolling] Loop exited.");
    }

    private async Task CollectAlertsFromDeviceAsync(
        IDevice device,
        IAlertService alertService,
        CancellationToken ct)
    {
        var service = await device.GetServiceAsync(ServiceUuid);
        var commandChar = await service.GetCharacteristicAsync(CommandCharUuid);
        var dataChar = await service.GetCharacteristicAsync(DataCharUuid);

        var alertEndTcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        int alertsThisCycle = 0;
        byte expectedSequence = 0;

        async Task AckAsync(byte sequence)
        {
            try { await dataChar.WriteAsync(new[] { PktAck, sequence }, ct); }
            catch (Exception ex)
            { Debug.WriteLine($"[AlertPolling] ACK write failed (seq={sequence}): {ex.Message}"); }
        }

        async Task NackAsync(byte sequence, byte reason)
        {
            try { await dataChar.WriteAsync(new[] { PktNack, sequence, reason }, ct); }
            catch (Exception ex)
            { Debug.WriteLine($"[AlertPolling] NACK write failed (seq={sequence}): {ex.Message}"); }
        }

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
                                if (bytes.Length < 3)
                                {
                                    Debug.WriteLine(
                                        $"[AlertPolling] Malformed alert — packet too short ({bytes.Length} B).");
                                    await NackAsync(0, NackReasonMalformed);
                                    return;
                                }

                                byte seq = bytes[1];
                                byte payloadLength = bytes[2];
                                int expectedSize = 3 + payloadLength;

                                if (bytes.Length != expectedSize)
                                {
                                    Debug.WriteLine(
                                        $"[AlertPolling] Length mismatch — " +
                                        $"declared={expectedSize}, actual={bytes.Length}.");
                                    await NackAsync(seq, NackReasonLength);
                                    return;
                                }

                                if (seq != expectedSequence)
                                {
                                    Debug.WriteLine(
                                        $"[AlertPolling] Sequence mismatch — " +
                                        $"expected={expectedSequence}, received={seq}.");
                                    await NackAsync(seq, NackReasonSequence);
                                    return;
                                }

                                var payload = new byte[payloadLength];
                                Buffer.BlockCopy(bytes, 3, payload, 0, payloadLength);

                                await AckAsync(seq);

                                await alertService.AddRawAlertAsync(payload, "DropSense"); alertsThisCycle++;
                                expectedSequence++;

                                Debug.WriteLine(
                                    $"[AlertPolling] Alert seq={seq} ACKed and forwarded " +
                                    $"({payloadLength} B payload).");
                                break;
                            }

                        case PktAlertEnd:
                            Debug.WriteLine(
                                $"[AlertPolling] PktAlertEnd — {alertsThisCycle} alert(s) this cycle.");
                            alertEndTcs.TrySetResult(true);
                            break;

                        default:
                            Debug.WriteLine(
                                $"[AlertPolling] Unexpected packet type 0x{bytes[0]:X2} — ignored.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[AlertPolling] Handler exception: {ex.GetType().Name} — {ex.Message}");
                    try { await NackAsync(expectedSequence, NackReasonInternal); }
                    catch { /* swallow — we're already in an error path */ }
                }
            }, ct);
        }

        dataChar.ValueUpdated += OnAlertPacket;
        try
        {
            await dataChar.StartUpdatesAsync(ct);

            await commandChar.WriteAsync(new[] { CmdRequestAlerts, CmdFlagNone }, ct);
            Debug.WriteLine("[AlertPolling] CmdRequestAlerts sent — awaiting packets…");

            using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            windowCts.CancelAfter(AlertWindowMs);

            try
            {
                using (windowCts.Token.Register(() => alertEndTcs.TrySetCanceled()))
                    await alertEndTcs.Task;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Debug.WriteLine(
                    alertsThisCycle > 0
                        ? $"[AlertPolling] Window timeout — {alertsThisCycle} alert(s) collected."
                        : "[AlertPolling] Window timeout — no alerts this cycle.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine(
                $"[AlertPolling] CollectAlerts error: {ex.GetType().Name} — {ex.Message}");
            throw;
        }
        finally
        {
            dataChar.ValueUpdated -= OnAlertPacket;
            try { await dataChar.StopUpdatesAsync(); }
            catch (Exception ex)
            { Debug.WriteLine($"[AlertPolling] StopUpdatesAsync failed (non-fatal): {ex.Message}"); }
        }
    }

    // ── SetState ──────────────────────────────────────────────────────────────

    private void SetState(ConnectionState newState)
    {
        Debug.WriteLine($"[State] {State} → {newState}");
        State = newState;
        ConnectionStateChanged?.Invoke(this, newState);
    }
}