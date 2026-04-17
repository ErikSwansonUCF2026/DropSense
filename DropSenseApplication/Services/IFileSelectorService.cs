using System;
using System.Collections.Generic;
using System.Text;


namespace DropSense.Services
{
    public interface IFileSelectorService
    {
        Task<string?> PickCsvFileAsync(CancellationToken ct = default);
    }
}

