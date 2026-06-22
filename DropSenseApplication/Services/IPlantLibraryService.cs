using DropSense.Services;
using DropSense.Models;

namespace DropSense.Services
{
    /// <summary>
    /// Contract for the plant-library service.
    /// Exposes full CRUD for both <see cref="Plant"/> roots and their child
    /// <see cref="Threshold"/> records.  All mutations are persisted to a local
    /// JSON file automatically.
    /// </summary>
    public interface IPlantLibraryService
    {
        // ── Plant CRUD ────────────────────────────────────────────────────────

        /// <summary>Returns every plant in the library.</summary>
        Task<IReadOnlyList<Plant>> GetAllPlantsAsync();

        /// <summary>Returns a single plant, or <c>null</c> if not found.</summary>
        Task<Plant?> GetPlantByIdAsync(int plantId);

        /// <summary>
        /// Adds a new plant.  Assigns a unique PlantId automatically.
        /// Throws <see cref="InvalidOperationException"/> if CommonName is already taken.
        /// </summary>
        Task<Plant> AddPlantAsync(Plant plant);

        /// <summary>
        /// Replaces the scalar fields (CommonName, ScientificName, Notes) of an
        /// existing plant.  Does <em>not</em> touch the plant's Thresholds.
        /// Throws <see cref="KeyNotFoundException"/> if the plant does not exist.
        /// </summary>
        Task UpdatePlantAsync(Plant plant);

        /// <summary>
        /// Permanently removes a plant and all its thresholds.
        /// Throws <see cref="KeyNotFoundException"/> if the plant does not exist.
        /// </summary>
        Task DeletePlantAsync(int plantId);

        // ── Threshold CRUD ───────────────────────────────────────────────────

        /// <summary>Returns all thresholds for a given plant.</summary>
        Task<IReadOnlyList<LibraryThreshold>> GetThresholdsAsync(int plantId);

        /// <summary>
        /// Returns the threshold for a specific channel on a plant,
        /// or <c>null</c> if not yet defined.
        /// </summary>
        Task<LibraryThreshold?> GetThresholdAsync(int plantId, MeasurementChannel channel);

        /// <summary>
        /// Adds a threshold to a plant.
        /// Throws <see cref="InvalidOperationException"/> if a threshold for that
        /// channel already exists on the plant.
        /// </summary>
        Task<LibraryThreshold> AddThresholdAsync(int plantId, LibraryThreshold threshold);

        /// <summary>
        /// Replaces the values of an existing threshold record.
        /// Throws <see cref="KeyNotFoundException"/> if the plant or channel is not found.
        /// </summary>
        Task UpdateThresholdAsync(int plantId, LibraryThreshold threshold);

        /// <summary>
        /// Removes a threshold record from a plant.
        /// Throws <see cref="KeyNotFoundException"/> if the plant or channel is not found.
        /// </summary>
        Task DeleteThresholdAsync(int plantId, MeasurementChannel channel);
    }
}
