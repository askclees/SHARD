using System.Collections.Generic;
using SHARD.Core.Enums;
using SHARD.Core.Records;

namespace SHARD.ViewModels;

public sealed class CellSectionViewModel
{
    public string Header { get; }
    public IReadOnlyList<InfoRow> Rows { get; }
    public int ByteOffset { get; }

    public CellSectionViewModel(BTreeLeafCell cell, int index, int byteOffset)
    {
        ByteOffset = byteOffset;
        Header = $"Cell {index}  —  RowId: {cell.RowId.Value}";

        var rows = new List<InfoRow>
        {
            new("Payload Size", $"{cell.SizeOfPayload.Value} bytes"),
            new("RowId",        $"{cell.RowId.Value}"),
            new("Header Size",  $"{cell.HeaderSize.Value} bytes"),
        };

        AddFieldRows(rows, cell.HeaderEntries, cell.FieldValues);
        Rows = rows;
    }

    public CellSectionViewModel(IndexBTreeLeafCell cell, int index, int byteOffset)
    {
        ByteOffset = byteOffset;
        Header = $"Cell {index}";

        var rows = new List<InfoRow>
        {
            new("Payload Size", $"{cell.SizeOfPayload.Value} bytes"),
            new("Header Size",  $"{cell.HeaderSize.Value} bytes"),
        };

        AddFieldRows(rows, cell.HeaderEntries, cell.FieldValues);
        Rows = rows;
    }

    private static void AddFieldRows(List<InfoRow> rows, List<HeaderEntry> entries, List<SqliteValue?> values)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var sv    = i < values.Count ? values[i] : null;

            string valStr = sv?.Value?.ToString()
                ?? (entry.Kind == SerialTypeKind.Null ? "NULL" : "—");

            rows.Add(new InfoRow($"Column {i}", $"{entry.Kind}  ({entry.ContentLength} bytes)  =  {valStr}"));
        }
    }
}
