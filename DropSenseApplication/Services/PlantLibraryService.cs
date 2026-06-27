using System.Text.Json;
using Microsoft.Extensions.Logging;
using DropSense.Services;
using DropSense.Models;

namespace DropSense.Services;

/// <summary>
/// JSON-backed implementation of <see cref="IPlantLibraryService"/>.
///
/// Thread-safety: all public methods acquire a <see cref="SemaphoreSlim"/>
/// so the service is safe to call from multiple async contexts (e.g. a
/// background sync task and the UI thread simultaneously).
///
/// Persistence: every mutation immediately serialises the in-memory list to
/// <c>FileSystem.AppDataDirectory/plant_library.json</c>.
/// </summary>
public sealed class PlantLibraryService : IPlantLibraryService
{
    // ── Infrastructure ────────────────────────────────────────────────────

    private readonly string _filePath;
    private readonly ILogger<PlantLibraryService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // ── In-memory store ───────────────────────────────────────────────────

    private List<Plant> _plants = new();
    private int _nextId = 1;
    private bool _loaded = false;

    // ── Constructor ───────────────────────────────────────────────────────

    public PlantLibraryService(ILogger<PlantLibraryService> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(FileSystem.AppDataDirectory, "plant_library.json");
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Plant CRUD
    // ═════════════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<Plant>> GetAllPlantsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            return _plants.AsReadOnly();
        }
        finally { _lock.Release(); }
    }

    public async Task<Plant?> GetPlantByIdAsync(int plantId)
    {
        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            return _plants.FirstOrDefault(p => p.PlantId == plantId);
        }
        finally { _lock.Release(); }
    }

    public async Task<Plant> AddPlantAsync(Plant plant)
    {
        ValidatePlantScalars(plant);

        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();

            if (_plants.Any(p => string.Equals(p.CommonName, plant.CommonName,
                                                StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"A plant with the common name '{plant.CommonName}' already exists.");

            plant.PlantId = _nextId++;
            plant.storedThresholds ??= new List<LibraryThreshold>();
            _plants.Add(plant);

            await PersistAsync();
            _logger.LogInformation("Added plant #{Id} '{Name}'", plant.PlantId, plant.CommonName);
            return plant;
        }
        finally { _lock.Release(); }
    }

    public async Task UpdatePlantAsync(Plant plant)
    {
        ValidatePlantScalars(plant);

        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();

            var existing = RequirePlant(plant.PlantId);

            // Guard unique CommonName (ignoring itself)
            if (_plants.Any(p => p.PlantId != plant.PlantId &&
                                    string.Equals(p.CommonName, plant.CommonName,
                                                StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"Another plant already uses the common name '{plant.CommonName}'.");

            existing.CommonName = plant.CommonName.Trim();
            existing.ScientificName = plant.ScientificName?.Trim();
            existing.Notes = plant.Notes?.Trim();
            // Thresholds are intentionally not overwritten here

            await PersistAsync();
            _logger.LogInformation("Updated plant #{Id}", plant.PlantId);
        }
        finally { _lock.Release(); }
    }

    public async Task DeletePlantAsync(int plantId)
    {
        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            var existing = RequirePlant(plantId);
            _plants.Remove(existing);
            await PersistAsync();
            _logger.LogInformation("Deleted plant #{Id}", plantId);
        }
        finally { _lock.Release(); }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Threshold CRUD
    // ═════════════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<LibraryThreshold>> GetThresholdsAsync(int plantId)
    {
        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            return RequirePlant(plantId).storedThresholds.AsReadOnly();
        }
        finally { _lock.Release(); }
    }

    public async Task<LibraryThreshold?> GetThresholdAsync(int plantId, MeasurementChannel channel)
    {
        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            return RequirePlant(plantId).storedThresholds
                                        .FirstOrDefault(t =>
                                        {
                                            return t.libChannel == channel;
                                        });
        }
        finally { _lock.Release(); }
    }

    public async Task<LibraryThreshold> AddThresholdAsync(int plantId, LibraryThreshold newThreshold)
    {
        ValidateThreshold(newThreshold);

        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            var plant = RequirePlant(plantId);

            if (plant.storedThresholds.Any(t => t.libChannel == newThreshold.libChannel))
                throw new InvalidOperationException(
                    $"Plant #{plantId} already has a threshold for {newThreshold.libChannel}. " +
                    "Use UpdateThreshold to modify it.");

            plant.storedThresholds.Add(newThreshold);
            await PersistAsync();

            _logger.LogInformation("Added threshold {Channel} to plant #{Id}",
                                    newThreshold.libChannel, plantId);
            return newThreshold;
        }
        finally { _lock.Release(); }
    }

    public async Task UpdateThresholdAsync(int plantId, LibraryThreshold changedThreshold)
    {
        ValidateThreshold(changedThreshold);

        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            var plant = RequirePlant(plantId);
            var existing = RequireThreshold(plant, changedThreshold.libChannel);

            existing.IdealMin = changedThreshold.IdealMin;
            existing.IdealMax = changedThreshold.IdealMax;
            existing.SafeMin = changedThreshold.SafeMin;
            existing.SafeMax = changedThreshold.SafeMax;
            existing.Unit = changedThreshold.Unit;

            await PersistAsync();
            _logger.LogInformation("Updated threshold {Channel} on plant #{Id}",
                                    changedThreshold.libChannel, plantId);
        }
        finally { _lock.Release(); }
    }

    public async Task DeleteThresholdAsync(int plantId, MeasurementChannel channel)
    {
        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            var plant = RequirePlant(plantId);
            var existing = RequireThreshold(plant, channel);

            plant.storedThresholds.Remove(existing);
            await PersistAsync();
            _logger.LogInformation("Deleted threshold {Channel} from plant #{Id}",
                                    channel, plantId);
        }
        finally { _lock.Release(); }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Private helpers
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Loads from disk on first access; no-op thereafter.</summary>
    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;

        if (File.Exists(_filePath))
        {
            try
            {
                await using var stream = File.OpenRead(_filePath);
                var data = await JsonSerializer.DeserializeAsync<PlantLibraryData>(
                                stream, _jsonOptions);

                if (data is not null)
                {
                    _plants = data.Plants ?? new List<Plant>();
                    _nextId = data.NextId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load plant library from {Path}. "
                                    + "Starting with empty library.", _filePath);
                _plants = new List<Plant>();
                _nextId = 1;
            }
        }

        _loaded = true;
    }

    /// <summary>Serialises the current in-memory state to disk atomically.</summary>
    private async Task PersistAsync()
    {
        var data = new PlantLibraryData { Plants = _plants, NextId = _nextId };

        // Guarantee the directory exists (first-run / fresh install)
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);   // no-op if already present

        // Write to a temp file then replace, to avoid corruption on crash
        var tmpPath = _filePath + ".tmp";
        await using (var stream = File.Create(tmpPath))
            await JsonSerializer.SerializeAsync(stream, data, _jsonOptions);

        File.Move(tmpPath, _filePath, overwrite: true);
    }

    // ── Guard helpers ─────────────────────────────────────────────────────

    private Plant RequirePlant(int plantId) =>
        _plants.FirstOrDefault(p => p.PlantId == plantId)
        ?? throw new KeyNotFoundException($"Plant #{plantId} was not found.");

    private static LibraryThreshold RequireThreshold(Plant plant, MeasurementChannel channel) =>
        plant.storedThresholds.FirstOrDefault(t => t.libChannel == channel)
        ?? throw new KeyNotFoundException(
            $"No threshold for {channel} found on plant #{plant.PlantId}.");

    // ── Validation ────────────────────────────────────────────────────────

    private static void ValidatePlantScalars(Plant plant)
    {
        if (string.IsNullOrWhiteSpace(plant.CommonName))
            throw new ArgumentException("CommonName is required.", nameof(plant));

        if (plant.CommonName.Length > 50)
            throw new ArgumentException("CommonName must be 50 characters or fewer.", nameof(plant));

        if (plant.ScientificName?.Length > 50)
            throw new ArgumentException("ScientificName must be 50 characters or fewer.", nameof(plant));
    }

    private static void ValidateThreshold(LibraryThreshold t)
    {
        if (string.IsNullOrWhiteSpace(t.Unit))
            throw new ArgumentException("Threshold.Unit is required.", nameof(t));

        // Logical range checks (only when both bounds are provided)
        if (t.IdealMin.HasValue && t.IdealMax.HasValue && t.IdealMin > t.IdealMax)
            throw new ArgumentException("IdealMin must be ≤ IdealMax.");

        if (t.SafeMin.HasValue && t.SafeMax.HasValue && t.SafeMin > t.SafeMax)
            throw new ArgumentException("SafeMin must be ≤ SafeMax.");
    }

    // ── JSON wrapper ──────────────────────────────────────────────────────

    /// <summary>
    /// Root object written to disk.  Wraps the list and the auto-increment
    /// counter so IDs remain stable across app restarts.
    /// </summary>
    private sealed class PlantLibraryData
    {
        public List<Plant> Plants { get; set; } = new();
        public int NextId { get; set; } = 1;
    }
}