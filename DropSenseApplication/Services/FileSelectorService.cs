using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

#if WINDOWS
using Windows.Storage.Pickers;
using WinRT.Interop;
using Microsoft.Maui.ApplicationModel;

namespace DropSense.Services;

public class FileSelectorService : IFileSelectorService
{
    public async Task<string?> PickCsvFileAsync(CancellationToken ct = default)
    {
        var picker = new FileOpenPicker();

        var app = Application.Current;
        if (app == null || app.Windows == null || app.Windows.Count == 0)
            return null;

        var window = app.Windows[0];
        var handler = window?.Handler;
        if (handler?.PlatformView is not MauiWinUIWindow mauiWindow)
            return null;

        var hwnd = mauiWindow.WindowHandle;
        InitializeWithWindow.Initialize(picker, hwnd);

        picker.ViewMode = PickerViewMode.Thumbnail;
        picker.FileTypeFilter.Add(".csv");

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}

#else

namespace DropSense.Services;

public class FileSelectorService : IFileSelectorService
{
    public async Task<string?> PickCsvFileAsync(CancellationToken ct = default)
    {
        try
        {
            var customFileType = new FilePickerFileType(
                new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.Android, new[] { "text/comma-separated-values", "text/csv", "application/csv", "text/plain" } },
                    { DevicePlatform.iOS, new[] { "public.comma-separated-values-text" } },
                    { DevicePlatform.MacCatalyst, new[] { "public.comma-separated-values-text" } },
                });

            var options = new PickOptions
            {
                PickerTitle = "Select a CSV file",
                FileTypes = customFileType
            };

            var result = await FilePicker.Default.PickAsync(options);
            return result?.FullPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PickCsvFileAsync failed: {ex}");
            return null;
        }
    }
}

#endif