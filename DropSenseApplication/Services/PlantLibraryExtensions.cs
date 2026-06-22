using Microsoft.Extensions.DependencyInjection;
using DropSense.Services;

namespace DropSense.Services;

/// <summary>
/// IServiceCollection extension – call from MauiProgram.cs.
/// </summary>
public static class PlantLibraryServiceCollectionExtensions
{
}  

/*
═══════════════════════════════════════════════════════════════════════════════
FILE LAYOUT  (drop these into your .NET MAUI project)
═══════════════════════════════════════════════════════════════════════════════

PlantLibrary/
├── Models/
│   └── PlantModels.cs          ← Plant, Threshold, MeasurementChannel enum
├── Services/
│   ├── IPlantLibraryService.cs ← Interface (inject this everywhere)
│   └── PlantLibraryService.cs  ← JSON-backed singleton implementation
└── PlantLibraryExtensions.cs   ← This file – DI helper

═══════════════════════════════════════════════════════════════════════════════
MAUI PROGRAM SETUP
═══════════════════════════════════════════════════════════════════════════════

// MauiProgram.cs
var builder = MauiApp.CreateBuilder();
builder
  .UseMauiApp&lt;App&gt;()
  .ConfigureFonts(fonts => { ... });

builder.Services.AddPlantLibrary();          // ← one line
builder.Services.AddTransient&lt;PlantLibraryViewModel&gt;();
builder.Services.AddTransient&lt;PlantLibraryPage&gt;();

═══════════════════════════════════════════════════════════════════════════════
VIEW-MODEL INJECTION EXAMPLE
═══════════════════════════════════════════════════════════════════════════════

public class PlantLibraryViewModel
{
  private readonly IPlantLibraryService _svc;

  public PlantLibraryViewModel(IPlantLibraryService plantLibraryService)
      => _svc = plantLibraryService;

  // ── Examples ──────────────────────────────────────────────────────────

  public Task&lt;IReadOnlyList&lt;Plant&gt;&gt; LoadPlantsAsync()
      => _svc.GetAllPlantsAsync();

  public Task&lt;Plant&gt; CreatePlantAsync(string commonName, string? scientificName)
      => _svc.AddPlantAsync(new Plant
      {
          CommonName     = commonName,
          ScientificName = scientificName
      });

  public Task AddTemperatureThresholdAsync(int plantId)
      => _svc.AddThresholdAsync(plantId, new Threshold
      {
          Channel  = MeasurementChannel.Temperature,
          SafeMin  = 10f,
          SafeMax  = 35f,
          IdealMin = 18f,
          IdealMax = 26f,
          Unit     = "°C"
      });

  public Task DeletePlantAsync(int plantId)
      => _svc.DeletePlantAsync(plantId);
}

═══════════════════════════════════════════════════════════════════════════════
APPSHELL PANEL (AppShell.xaml – view layer stub)
═══════════════════════════════════════════════════════════════════════════════

&lt;Shell ...&gt;
  &lt;FlyoutItem Title="Plant Library" Icon="leaf.png"&gt;
      &lt;ShellContent
          Title="Library"
          ContentTemplate="{DataTemplate views:PlantLibraryPage}"
          Route="PlantLibrary" /&gt;
  &lt;/FlyoutItem&gt;
&lt;/Shell&gt;

═══════════════════════════════════════════════════════════════════════════════
JSON FILE LOCATION AT RUNTIME
═══════════════════════════════════════════════════════════════════════════════

FileSystem.AppDataDirectory/plant_library.json
(platform-specific AppData folder; not user-visible, backed up on iOS/Android)

═══════════════════════════════════════════════════════════════════════════════
MEASUREMENT CHANNELS & RECOMMENDED UNITS
═══════════════════════════════════════════════════════════════════════════════

Channel                    Suggested Unit
─────────────────────────  ──────────────
Temperature                °C  (or °F)
RelativeHumidity           %
BarometricPressure         hPa
SolarIrradiance            W/m²
VaporPressureDeficit       kPa
DewPointTemperature        °C
AbsoluteHumidity           g/m³
AccumulatedSolarRadiation  MJ/m²
DailyLightIntegral         mol/m²/day
EstimatedPAR               µmol/m²/s
*/
