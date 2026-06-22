using System;
using System.Collections.Generic;
using System.Text;

namespace DropSense.Models;

public class ProcessDataResult
{
    public string StatsCsvPath { get; init; } = string.Empty;
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<StatRow> Stats { get; init; } = [];
    public int RowsProcessed { get; init; }
    public int RowsSkipped { get; init; }
    public IReadOnlyList<SensorRow> Rows { get; init; } = [];

}

public class StatRow
{
    public string Label { get; init; } = string.Empty;
    public Dictionary<string, double?> Values { get; init; } = new();
}

public class XlsxResult
{
    public string XlsxPath { get; init; } = string.Empty;
    public int SheetsWritten { get; init; }
    public int ColumnsExported { get; init; }
    public int StatsExported { get; init; }
    public int RowsProcessed { get; init; }
    public int RowsSkipped { get; init; }
}