// DropSense — Services/DeviceSettingsService.cs
//
// Wire-format models for the settings payload sent to the embedded device.
// All types in this file are intentionally plain data objects — no MAUI
// dependencies — so they can be unit-tested and later used by a CLI tool
// or desktop importer without modification.
//
// RELATIONSHIP TO THE ER DIAGRAM
// ────────────────────────────────
// The ER diagram defines:
//   Plants  1──* Thresholds
//   Thresholds.channel  = ENUM (Measurement type)
//   Thresholds.safeMin  = float  NOT NULL
//   Thresholds.safeMax  = float  NOT NULL
//   Thresholds.idealMin = float  (nullable)
//   Thresholds.idealMax = float  (nullable)
//   Thresholds.unit     = string NOT NULL
//
// THIS FILE sends only: channel + safeMin? + safeMax?
// (the minimum the device needs to enforce hardware-level safety cutoffs).
// The full Plant/Threshold objects live in the host database (Step 8).
// The device stores only the numeric limits it needs to act on.
// ══════════════════════════════════════════════════════════════════════════════

namespace DropSense.Services;

// ─────────────────────────────────────────────────────────────────────────────
// 1. Channel Enum
//    Matches Thresholds.channel in the ER diagram.
//    Extend here as new sensor types are added — the wire protocol uses the
//    byte value of this enum, so existing values MUST NOT be renumbered.
// ─────────────────────────────────────────────────────────────────────────────

public enum MeasurementChannel : byte
{
    Temperature  = 0x00,
    Humidity     = 0x01,
    Pressure     = 0x02,
    Irradiance   = 0x03,
    VPD          = 0x04,
    DewPoint     = 0x05,
    CSI          = 0x06 // Add new channels here, incrementing byte values (max 255).
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. ThresholdSetting
//    Represents one row of the Thresholds table as it is sent to the device.
//    Only channel, safeMin, and safeMax are transmitted — idealMin/idealMax
//    and unit are host-only concerns stored in the local database.
//    Both limits are nullable: a null value tells the device to disable that
//    bound (e.g. a plant with no maximum temperature concern sends safeMax=null).
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ThresholdSetting
{
    /// <summary>
    /// Which sensor channel this threshold applies to.
    /// Maps to Thresholds.channel in the ER diagram.
    /// </summary>
    public required MeasurementChannel Channel { get; init; }

    /// <summary>
    /// Lower safety boundary. Null = no lower limit enforced on device.
    /// Maps to Thresholds.safeMin (nullable in the wire context).
    /// </summary>
    public float? SafeMin { get; init; }

    /// <summary>
    /// Upper safety boundary. Null = no upper limit enforced on device.
    /// Maps to Thresholds.safeMax (nullable in the wire context).
    /// </summary>
    public float? SafeMax { get; init; }

    // ── Wire serialization ────────────────────────────────────────────────────
    // Packet layout (10 bytes per threshold):
    //
    //   Byte 0      : MeasurementChannel (1 byte)
    //   Byte 1      : flags bitmask
    //                   bit 0 = safeMin present (1) / absent (0)
    //                   bit 1 = safeMax present (1) / absent (0)
    //                   bits 2-7 reserved, must be 0
    //   Bytes 2–5   : safeMin as IEEE 754 little-endian float32 (0x00000000 if absent)
    //   Bytes 6–9   : safeMax as IEEE 754 little-endian float32 (0x00000000 if absent)
    //
    // Total: 10 bytes per channel. A payload of N thresholds = 10N bytes + header.
    //
    // The flags byte makes absent values unambiguous — a legitimate safeMin of 0.0
    // would otherwise be indistinguishable from "not set".

    internal byte[] ToWireBytes()
    {
        var buf = new byte[10];

        buf[0] = (byte)Channel;

        byte flags = 0;
        if (SafeMin.HasValue) flags |= 0x01;
        if (SafeMax.HasValue) flags |= 0x02;
        buf[1] = flags;

        BitConverter.TryWriteBytes(buf.AsSpan(2, 4), SafeMin ?? 0f);
        BitConverter.TryWriteBytes(buf.AsSpan(6, 4), SafeMax ?? 0f);

        return buf;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. DeviceSettings
//    The full settings payload passed from DeviceSettingsViewModel to
//    IDeviceConnectionService.SendSettingsAsync(). Add new groups of settings
//    here as Step 3 expands. Anything not yet implemented is marked TODO.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class DeviceSettings
{
    // ── Sensor measurement frequency ─────────────────────────────────────────
    /// <summary>
    /// How often the device reads and logs sensor values, in seconds.
    /// Sent to device as a uint16 (0–65535 s). Minimum enforced: 1 s.
    /// Recommended range: 10–3600 s depending on plant sensitivity and battery budget.
    /// </summary>
    public ushort MeasurementIntervalSeconds { get; init; } = 60;

    // ── Alert check frequency (host-side) ────────────────────────────────────
    /// <summary>
    /// How often the HOST evaluates the downloaded data against alert thresholds.
    /// This is a host-only concept — the device does not need to know this value
    /// in the current architecture. It is included in DeviceSettings so the
    /// ViewModel can store it via ISettingsService alongside the other preferences.
    /// </summary>
    public ushort AlertCheckIntervalSeconds { get; init; } = 300;

    // ── Automatic startup behaviour ───────────────────────────────────────────
    /// <summary>
    /// Whether the device should automatically begin its measurement / alert
    /// monitoring cycle after boot or reset.
    ///
    /// Sent to firmware as a single binary byte:
    /// • 0x00 = disabled (manual start required)
    /// • 0x01 = enabled (auto-start)
    ///
    /// This setting is persisted with the other device settings and transmitted
    /// in the settings packet.
    /// </summary>
    public bool AutoStartEnabled { get; init; } = false;

    // ── Alert thresholds ─────────────────────────────────────────────────────
    /// <summary>
    /// Per-channel safety thresholds sent to the device.
    /// Extensible: add entries for new MeasurementChannel values without
    /// changing the wire protocol or this class.
    /// Configured in DeviceSettingsViewModel and passed here.
    /// At most one entry per channel — the serialiser validates this.
    /// </summary>
    public IReadOnlyList<ThresholdSetting> Thresholds { get; init; }
        = Array.Empty<ThresholdSetting>();

    

    // ── Suggested future settings (NOT YET IMPLEMENTED) ──────────────────────
    //
    // The following settings are worth adding as Step 3 matures. They are
    // listed here so the ViewModel, the embedded developer, and the wire
    // protocol can plan for them. Uncomment and implement when ready.
    //
    // (a) BatteryLowThresholdPct — byte
    //     Percentage below which the device raises a battery-low alert.
    //     Avoids the need to hard-code this on the firmware. Range: 1–50.
    //     // public byte BatteryLowThresholdPct { get; init; } = 15;


}

// ─────────────────────────────────────────────────────────────────────────────
// 4. Wire-format constants and serialiser (internal — used by the service only)
// ─────────────────────────────────────────────────────────────────────────────

internal static class DeviceSettingsSerializer
{
    // ── Command IDs (extend alongside firmware spec §2) ───────────────────────
    // DOWNLOAD_CSV is 0x01 0x00 per firmware spec.
    // Settings use 0x02 to avoid collision with any future 0x01 subcommand.
    private const byte CMD_SEND_SETTINGS = 0x02;
    private const byte CMD_FLAGS         = 0x00;   // reserved, must be 0

    // ── ACK / NACK values expected back from device on COMMAND_CHAR ──────────
    internal const byte ACK  = 0xAA;
    internal const byte NACK = 0xAB;

    // ── Settings packet layout ────────────────────────────────────────────────
    // Byte 0        : CMD_SEND_SETTINGS (0x02)
    // Byte 1        : CMD_FLAGS         (0x00)
    //
    // Bytes 2–3     : MeasurementIntervalSeconds (uint16 LE)
    //
    // Bytes 4–7     : UnixTimestampUtcSeconds    (uint32 LE)
    //                  Seconds since Unix epoch
    //                  (1970-01-01T00:00:00Z)
    //                  Used to synchronise firmware clock with application.
    //
    // Byte 8        : AutoStartEnabled           (uint8)
    //                  0x00 = disabled
    //                  0x01 = enabled
    //
    // Byte 9        : threshold count N          (uint8, max 16)
    //
    // Bytes 10+     : N × 10-byte ThresholdSetting records
    //
    // Total minimum: 10 bytes (no thresholds).
    // Total maximum: 10 + 16×10 = 170 bytes
    // — fits within a single BLE 4.2 MTU (185 bytes).
    //
    // AlertCheckIntervalSeconds is NOT included — it is a host-only preference.
    internal static byte[] Serialize(DeviceSettings settings)
    {
        // Validate: at most one threshold per channel
        var channels = settings.Thresholds
            .Select(t => t.Channel)
            .ToList();

        if (channels.Count != channels.Distinct().Count())
        {
            throw new ArgumentException(
                "DeviceSettings.Thresholds contains duplicate channels. " +
                "Each MeasurementChannel may appear at most once.");
        }

        if (settings.Thresholds.Count > 16)
        {
            throw new ArgumentException(
                $"Too many thresholds: {settings.Thresholds.Count} provided, maximum is 16.");
        }

        // ── Unix timestamp (UTC seconds since Unix epoch) ──────────────────
        uint unixTimestamp =
            (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // ── Build payload ───────────────────────────────────────────────────
        int thresholdBytes =
            settings.Thresholds.Count * 10;

        // Base packet now 10 bytes:
        // CMD + FLAGS + Interval + UnixTime + AutoStart + ThresholdCount
        var buf = new byte[10 + thresholdBytes];

        // Header
        buf[0] = CMD_SEND_SETTINGS;
        buf[1] = CMD_FLAGS;

        // Measurement interval (bytes 2–3)
        BitConverter.TryWriteBytes(
            buf.AsSpan(2, 2),
            settings.MeasurementIntervalSeconds);

        // Unix timestamp (bytes 4–7)
        BitConverter.TryWriteBytes(
            buf.AsSpan(4, 4),
            unixTimestamp);

        // Auto-start flag (byte 8)
        buf[8] =
            settings.AutoStartEnabled
                ? (byte)0x01
                : (byte)0x00;

        // Threshold count (byte 9)
        buf[9] = (byte)settings.Thresholds.Count;

        // Threshold records begin at byte 10
        int offset = 10;

        foreach (var threshold in settings.Thresholds)
        {
            threshold
                .ToWireBytes()
                .CopyTo(buf, offset);

            offset += 10;
        }

        return buf;
    }
}
