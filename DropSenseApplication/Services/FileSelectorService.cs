using System;
using System.Collections.Generic;
using System.Text;
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

        var hwnd = ((MauiWinUIWindow)App.Current.Windows[0].Handler.PlatformView).WindowHandle;
        InitializeWithWindow.Initialize(picker, hwnd);

        picker.ViewMode = PickerViewMode.Thumbnail;
        picker.FileTypeFilter.Add(".csv");

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
#endif