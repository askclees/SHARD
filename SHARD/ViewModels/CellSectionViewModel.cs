using System.Collections.Generic;
using SHARD.Core.Records;

namespace SHARD.ViewModels;

public sealed class CellSectionViewModel
{
    public string Header { get; }
    public IReadOnlyList<InfoRow> Rows { get; }

    public CellSectionViewModel(BTreeLeafCell cell, int index)
    {
        Header = $"Cell {index}  —  RowId: {cell.RowId.Value}";

        var rows = new List<InfoRow>
        {
            new("Payload Size", $"{cell.SizeOfPayload.Value} bytes"),
            new("RowId",        $"{cell.RowId.Value}"),
            new("Header Size",  $"{cell.HeaderSize.Value} bytes"),
        };

        for (int i = 0; i < cell.HeaderEntries.Count; i++)
        {
            var entry = cell.HeaderEntries[i];
            rows.Add(new InfoRow($"Column {i}", $"{entry.Kind}  ({entry.ContentLength} bytes)"));
        }

        Rows = rows;
    }
}
