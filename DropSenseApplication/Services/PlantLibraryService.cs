using System.Text.Json;
using System.Text.Json.Serialization;
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
///
/// Android / AOT-safety: serialisation uses a source-generated
/// <see cref="JsonSerializerContext"/> (<see cref="PlantLibraryJsonContext"/>)
/// instead of the reflection-based <c>JsonSerializer</c> overloads. Release
/// Android builds run the linker (and can run full/partial AOT), which strips
/// the reflection metadata that the default JSON path depends on. Reflection
/// serialization can therefore work perfectly in Debug on a dev machine and
/// silently fail (or throw) once installed as a trimmed Release APK/AAB.
/// Source generation sidesteps that entirely.
/// </summary>
public sealed class PlantLibraryService : IPlantLibraryService, IDisposable
{
    // ── Infrastructure ────────────────────────────────────────────────────

    private readonly string _filePath;
    private readonly string _tmpPath;
    private readonly ILogger<PlantLibraryService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // ── In-memory store ───────────────────────────────────────────────────

    private List<Plant> _plants = new();
    private int _nextId = 1;
    private bool _loaded = false;

    // ── Constructor ───────────────────────────────────────────────────────

    public PlantLibraryService(ILogger<PlantLibraryService> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(FileSystem.AppDataDirectory, "plant_library.json");

        // Include a process-unique suffix on the temp file. If two instances
        // ever raced (e.g. a background service + the UI on some Android
        // configurations) a shared ".tmp" name could collide mid-write.
        _tmpPath = _filePath + $".{Environment.ProcessId}.tmp";
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

            // IMPORTANT: return a snapshot copy, not AsReadOnly() over the
            // Plant's live list. AsReadOnly() only wraps the same underlying
            // List<T> instance — it does NOT copy it. A caller that later
            // does something like `plant.storedThresholds.Clear()` (e.g. to
            // rebuild a UI-bound collection from this result) would silently
            // clear out the very list this method just returned, since both
            // are the same object in memory. Returning ToList() breaks that
            // aliasing and gives callers an independent, safe-to-mutate copy.
            return RequirePlant(plantId).storedThresholds.ToList();
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

    /// <summary>
    /// Loads from disk on first access; no-op thereafter. If no file exists
    /// yet (fresh install, or the app's private storage was cleared), the
    /// library is seeded with a baseline set of plants so the app never
    /// opens to an empty screen out of the box.
    /// </summary>
    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;

        if (File.Exists(_filePath))
        {
            try
            {
                await using var stream = File.OpenRead(_filePath);
                var data = await JsonSerializer.DeserializeAsync(
                                stream, PlantLibraryJsonContext.Default.PlantLibraryData);

                if (data is not null)
                {
                    _plants = data.Plants ?? new List<Plant>();
                    _nextId = data.NextId;
                }
                else
                {
                    SeedDefaultLibrary();
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Covers a corrupted file, a mid-write crash from a previous
                // session, or (on some Android storage configurations)
                // transient permission errors. Falling back to defaults
                // keeps the app usable instead of crashing on launch.
                _logger.LogError(ex, "Failed to load plant library from {Path}. "
                                    + "Falling back to the default library.", _filePath);
                SeedDefaultLibrary();
            }
        }
        else
        {
            // First run: no file on disk at all.
            SeedDefaultLibrary();
        }

        _loaded = true;

        // Persist so the seed (or the recovered default) is durable and
        // future loads read from disk rather than reseeding every crash.
        try
        {
            await PersistAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Non-fatal: the in-memory library is still usable for this
            // session even if the initial write-to-disk failed.
            _logger.LogError(ex, "Failed to persist the initial plant library to {Path}.", _filePath);
        }
    }

    /// <summary>
    /// Populates the in-memory store with a standard baseline set of plants
    /// and resets the ID counter. Used whenever there is no usable file on
    /// disk (fresh install or unrecoverable corruption).
    /// </summary>
    private void SeedDefaultLibrary()
    {
        _plants = new List<Plant>
    {
        new()
        {
            PlantId = 1,
            CommonName = "Kale",
            ScientificName = "Brassica oleracea var. sabellica",
            Notes = "A hardy cool-season leafy green prized for its nutrient-dense leaves and strong cold tolerance.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.Temperature, IdealMin = 12, IdealMax = 21, SafeMin = -4, SafeMax = 27, Unit = "°C" },
                new() { libChannel = MeasurementChannel.RelativeHumidity, IdealMin = 50, IdealMax = 70, SafeMin = 40, SafeMax = 80, Unit = "%" },
                new() { libChannel = MeasurementChannel.DailyLightIntegral, IdealMin = 10, IdealMax = 16, SafeMin = 6, SafeMax = 20, Unit = "mol/m²/day" },
                new() { libChannel = MeasurementChannel.VaporPressureDeficit, IdealMin = 0.8f, IdealMax = 1.1f, SafeMin = 0.6f, SafeMax = 1.4f, Unit = "kPa" }
            }
        },

        new()
        {
            PlantId = 2,
            CommonName = "Parsley",
            ScientificName = "Petroselinum crispum",
            Notes = "A biennial herb usually grown as an annual for its flavorful leaves.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.Temperature, IdealMin = 15, IdealMax = 24, SafeMin = 4, SafeMax = 30, Unit = "°C" },
                new() { libChannel = MeasurementChannel.RelativeHumidity, IdealMin = 50, IdealMax = 70, SafeMin = 40, SafeMax = 80, Unit = "%" },
                new() { libChannel = MeasurementChannel.DailyLightIntegral, IdealMin = 8, IdealMax = 14, SafeMin = 5, SafeMax = 18, Unit = "mol/m²/day" },
                new() { libChannel = MeasurementChannel.VaporPressureDeficit, IdealMin = 0.7f, IdealMax = 1.1f, SafeMin = 0.5f, SafeMax = 1.4f, Unit = "kPa" }
            }
        },

        new()
        {
            PlantId = 3,
            CommonName = "Arugula",
            ScientificName = "Eruca vesicaria",
            Notes = "A fast-growing leafy green with a peppery flavor.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.Temperature, IdealMin = 10, IdealMax = 20, SafeMin = 2, SafeMax = 26, Unit = "°C" },
                new() { libChannel = MeasurementChannel.RelativeHumidity, IdealMin = 50, IdealMax = 70, SafeMin = 40, SafeMax = 80, Unit = "%" },
                new() { libChannel = MeasurementChannel.DailyLightIntegral, IdealMin = 8, IdealMax = 12, SafeMin = 5, SafeMax = 16, Unit = "mol/m²/day" },
                new() { libChannel = MeasurementChannel.VaporPressureDeficit, IdealMin = 0.8f, IdealMax = 1.1f, SafeMin = 0.6f, SafeMax = 1.4f, Unit = "kPa" }
            }
        },

        new()
        {
            PlantId = 4,
            CommonName = "Thyme",
            ScientificName = "Thymus vulgaris",
            Notes = "A woody perennial herb valued for aromatic leaves and drought tolerance.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.Temperature, IdealMin = 18, IdealMax = 27, SafeMin = 5, SafeMax = 35, Unit = "°C" },
                new() { libChannel = MeasurementChannel.RelativeHumidity, IdealMin = 40, IdealMax = 60, SafeMin = 30, SafeMax = 70, Unit = "%" },
                new() { libChannel = MeasurementChannel.DailyLightIntegral, IdealMin = 14, IdealMax = 20, SafeMin = 8, SafeMax = 24, Unit = "mol/m²/day" },
                new() { libChannel = MeasurementChannel.VaporPressureDeficit, IdealMin = 1f, IdealMax = 1.4f, SafeMin = 0.7f, SafeMax = 1.8f, Unit = "kPa" }
            }
        },

        new()
        {
            PlantId = 5,
            CommonName = "Tomato",
            ScientificName = "Solanum lycopersicum",
            Notes = "A warm-season fruiting crop requiring strong light and steady airflow.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.Temperature, IdealMin = 21, IdealMax = 27, SafeMin = 13, SafeMax = 35, Unit = "°C" },
                new() { libChannel = MeasurementChannel.RelativeHumidity, IdealMin = 60, IdealMax = 75, SafeMin = 50, SafeMax = 85, Unit = "%" },
                new() { libChannel = MeasurementChannel.DailyLightIntegral, IdealMin = 20, IdealMax = 30, SafeMin = 15, SafeMax = 35, Unit = "mol/m²/day" },
                new() { libChannel = MeasurementChannel.VaporPressureDeficit, IdealMin = 1f, IdealMax = 1.3f, SafeMin = 0.8f, SafeMax = 1.6f, Unit = "kPa" }
            }
        },

        new()
        {
            PlantId = 6,
            CommonName = "Basil",
            ScientificName = "Ocimum basilicum",
            Notes = "Warm-weather herb requiring warmth, moisture, and bright light.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.Temperature, IdealMin = 20, IdealMax = 28, SafeMin = 10, SafeMax = 35, Unit = "°C" },
                new() { libChannel = MeasurementChannel.RelativeHumidity, IdealMin = 40, IdealMax = 60, SafeMin = 30, SafeMax = 70, Unit = "%" },
                new() { libChannel = MeasurementChannel.VaporPressureDeficit, IdealMin = 0.4f, IdealMax = 1f, SafeMin = 0.2f, SafeMax = 1.5f, Unit = "kPa" },
                new() { libChannel = MeasurementChannel.DailyLightIntegral, IdealMin = 14, IdealMax = 18, SafeMin = 8, SafeMax = 22, Unit = "mol/m²/day" }
            }
        },

                new()
        {
            PlantId = 7,
            CommonName = "Lettuce",
            ScientificName = "Lactuca sativa",
            Notes = "A fast-growing cool-season leafy green commonly grown for salads.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.Temperature, IdealMin = 15, IdealMax = 21, SafeMin = 4, SafeMax = 27, Unit = "°C" },
                new() { libChannel = MeasurementChannel.RelativeHumidity, IdealMin = 50, IdealMax = 70, SafeMin = 40, SafeMax = 80, Unit = "%" },
                new() { libChannel = MeasurementChannel.DailyLightIntegral, IdealMin = 10, IdealMax = 14, SafeMin = 6, SafeMax = 17, Unit = "mol/m²/day" },
                new() { libChannel = MeasurementChannel.VaporPressureDeficit, IdealMin = 0.3f, IdealMax = 0.8f, SafeMin = 0.1f, SafeMax = 1.2f, Unit = "kPa" }
            }
        },

        new()
        {
            PlantId = 8,
            CommonName = "Bell Pepper",
            ScientificName = "Capsicum annuum",
            Notes = "A warm-season fruiting vegetable requiring long warm seasons and strong light.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.Temperature, IdealMin = 20, IdealMax = 28, SafeMin = 15, SafeMax = 35, Unit = "°C" },
                new() { libChannel = MeasurementChannel.DailyLightIntegral, IdealMin = 18, IdealMax = 28, SafeMin = 12, SafeMax = 35, Unit = "mol/m²/day" },
                new() { libChannel = MeasurementChannel.RelativeHumidity, IdealMin = 60, IdealMax = 70, SafeMin = 50, SafeMax = 80, Unit = "%" },
                new() { libChannel = MeasurementChannel.VaporPressureDeficit, IdealMin = 1f, IdealMax = 1.4f, SafeMin = 0.8f, SafeMax = 1.8f, Unit = "kPa" }
            }
        },

        new()
        {
            PlantId = 9,
            CommonName = "Cucumber",
            ScientificName = "Cucumis sativus",
            Notes = "A vigorous warm-season vine grown for crisp water-rich fruit.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.Temperature, IdealMin = 18, IdealMax = 30, SafeMin = 10, SafeMax = 35, Unit = "°C" },
                new() { libChannel = MeasurementChannel.RelativeHumidity, IdealMin = 60, IdealMax = 80, SafeMin = 50, SafeMax = 90, Unit = "%" },
                new() { libChannel = MeasurementChannel.DailyLightIntegral, IdealMin = 15, IdealMax = 20, SafeMin = 9, SafeMax = 24, Unit = "mol/m²/day" },
                new() { libChannel = MeasurementChannel.VaporPressureDeficit, IdealMin = 0.5f, IdealMax = 0.9f, SafeMin = 0.3f, SafeMax = 1.3f, Unit = "kPa" }
            }
        },

        new()
        {
            PlantId = 10,
            CommonName = "Strawberry",
            ScientificName = "Fragaria × ananassa",
            Notes = "A low-growing perennial fruiting plant popular in gardens and containers.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.Temperature, IdealMin = 15, IdealMax = 26, SafeMin = 0, SafeMax = 32, Unit = "°C" },
                new() { libChannel = MeasurementChannel.RelativeHumidity, IdealMin = 40, IdealMax = 60, SafeMin = 30, SafeMax = 75, Unit = "%" },
                new() { libChannel = MeasurementChannel.DailyLightIntegral, IdealMin = 12, IdealMax = 17, SafeMin = 7, SafeMax = 20, Unit = "mol/m²/day" },
                new() { libChannel = MeasurementChannel.VaporPressureDeficit, IdealMin = 0.3f, IdealMax = 0.9f, SafeMin = 0.1f, SafeMax = 1.3f, Unit = "kPa" }
            }
        },

        new()
        {
            PlantId = 11,
            CommonName = "Carrot",
            ScientificName = "Daucus carota subsp. sativus",
            Notes = "A cool-season root vegetable grown for its sweet edible taproot.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.Temperature, IdealMin = 16, IdealMax = 21, SafeMin = 4, SafeMax = 29, Unit = "°C" },
                new() { libChannel = MeasurementChannel.RelativeHumidity, IdealMin = 50, IdealMax = 70, SafeMin = 40, SafeMax = 80, Unit = "%" },
                new() { libChannel = MeasurementChannel.DailyLightIntegral, IdealMin = 10, IdealMax = 15, SafeMin = 6, SafeMax = 18, Unit = "mol/m²/day" },
                new() { libChannel = MeasurementChannel.VaporPressureDeficit, IdealMin = 0.4f, IdealMax = 0.9f, SafeMin = 0.2f, SafeMax = 1.2f, Unit = "kPa" }
            }
        },

        new()
        {
            PlantId = 12,
            CommonName = "Spinach",
            ScientificName = "Spinacia oleracea",
            Notes = "A hardy fast-growing leafy green suited to cool seasons.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.Temperature, IdealMin = 10, IdealMax = 20, SafeMin = -2, SafeMax = 24, Unit = "°C" },
                new() { libChannel = MeasurementChannel.RelativeHumidity, IdealMin = 50, IdealMax = 70, SafeMin = 40, SafeMax = 80, Unit = "%" },
                new() { libChannel = MeasurementChannel.DailyLightIntegral, IdealMin = 8, IdealMax = 12, SafeMin = 5, SafeMax = 15, Unit = "mol/m²/day" },
                new() { libChannel = MeasurementChannel.VaporPressureDeficit, IdealMin = 0.3f, IdealMax = 0.8f, SafeMin = 0.1f, SafeMax = 1.1f, Unit = "kPa" }
            }
        },

        new()
        {
            PlantId = 13,
            CommonName = "Cilantro",
            ScientificName = "Coriandrum sativum",
            Notes = "A fast-bolting annual herb grown for leaves and seeds.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.Temperature, IdealMin = 13, IdealMax = 21, SafeMin = 2, SafeMax = 27, Unit = "°C" },
                new() { libChannel = MeasurementChannel.RelativeHumidity, IdealMin = 45, IdealMax = 65, SafeMin = 35, SafeMax = 75, Unit = "%" },
                new() { libChannel = MeasurementChannel.DailyLightIntegral, IdealMin = 8, IdealMax = 12, SafeMin = 5, SafeMax = 16, Unit = "mol/m²/day" },
                new() { libChannel = MeasurementChannel.VaporPressureDeficit, IdealMin = 0.4f, IdealMax = 0.9f, SafeMin = 0.2f, SafeMax = 1.2f, Unit = "kPa" }
            }
        },

        new()
        {
            PlantId = 14,
            CommonName = "Marigold",
            ScientificName = "Tagetes spp.",
            Notes = "A sun-loving flowering annual commonly companion-planted in gardens.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.Temperature, IdealMin = 18, IdealMax = 27, SafeMin = 5, SafeMax = 35, Unit = "°C" },
                new() { libChannel = MeasurementChannel.RelativeHumidity, IdealMin = 40, IdealMax = 60, SafeMin = 30, SafeMax = 70, Unit = "%" },
                new() { libChannel = MeasurementChannel.DailyLightIntegral, IdealMin = 14, IdealMax = 20, SafeMin = 8, SafeMax = 25, Unit = "mol/m²/day" },
                new() { libChannel = MeasurementChannel.VaporPressureDeficit, IdealMin = 0.8f, IdealMax = 1.3f, SafeMin = 0.5f, SafeMax = 1.8f, Unit = "kPa" }
            }
        },

        new()
        {
            PlantId = 15,
            CommonName = "Mint",
            ScientificName = "Mentha spp.",
            Notes = "A vigorous perennial herb known for rapid growth and spreading habit.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.Temperature, IdealMin = 15, IdealMax = 25, SafeMin = -5, SafeMax = 32, Unit = "°C" },
                new() { libChannel = MeasurementChannel.RelativeHumidity, IdealMin = 50, IdealMax = 70, SafeMin = 35, SafeMax = 85, Unit = "%" },
                new() { libChannel = MeasurementChannel.DailyLightIntegral, IdealMin = 8, IdealMax = 14, SafeMin = 4, SafeMax = 18, Unit = "mol/m²/day" },
                new() { libChannel = MeasurementChannel.VaporPressureDeficit, IdealMin = 0.3f, IdealMax = 0.7f, SafeMin = 0.15f, SafeMax = 1f, Unit = "kPa" }
            }
        },
            new()
        {
            PlantId = 16,
            CommonName = "Pothos",
            ScientificName = "Epipremnum aureum",
            Notes = "Very tolerant of low light and infrequent watering.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.Temperature, IdealMin = 18, IdealMax = 27, SafeMin = 10, SafeMax = 32, Unit = "°C" },
                new() { libChannel = MeasurementChannel.RelativeHumidity, IdealMin = 40, IdealMax = 60, SafeMin = 20, SafeMax = 80, Unit = "%" }
            }
        },

        new()
        {
            PlantId = 17,
            CommonName = "Snake Plant",
            ScientificName = "Dracaena trifasciata",
            Notes = "Prefers to dry out fully between waterings and tolerates low light.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.VaporPressureDeficit, IdealMin = 0.4f, IdealMax = 1.2f, SafeMin = 0.2f, SafeMax = 1.8f, Unit = "kPa" },
                new() { libChannel = MeasurementChannel.DailyLightIntegral, IdealMin = 2, IdealMax = 8, SafeMin = 1, SafeMax = 12, Unit = "mol/m²/day" }
            }
        },

        new()
        {
            PlantId = 18,
            CommonName = "Peace Lily",
            ScientificName = "Spathiphyllum",
            Notes = "Wilts visibly when thirsty; recovers quickly once watered.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.DewPointTemperature, IdealMin = 12, IdealMax = 20, SafeMin = 5, SafeMax = 25, Unit = "°C" },
                new() { libChannel = MeasurementChannel.AbsoluteHumidity, IdealMin = 6, IdealMax = 12, SafeMin = 3, SafeMax = 16, Unit = "g/m³" }
            }
        },

        new()
        {
            PlantId = 19,
            CommonName = "Basil (Indoor Variety)",
            ScientificName = "Ocimum basilicum",
            Notes = "Heavy feeder that likes consistent moisture, warmth, and bright light.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.SolarIrradiance, IdealMin = 300, IdealMax = 600, SafeMin = 150, SafeMax = 800, Unit = "W/m²" },
                new() { libChannel = MeasurementChannel.EstimatedPAR, IdealMin = 300, IdealMax = 600, SafeMin = 150, SafeMax = 900, Unit = "µmol/m²/s" }
            }
        },

        new()
        {
            PlantId = 20,
            CommonName = "Succulent (mixed)",
            ScientificName = "Echeveria spp.",
            Notes = "Drought-tolerant and sun-loving; overwatering is the main risk.",
            storedThresholds = new List<LibraryThreshold>
            {
                new() { libChannel = MeasurementChannel.BarometricPressure, IdealMin = 990, IdealMax = 1025, SafeMin = 950, SafeMax = 1050, Unit = "hPa" },
                new() { libChannel = MeasurementChannel.AccumulatedSolarRadiation, IdealMin = 12, IdealMax = 25, SafeMin = 6, SafeMax = 35, Unit = "MJ/m²" }
            }
        }
    };

        _nextId = _plants.Max(p => p.PlantId) + 1;

        _logger.LogInformation(
            "Seeded default plant library with {Count} plants.",
            _plants.Count);
    }

    /// <summary>Serialises the current in-memory state to disk atomically.</summary>
    private async Task PersistAsync()
    {
        var data = new PlantLibraryData { Plants = _plants, NextId = _nextId };

        // Guarantee the directory exists (first-run / fresh install)
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);   // no-op if already present

        // Write to a temp file then replace, to avoid corruption on crash.
        try
        {
            await using (var stream = File.Create(_tmpPath))
                await JsonSerializer.SerializeAsync(
                    stream, data, PlantLibraryJsonContext.Default.PlantLibraryData);

            File.Move(_tmpPath, _filePath, overwrite: true);
        }
        finally
        {
            // Clean up a leftover temp file if the write or move above threw,
            // so it doesn't linger in app-private storage indefinitely.
            if (File.Exists(_tmpPath))
            {
                try { File.Delete(_tmpPath); } catch { /* best effort */ }
            }
        }
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

    // ── Disposal ──────────────────────────────────────────────────────────

    public void Dispose() => _lock.Dispose();

    // ── JSON wrapper ──────────────────────────────────────────────────────

    /// <summary>
    /// Root object written to disk. Wraps the list and the auto-increment
    /// counter so IDs remain stable across app restarts.
    /// </summary>
    public sealed class PlantLibraryData
    {
        public List<Plant> Plants { get; set; } = new();
        public int NextId { get; set; } = 1;
    }
}

/// <summary>
/// Source-generated JSON context for the plant library.
///
/// This is the Android/AOT-safe replacement for a reflection-based
/// <see cref="JsonSerializerOptions"/>. The linker used in Release Android
/// builds can strip the metadata that reflection serialization needs;
/// source generation emits the (de)serialization code at compile time, so
/// there's nothing for the linker to remove and no reflection at runtime.
///
/// NOTE: Adjust the [JsonSerializable] list below if the Plant / LibraryThreshold
/// / MeasurementChannel model shapes differ from what's used above — every
/// type that gets (de)serialized, directly or nested, must be listed here.
///
/// MeasurementChannel already carries its own
/// [JsonConverter(typeof(JsonStringEnumConverter))] attribute, and the
/// source generator honors type-level JsonConverter attributes automatically
/// — no need to (and safer not to) register a second enum converter here.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PlantLibraryService.PlantLibraryData))]
[JsonSerializable(typeof(List<Plant>))]
[JsonSerializable(typeof(Plant))]
[JsonSerializable(typeof(List<LibraryThreshold>))]
[JsonSerializable(typeof(LibraryThreshold))]
[JsonSerializable(typeof(MeasurementChannel))]
internal partial class PlantLibraryJsonContext : JsonSerializerContext
{
}