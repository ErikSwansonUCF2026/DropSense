// DropSense — Services/IPlantLibraryService.cs
// ══════════════════════════════════════════════════════════════════════════════
// Interface matching PlantLibraryService.cs exactly:
//   • Threshold type  → LibraryThreshold
//   • Channel enum    → MeasurementChannel
//   • Collection name → storedThresholds  (on Plant model)

using DropSense.Models;

namespace DropSense.Services;

public interface IPlantLibraryService
{
    // ── Plant CRUD ────────────────────────────────────────────────────────────
    Task<IReadOnlyList<Plant>> GetAllPlantsAsync();
    Task<Plant?> GetPlantByIdAsync(int plantId);
    Task<Plant> AddPlantAsync(Plant plant);
    Task UpdatePlantAsync(Plant plant);
    Task DeletePlantAsync(int plantId);

    // ── Threshold CRUD ────────────────────────────────────────────────────────
    Task<IReadOnlyList<LibraryThreshold>> GetThresholdsAsync(int plantId);
    Task<LibraryThreshold?> GetThresholdAsync(int plantId, MeasurementChannel channel);
    Task<LibraryThreshold> AddThresholdAsync(int plantId, LibraryThreshold newThreshold);
    Task UpdateThresholdAsync(int plantId, LibraryThreshold changedThreshold);
    Task DeleteThresholdAsync(int plantId, MeasurementChannel channel);
}