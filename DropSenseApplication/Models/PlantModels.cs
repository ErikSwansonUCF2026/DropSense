using System.Text.Json.Serialization;

namespace DropSense.Models
{
    

    // ──────────────────────────────────────────────
    //  Threshold  (child entity – FK → Plant)
    // ──────────────────────────────────────────────

    public class LibraryThreshold
    {
        /// <summary>Which sensor / derived statistic this threshold covers.</summary>
        public MeasurementChannel libChannel { get; set; }

        // ── Ideal band (nullable – may be unknown) ──
        public float? IdealMin { get; set; }
        public float? IdealMax { get; set; }

        // ── Safe band (nullable – may be unknown) ──
        public float? SafeMin  { get; set; }
        public float? SafeMax  { get; set; }

        /// <summary>SI / display unit for this channel (e.g. "°C", "%", "hPa").</summary>
        public string Unit { get; set; } = string.Empty;
    }

    // ──────────────────────────────────────────────
    //  Plant  (root entity)
    // ──────────────────────────────────────────────

    public class Plant
    {
        public int    PlantId        { get; set; }   // AUTO-INCREMENT, set by service
        public string CommonName     { get; set; } = string.Empty;   // UNIQUE, MAX 50
        public string? ScientificName { get; set; }                  // MAX 50, nullable
        public string? Notes          { get; set; }

        /// <summary>
        /// One Threshold entry per MeasurementChannel.
        /// A plant may have 0–10 threshold records; channels without data are omitted
        /// rather than stored with all-null values.
        /// </summary>
        public List<LibraryThreshold> storedThresholds { get; set; } = new();
    }
}
