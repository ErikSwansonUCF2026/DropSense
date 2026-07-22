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
//                           NOTE: Android does not negotiate a large ATT MTU by
//                           default (starts at 23 bytes / 20-byte payload). See
//                           NegotiateMtuIfAndroidAsync — without an explicit MTU
//                           request, large chunks from the device are silently
//                           truncated by the OS BLE stack on Android.
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
// ── ANDROID NOTES ────────────────────────────────────────────────────────────
//   • Runtime permissions: Android 12+ (API 31+) requires BLUETOOTH_SCAN and
//     BLUETOOTH_CONNECT; API < 31 requires ACCESS_FINE_LOCATION for scanning.
//     EnsureAndroidBlePermissionsAsync() below requests whichever set applies.
//     These also need to be declared in AndroidManifest.xml, e.g.:
//       <uses-permission android:name="android.permission.BLUETOOTH_SCAN" />
//       <uses-permission android:name="android.permission.BLUETOOTH_CONNECT" />
//       <uses-permission android:name="android.permission.ACCESS_FINE_LOCATION" />
//   • ATT MTU: Android starts every GATT connection at a 23-byte MTU (20-byte
//     usable payload) and will NOT auto-negotiate a larger one the way iOS
//     does. NegotiateMtuIfAndroidAsync requests 517 bytes right after connect
//     so CSV chunks and settings payloads aren't truncated.
//   • Background execution: Android's Doze / App Standby can suspend timers
//     and drop BLE connections while the app is backgrounded. The alert
//     polling loop here is a plain Task.Run loop; for reliable polling while
//     backgrounded on Android you should host it from a foreground service
//     (e.g. via a platform-specific service + notification) rather than
//     relying on this in-process loop alone.
//   • Filesystem: Environment.SpecialFolder.MyDocuments is not a reliable
//     concept on Android (scoped storage / no shared "Documents" folder in
//     the way Windows has one). CSV downloads land under the app's own
//     FileSystem.AppDataDirectory instead on Android.
// ══════════════════════════════════════════════════════════════════════════════
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
#if WINDOWS
using Plugin.BLE.Windows;
#endif
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

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
    Task<CancellationTokenSource> StartAlertPollingAsync(
        int checkIntervalSeconds,
        IAlertService alertService);

    Task StopAlertPollingAsync();

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
    private const int AlertWindowMs = 1_000;

    /// <summary>
    /// Maximum time allowed for a full CSV download before the transfer is
    /// abandoned with a TimeoutException. Tune to the largest expected file size.
    /// </summary>
    private const int DownloadTimeoutSeconds = 30;

    /// <summary>
    /// ATT MTU requested on Android right after connecting, so 511-byte CSV
    /// payloads and settings writes aren't truncated to Android's 20-byte
    /// default usable payload. 517 = 512 (BLE spec max ATT_MTU) + 5-byte
    /// header allowance some stacks expect; Plugin.BLE clamps to what the
    /// platform/peer actually support.
    /// </summary>
    private const int AndroidRequestedMtu = 517;

    /// <summary>
    /// Maximum time to wait to *acquire* any of the internal coordination
    /// locks (_operationLock, _connectionLock, _pollingLock). These locks are
    /// held for the duration of a whole connect/write/disconnect cycle, so if
    /// one of those cycles gets wedged on a stalled native BLE call, every
    /// other caller waiting on the same lock would otherwise hang forever
    /// (SemaphoreSlim.WaitAsync has no built-in timeout of its own, and the
    /// callers here never pass a self-cancelling token). This bound turns
    /// that silent freeze into a clear, catchable TimeoutException instead.
    /// </summary>
    private static readonly TimeSpan LockAcquireTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum time allowed for any single native Plugin.BLE call used during
    /// the settings/connect path (ConnectToDeviceAsync, ConnectToKnownDeviceAsync,
    /// GetServiceAsync, GetCharacteristicAsync, WriteAsync, DisconnectDeviceAsync).
    /// These calls ultimately depend on the OS BLE stack and a real peripheral
    /// responding; nothing in Plugin.BLE guarantees they complete promptly, and
    /// the caller-supplied CancellationToken is often CancellationToken.None
    /// (i.e. it can never fire). Bounding each individual call defensively —
    /// in addition to whatever overall timeout the caller supplies — means a
    /// single stuck native call degrades to a clear error instead of wedging
    /// the whole connection/settings pipeline (and the locks that guard it)
    /// indefinitely.
    /// </summary>
    private static readonly TimeSpan BleCallTimeout = TimeSpan.FromSeconds(10);

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
    private readonly SemaphoreSlim _operationLock = new(1, 1);


    private volatile bool _downloadInProgress;


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

            int interval = Preferences.Get("settings_alert_interval", defaultValue: 300);
            await StartAlertPollingAsync(interval, _alertService);
        }
        else
        {
            Debug.WriteLine("[AppInitializer] alert_polling_enabled=false — polling not started.");
        }

        await Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // BLE write wrapper
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Single call site for all GATT characteristic writes.
    /// Centralises the explicit byte[] type so that Plugin.BLE's internal
    /// reflection never receives an int[] or boxed enum, which would throw
    /// ArgumentException ("Enum underlying type … was System.Int32").
    /// Bounded by <see cref="BleCallTimeout"/> so a stalled native write can't
    /// hang the caller (and any lock the caller is holding) forever.
    /// </summary>
    private static Task WriteCharAsync(
        ICharacteristic characteristic,
        byte[] data,
        CancellationToken ct)
        => WithTimeoutAsync(
            innerCt => characteristic.WriteAsync(data, innerCt),
            BleCallTimeout,
            ct,
            $"Characteristic write ({characteristic.Id})");

    // ══════════════════════════════════════════════════════════════════════════
    // Timeout helpers
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Acquires <paramref name="semaphore"/>, bounded by <paramref name="timeout"/>
    /// in addition to <paramref name="ct"/>. SemaphoreSlim.WaitAsync has no
    /// built-in timeout, so without this an operation stuck holding the lock
    /// (e.g. a wedged connect/disconnect cycle) would leave every other caller
    /// waiting forever, since the ct supplied by callers is frequently
    /// CancellationToken.None. Throws a clear <see cref="TimeoutException"/>
    /// instead of hanging silently.
    /// </summary>
    private static async Task WaitLockAsync(
        SemaphoreSlim semaphore,
        TimeSpan timeout,
        CancellationToken ct,
        string lockName)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await semaphore.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out after {timeout.TotalSeconds:0}s waiting for the {lockName}. " +
                "Another operation may be stuck holding it — check for a stalled BLE call.");
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> bounded by <paramref name="timeout"/> in
    /// addition to whatever <paramref name="ct"/> the caller supplied. Native
    /// Plugin.BLE calls (connect, disconnect, service/characteristic
    /// resolution, writes) are not guaranteed to return promptly on their own,
    /// and callers frequently pass CancellationToken.None, so relying solely
    /// on the caller's token is not sufficient to bound these calls. On
    /// timeout, throws <see cref="TimeoutException"/> rather than leaving the
    /// caller (and any lock it holds) blocked indefinitely.
    /// </summary>
    private static async Task<T> WithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> action,
        TimeSpan timeout,
        CancellationToken ct,
        string opName)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        // IMPORTANT: several Plugin.BLE calls used on this path (GetServiceAsync,
        // GetCharacteristicAsync, DisconnectDeviceAsync) do not accept a
        // CancellationToken at all. For those, simply passing timeoutCts.Token
        // into `action` and awaiting the result would do nothing — CancelAfter
        // only changes the token's own state, it can't reach into a Task that
        // never observes that token. So instead we race the call itself against
        // a delay task keyed to the same token: whichever finishes first wins.
        // This bounds how long the *caller* waits even when the native call
        // can't be torn down mid-flight (the call may keep running in the
        // background after we give up on it — unavoidable without cooperative
        // cancellation support from the library — but the caller, and any lock
        // it holds, is no longer stuck on it).
        var callTask = action(timeoutCts.Token);
        var watchdogTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);

        var finished = await Task.WhenAny(callTask, watchdogTask);

        if (finished != callTask)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            throw new TimeoutException($"{opName} timed out after {timeout.TotalSeconds:0}s.");
        }

        return await callTask; // observe/propagate the real result or exception
    }

    /// <summary>
    /// Non-generic overload of <see cref="WithTimeoutAsync{T}"/> for Task-returning
    /// calls (e.g. DisconnectDeviceAsync). Same race-against-a-watchdog approach —
    /// see the generic overload for why CancelAfter alone isn't sufficient here.
    /// </summary>
    private static async Task WithTimeoutAsync(
        Func<CancellationToken, Task> action,
        TimeSpan timeout,
        CancellationToken ct,
        string opName)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var callTask = action(timeoutCts.Token);
        var watchdogTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);

        var finished = await Task.WhenAny(callTask, watchdogTask);

        if (finished != callTask)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            throw new TimeoutException($"{opName} timed out after {timeout.TotalSeconds:0}s.");
        }

        await callTask; // observe/propagate the real exception, if any
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Android-specific helpers (permissions + MTU)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Requests the runtime permissions Android needs before scanning for or
    /// connecting to BLE peripherals. No-op on other platforms.
    /// Android 12+ (API 31+) uses BLUETOOTH_SCAN/BLUETOOTH_CONNECT; older
    /// versions require ACCESS_FINE_LOCATION for BLE scan results to be
    /// delivered at all. Throws if the user denies the request so callers
    /// fail fast with a clear message instead of silently getting empty scans.
    /// </summary>
    private static async Task EnsureAndroidBlePermissionsAsync()
    {
#if ANDROID
        // OperatingSystem.IsAndroidVersionAtLeast(...) is the pattern the
        // platform-compatibility analyzer actually recognizes as a version
        // guard. The previous Android.OS.Build.VERSION.SdkInt comparison was
        // functionally equivalent at runtime but invisible to CA1416, so the
        // analyzer had no way to know this branch was already safely guarded
        // for devices below API 31 — which is why raising the whole
        // project's SupportedOSPlatformVersion looked like the only fix.
        // With this guard in place, CA1416 is resolved at the call site
        // itself, so the project's real minimum (API 23) can stay accurate.
        if (OperatingSystem.IsAndroidVersionAtLeast(31)) // API 31+
        {
            var scanStatus = await Permissions.CheckStatusAsync<Permissions.Bluetooth>();
            if (scanStatus != PermissionStatus.Granted)
                scanStatus = await Permissions.RequestAsync<Permissions.Bluetooth>();

            if (scanStatus != PermissionStatus.Granted)
                throw new InvalidOperationException(
                    "Bluetooth permission (BLUETOOTH_SCAN/BLUETOOTH_CONNECT) was denied. " +
                    "DropSense cannot discover or connect to devices without it.");
        }
        else
        {
            var locationStatus = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (locationStatus != PermissionStatus.Granted)
                locationStatus = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();

            if (locationStatus != PermissionStatus.Granted)
                throw new InvalidOperationException(
                    "Location permission was denied. Android requires it for BLE scanning " +
                    "on this OS version, even though DropSense does not use GPS location data.");
        }
#else
        await Task.CompletedTask;
#endif
    }

    /// <summary>
    /// Requests a larger ATT MTU on Android immediately after connecting.
    /// Android connections start at a 23-byte MTU (20-byte usable payload)
    /// and never renegotiate automatically, unlike iOS/other platforms which
    /// already use a large MTU by default. Without this, 511-byte CSV chunks
    /// and multi-byte settings payloads get truncated by the OS BLE stack.
    /// Failure here is logged and swallowed — worst case the transfer falls
    /// back to small-packet behaviour instead of hard-failing the connection.
    /// </summary>
    private static async Task NegotiateMtuIfAndroidAsync(IDevice device, CancellationToken ct)
    {
#if ANDROID
        try
        {
            int negotiated = await WithTimeoutAsync(
                innerCt => device.RequestMtuAsync(AndroidRequestedMtu, innerCt),
                BleCallTimeout,
                ct,
                "RequestMtuAsync");
            Debug.WriteLine($"[MTU] Requested {AndroidRequestedMtu}, negotiated {negotiated}.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[MTU] RequestMtuAsync failed (non-fatal, falling back to default MTU): " +
                $"{ex.GetType().Name} — {ex.Message}");
        }
#else
        await Task.CompletedTask;
#endif
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

        await EnsureAndroidBlePermissionsAsync();

        await WaitLockAsync(_connectionLock, LockAcquireTimeout, ct, "connection lock");
        try
        {
            SetState(ConnectionState.Connecting);

            if (_ble.State != BluetoothState.On)
                throw new InvalidOperationException("Bluetooth is not enabled.");

            await WithTimeoutAsync(
                innerCt => _adapter.ConnectToDeviceAsync(
                    device,
                    new ConnectParameters(autoConnect: false, forceBleTransport: true),
                    cancellationToken: innerCt),
                BleCallTimeout,
                ct,
                $"ConnectToDeviceAsync('{device.Name}')");

            await NegotiateMtuIfAndroidAsync(device, ct);

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

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Debug.WriteLine("[Disconnect] Waiting for connection lock");

        try
        {
            await _connectionLock.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                "Timed out after 10s waiting for the connection lock during disconnect. " +
                "Another operation may be stuck holding it.");
        }

        try
        {
            Debug.WriteLine("[Disconnect] Connection lock acquired");

            if (_connectedDevice != null)
            {
                Debug.WriteLine("[Disconnect] Calling library DisconnectDeviceAsync...");

                var deviceToDisconnect = _connectedDevice;
                try
                {
                    await WithTimeoutAsync(
                        _ => _adapter.DisconnectDeviceAsync(deviceToDisconnect),
                        BleCallTimeout,
                        CancellationToken.None,
                        "DisconnectDeviceAsync");
                }
                catch (TimeoutException ex)
                {
                    // Don't let a stuck native disconnect call keep the radio
                    // link (and this lock) tied up forever — log and proceed
                    // to clear local state regardless. Worst case the OS-level
                    // link lingers until the peripheral's own supervision
                    // timeout kicks in.
                    Debug.WriteLine($"[Disconnect] {ex.Message} — clearing local state anyway.");
                }

                Debug.WriteLine(deviceToDisconnect.State);
                await Task.Delay(500);
                Debug.WriteLine(deviceToDisconnect.State);
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
            Debug.WriteLine("[Disconnect] Releasing connection lock");

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

        await EnsureAndroidBlePermissionsAsync();

        await WaitLockAsync(_connectionLock, LockAcquireTimeout, ct, "connection lock");
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
                        var device = await WithTimeoutAsync(
                            innerCt => _adapter.ConnectToKnownDeviceAsync(
                                guid,
                                new ConnectParameters(false, true),
                                innerCt),
                            BleCallTimeout,
                            ct,
                            $"ConnectToKnownDeviceAsync({guid})");

                        if (device != null && device.State == DeviceState.Connected)
                        {
                            await NegotiateMtuIfAndroidAsync(device, ct);

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

            await WithTimeoutAsync(
                innerCt => _adapter.ConnectToDeviceAsync(found, cancellationToken: innerCt),
                BleCallTimeout,
                ct,
                $"ConnectToDeviceAsync('{found.Name}')");

            if (found.State != DeviceState.Connected)
                throw new InvalidOperationException(
                    $"Device '{found.Name}' did not enter Connected state after ConnectToDeviceAsync. " +
                    $"Actual state: {found.State}.");

            await NegotiateMtuIfAndroidAsync(found, ct);

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

        await EnsureAndroidBlePermissionsAsync();

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
        await WaitLockAsync(_operationLock, LockAcquireTimeout, ct, "operation lock");
        try
        {
            await ExecuteWithConnectionAsync(
                async (device, linkedCt) =>
                {
                    // ── 1. Resolve service + characteristic ───────────────────────
                    var service = await WithTimeoutAsync(
                        _ => device.GetServiceAsync(ServiceUuid),
                        BleCallTimeout,
                        linkedCt,
                        "GetServiceAsync");
                    var commandChar = await WithTimeoutAsync(
                        _ => service.GetCharacteristicAsync(CommandCharUuid),
                        BleCallTimeout,
                        linkedCt,
                        "GetCharacteristicAsync(COMMAND_CHAR)");

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
                        await WithTimeoutAsync(
                            innerCt => commandChar.StartUpdatesAsync(innerCt),
                            BleCallTimeout,
                            linkedCt,
                            "StartUpdatesAsync(COMMAND_CHAR)");

                        // ── 3. Write settings payload ─────────────────────────────
                        await WriteCharAsync(commandChar, payload, linkedCt);
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
        finally
        {
            _operationLock.Release();
        }
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

        bool pollingWasRunning = _alertPollingCts is not null;
        Debug.WriteLine("[Download] Alert polling suspended for transfer.");

        await WaitLockAsync(_operationLock, LockAcquireTimeout, ct, "operation lock");
        _downloadInProgress = true;
        try
        {
            return await ExecuteWithConnectionAsync<string>(async (device, linkedCt) =>
            {
                SetState(ConnectionState.Transferring);

                // ── 1. Resolve service + characteristics ──────────────────────────
                var service = await WithTimeoutAsync(
                    _ => device.GetServiceAsync(ServiceUuid),
                    BleCallTimeout,
                    linkedCt,
                    "GetServiceAsync");
                var commandChar = await WithTimeoutAsync(
                    _ => service.GetCharacteristicAsync(CommandCharUuid),
                    BleCallTimeout,
                    linkedCt,
                    "GetCharacteristicAsync(COMMAND_CHAR)");
                var dataChar = await WithTimeoutAsync(
                    _ => service.GetCharacteristicAsync(DataCharUuid),
                    BleCallTimeout,
                    linkedCt,
                    "GetCharacteristicAsync(DATA_CHAR)");

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

                        await WithTimeoutAsync(
                            innerCt => dataChar.StartUpdatesAsync(innerCt),
                            BleCallTimeout,
                            linkedCt,
                            "StartUpdatesAsync(DATA_CHAR)");
                        Debug.WriteLine("[Download] DATA_CHAR notifications subscribed.");

                        // ── 4. Send download request ──────────────────────────────
                        await WriteCharAsync(
                            commandChar,
                            new byte[] { CmdDownloadCsv, CmdFlagNone },
                            linkedCt);

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
                        // try { await dataChar.StopUpdatesAsync(); }
                        //catch (Exception ex)
                        //{ Debug.WriteLine($"[Download] StopUpdatesAsync failed (non-fatal): {ex.Message}"); }
                    }

                } // ← stream.DisposeAsync() here — OS handle released before File.Move

                // ── 6. Move to app-owned "DropSense" documents folder ─────────────
                // Environment.SpecialFolder.MyDocuments is unreliable/unsupported on
                // Android (there is no shared "My Documents" concept, and scoped
                // storage means arbitrary external paths often aren't writable
                // without extra storage permissions). Use MAUI's app-scoped
                // FileSystem.AppDataDirectory there instead; keep the real
                // Documents folder on platforms where it behaves as expected.
#if ANDROID
                var docs = FileSystem.AppDataDirectory;
#elif WINDOWS
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
#else
                var docs = FileSystem.AppDataDirectory;
#endif
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
        finally
        {
            _downloadInProgress = false;
            _operationLock.Release();
            Debug.WriteLine("[Download] Alert polling suspension lifted.");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Alert polling
    // ══════════════════════════════════════════════════════════════════════════

    public async Task<CancellationTokenSource> StartAlertPollingAsync(
        int checkIntervalSeconds,
        IAlertService alertService)
    {
        if (checkIntervalSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(checkIntervalSeconds));

        // _pollingLock is a SemaphoreSlim(1,1) — must be used with Wait/Release,
        // not lock(), which only locks on the object reference.
        await WaitLockAsync(_pollingLock, LockAcquireTimeout, CancellationToken.None, "polling lock");
        try
        {
            if (_alertPollingCts is not null)
                await StopAlertPollingAsync();

            Preferences.Set("alert_polling_enabled", true);

            var cts = new CancellationTokenSource();
            _alertPollingCts = cts;

            // NOTE (Android): this loop runs as a plain in-process Task and will
            // be paused or killed by Doze/App Standby once the app is backgrounded
            // for any length of time, and the BLE connection itself may be torn
            // down by the OS. For alert polling that must survive backgrounding on
            // Android, drive this loop from a foreground service (with a visible
            // notification) rather than relying solely on this Task.Run loop.
            _ = Task.Run(async () =>
            {
                if (!_downloadInProgress)
                {
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
                }
                else
                {
                    Debug.WriteLine("[AlertPolling] Skipping initial collection — download in progress.");
                }

                await RunAlertPollingLoopAsync(checkIntervalSeconds, alertService, cts.Token);
            }, cts.Token);

            Debug.WriteLine($"[AlertPolling] Started — interval={checkIntervalSeconds}s");
            return cts;
        }
        finally
        {
            _pollingLock.Release();
        }
    }

    public async Task StopAlertPollingAsync()
    {
        await WaitLockAsync(_pollingLock, LockAcquireTimeout, CancellationToken.None, "polling lock");
        try
        {
            if (_alertPollingCts is null) return;
            Preferences.Set("alert_polling_enabled", false);
            _alertPollingCts.Cancel();
            _alertPollingCts.Dispose();
            _alertPollingCts = null;
        }
        finally
        {
            _pollingLock.Release();
        }
    }


    private async Task RunAlertPollingLoopAsync(
        int checkIntervalSeconds,
        IAlertService alertService,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds), ct); }
            catch (OperationCanceledException) { return; }

            // ── Guard: yield the cycle if a download is in progress ───────────
            try
            {
                await WaitLockAsync(_operationLock, LockAcquireTimeout, ct, "operation lock");
            }
            catch (OperationCanceledException) { return; }
            catch (TimeoutException ex)
            {
                Debug.WriteLine($"[AlertPolling] {ex.Message} — skipping this cycle.");
                continue;
            }

            try
            {
                await ExecuteWithConnectionAsync(
                    (device, linkedCt) =>
                        CollectAlertsFromDeviceAsync(device, alertService, linkedCt),
                    stayConnected: false,
                    ct: ct);
            }
            catch (OperationCanceledException) { return; }
            catch (TimeoutException ex)
            {
                Debug.WriteLine($"[AlertPolling] Device not found ({ex.Message}) — will retry next cycle.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AlertPolling] Poll error ({ex.GetType().Name}): {ex.Message} — continuing.");
            }
            finally
            {
                _operationLock.Release();
            }
        }
    }

    private async Task CollectAlertsFromDeviceAsync(
        IDevice device,
        IAlertService alertService,
        CancellationToken ct)
    {
        var service = await WithTimeoutAsync(
            _ => device.GetServiceAsync(ServiceUuid),
            BleCallTimeout,
            ct,
            "GetServiceAsync");
        var commandChar = await WithTimeoutAsync(
            _ => service.GetCharacteristicAsync(CommandCharUuid),
            BleCallTimeout,
            ct,
            "GetCharacteristicAsync(COMMAND_CHAR)");
        var dataChar = await WithTimeoutAsync(
            _ => service.GetCharacteristicAsync(DataCharUuid),
            BleCallTimeout,
            ct,
            "GetCharacteristicAsync(DATA_CHAR)");

        // ── Single-writer, single-reader channel ──────────────────────────────────
        // The BLE callback enqueues raw bytes synchronously and returns immediately.
        // A single consumer loop below processes them in order — no concurrent access
        // to expectedSequence or alertsThisCycle is possible.
        var channel = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions
            {
                SingleWriter = true,   // only the BLE callback writes
                SingleReader = true,   // only the consumer loop reads
                AllowSynchronousContinuations = false
            });

        void OnAlertPacket(object? _, CharacteristicUpdatedEventArgs e)
        {
            var bytes = e.Characteristic.Value;
            if (bytes is null || bytes.Length == 0) return;

            // TryWrite on an UnboundedChannel never blocks and never fails
            // (unless the channel is already completed), so this is safe to call
            // from any thread without await.
            channel.Writer.TryWrite(bytes);
        }

        dataChar.ValueUpdated += OnAlertPacket;
        try
        {
            await WithTimeoutAsync(
                innerCt => dataChar.StartUpdatesAsync(innerCt),
                BleCallTimeout,
                ct,
                "StartUpdatesAsync(DATA_CHAR)");

            await WriteCharAsync(
                commandChar,
                new byte[] { CmdRequestAlerts, CmdFlagNone },
                ct);
            Debug.WriteLine("[AlertPolling] CmdRequestAlerts sent — awaiting packets…");

            // ── Consumer loop ─────────────────────────────────────────────────────
            // Runs on the awaiting thread. All state is local and single-threaded.
            int alertsThisCycle = 0;
            byte expectedSequence = 0;

            using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            windowCts.CancelAfter(AlertWindowMs);

            try
            {
                // ReadAllAsync yields each item as it arrives and exits cleanly when
                // the channel is completed (Writer.Complete() called below) or the
                // window CancellationToken fires.
                await foreach (var bytes in channel.Reader.ReadAllAsync(windowCts.Token))
                {
                    switch (bytes[0])
                    {
                        case PktAlert:
                            {
                                if (bytes.Length < 3)
                                {
                                    Debug.WriteLine(
                                        $"[AlertPolling] Malformed alert — packet too short ({bytes.Length} B).");
                                    await NackAsync(dataChar, (byte)0, NackReasonMalformed, ct);
                                    break;
                                }

                                byte seq = bytes[1];
                                byte payloadLength = bytes[2];
                                int expectedSize = 3 + payloadLength;

                                if (bytes.Length != expectedSize)
                                {
                                    Debug.WriteLine(
                                        $"[AlertPolling] Length mismatch — " +
                                        $"declared={expectedSize}, actual={bytes.Length}.");
                                    await NackAsync(dataChar, seq, NackReasonLength, ct);
                                    break;
                                }

                                if (seq != expectedSequence)
                                {
                                    Debug.WriteLine(
                                        $"[AlertPolling] Sequence mismatch — " +
                                        $"expected={expectedSequence}, received={seq}.");
                                    await NackAsync(dataChar, seq, NackReasonSequence, ct);
                                    break;
                                }

                                var payload = new byte[payloadLength];
                                Buffer.BlockCopy(bytes, 3, payload, 0, payloadLength);

                                Debug.WriteLine("[AlertPolling] Before ACK");
                                await AckAsync(dataChar, seq, ct);

                                Debug.WriteLine("[AlertPolling] Before AddRawAlertAsync");
                                await alertService.AddRawAlertAsync(payload, "DropSense");

                                Debug.WriteLine("[AlertPolling] After AddRawAlertAsync");

                                alertsThisCycle++;
                                expectedSequence++;

                                Debug.WriteLine(
                                    $"[AlertPolling] Alert seq={seq} ACKed and forwarded " +
                                    $"({payloadLength} B payload).");
                                break;
                            }

                        case PktAlertEnd:
                            Debug.WriteLine(
                                $"[AlertPolling] PktAlertEnd — {alertsThisCycle} alert(s) this cycle.");
                            // Signal the consumer loop to stop by completing the channel.
                            // Any packets already enqueued will still be drained before
                            // ReadAllAsync returns.
                            channel.Writer.TryComplete();
                            break;

                        default:
                            Debug.WriteLine(
                                $"[AlertPolling] Unexpected packet type 0x{bytes[0]:X2} — ignored.");
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Window timeout — not a caller cancel.
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
            // Ensure the channel is always completed so ReadAllAsync can't hang
            // if we exit via an exception before TryComplete() in PktAlertEnd.
            channel.Writer.TryComplete();
            dataChar.ValueUpdated -= OnAlertPacket;
            DateTime PollTime = DateTime.Now;
            Preferences.Set("Last Alert", PollTime);

            //try { await dataChar.StopUpdatesAsync(); }
            //catch (Exception ex)
            // { Debug.WriteLine($"[AlertPolling] StopUpdatesAsync failed (non-fatal): {ex.Message}"); }
        }
    }

    // ── ACK/NACK helpers ──────────────────────────────────────────────────────
    // Both route through WriteCharAsync so the explicit byte[] guarantee is
    // enforced at a single call site rather than repeated at each use.

    private Task AckAsync(ICharacteristic dataChar, byte sequence, CancellationToken ct)
    {
        try { return WriteCharAsync(dataChar, new byte[] { PktAck, sequence }, ct); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AlertPolling] ACK write failed (seq={sequence}): {ex.Message}");
            return Task.CompletedTask;
        }
    }

    private Task NackAsync(
        ICharacteristic dataChar, byte sequence, byte reason, CancellationToken ct)
    {
        try { return WriteCharAsync(dataChar, new byte[] { PktNack, sequence, reason }, ct); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AlertPolling] NACK write failed (seq={sequence}): {ex.Message}");
            return Task.CompletedTask;
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