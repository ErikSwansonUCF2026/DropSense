using System;
using System.Collections.Generic;
using System.Text;

namespace DropSense.Services;
public interface IFileSessionService
{
    string? ActiveFileName { get; }
    string? ActiveFilePath { get; }

    event EventHandler? FileChanged;

    void SetActiveFile(string fullPath);
}

public class FileSessionService : IFileSessionService
{
    public string? ActiveFileName { get; private set; }
    public string? ActiveFilePath { get; private set; }

    public event EventHandler? FileChanged;


    public void SetActiveFile(string fullPath)
    {
        ActiveFilePath = fullPath;
        ActiveFileName = Path.GetFileName(fullPath);
        FileChanged?.Invoke(this, EventArgs.Empty);

    }
}