using System.Text.Json;
using DropSense.Models;

namespace DropSense.Services;


public interface IAlertPersistenceService
{
    Task<List<AlertEvent>> LoadAlertsAsync();
    Task SaveAlertsAsync(IEnumerable<AlertEvent> alerts);
}

public class AlertPersistenceService : IAlertPersistenceService
{
    private const string FileName = "alerts.json";

    private readonly JsonSerializerOptions _jsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

    private readonly string _filePath;

    public AlertPersistenceService()
    {
        _filePath = Path.Combine(
            FileSystem.AppDataDirectory,
            FileName);
    }

    public async Task<List<AlertEvent>> LoadAlertsAsync()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new List<AlertEvent>();

            await using FileStream stream =
                File.OpenRead(_filePath);

            var alerts =
                await JsonSerializer.DeserializeAsync<List<AlertEvent>>(
                    stream,
                    _jsonOptions);

            return alerts ?? new List<AlertEvent>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AlertPersistence] Load failed: {ex}");

            return new List<AlertEvent>();
        }
    }

    public async Task SaveAlertsAsync(
        IEnumerable<AlertEvent> alerts)
    {
        try
        {
            string directory =
                Path.GetDirectoryName(_filePath)!;

            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            await using FileStream stream =
                File.Create(_filePath);

            await JsonSerializer.SerializeAsync(
                stream,
                alerts,
                _jsonOptions);

            await stream.FlushAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AlertPersistence] Save failed: {ex}");
        }
    }
}