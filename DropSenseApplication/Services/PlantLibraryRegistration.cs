// DropSense — Services/PlantLibraryRegistration.cs
// ══════════════════════════════════════════════════════════════════════════════
// Call builder.Services.AddPlantLibrary() in MauiProgram.cs.

using Microsoft.Extensions.DependencyInjection;
using DropSense.Services;
using DropSense.ViewModels;
using DropSense.Views;

namespace DropSense.Services;

public static class PlantLibraryRegistration
{
    /// <summary>
    /// Registers all Plant Library types with the MAUI DI container.
    ///
    /// Usage — MauiProgram.cs:
    ///   builder.Services.AddPlantLibrary();
    /// </summary>
    public static IServiceCollection AddPlantLibrary(this IServiceCollection services)
    {
        // Service — singleton: one in-memory store, shared app-wide, lazy-loads JSON on first call
        services.AddSingleton<IPlantLibraryService, PlantLibraryService>();

        // ViewModel — singleton: state (list, expand/edit state) survives navigation
        services.AddSingleton<PlantLibraryViewModel>();

        // Page — transient is fine; ViewModel is singleton so state is preserved
        services.AddTransient<PlantLibraryPage>();

        return services;
    }
}
