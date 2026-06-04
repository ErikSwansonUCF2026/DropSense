using System;
using System.Collections.Generic;
using System.Text;
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
        // Guard against possible nulls: Application.Current, Windows collection, Handler or PlatformView
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
    public Task<string?> PickCsvFileAsync(CancellationToken ct = default)
    {
        // MAUI's cross-platform FilePicker will be wired here at Step 4.
        // For now, return null so callers receive "no file selected".
        return Task.FromResult<string?>(null);
    }
}
 
#endif