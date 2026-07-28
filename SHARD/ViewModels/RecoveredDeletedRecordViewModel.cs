using System.Collections.Generic;
using SHARD.Core.Records;
using SHARD.Core.Schema;

namespace SHARD.ViewModels;

public sealed class RecoveredDeletedRecordViewModel
{
    public string Header { get; }
    public IReadOnlyList<InfoRow> Fields { get; }

    public RecoveredDeletedRecordViewModel(TableRow row, TableSchema? schema)
    {
        var fields = new List<InfoRow>();
        fields.Add(new InfoRow("rowid", row.RowId.ToString()));

        for (int i = 0; i < row.FieldValues.Count; i++)
        {
            string colName = i < (schema?.Columns.Count ?? 0)
                ? schema!.Columns[i].Name
                : $"col{i}";
            fields.Add(new InfoRow(colName, FormatValue(row.FieldValues[i])));
        }

        Fields = fields;
        Header = $"rowid={row.RowId}  —  page {row.PageNumber}, offset 0x{row.CellOffset:X}";
    }

    private static string FormatValue(SqliteValue? v)
    {
        if (v is null || v.IsNull)      return "NULL";
        if (v.TextValue    is not null) return v.TextValue;
        if (v.IntegerValue is not null) return v.IntegerValue.Value.ToString();
        if (v.RealValue    is not null) return v.RealValue.Value.ToString("G");
        if (v.BlobValue    is not null) return $"[blob {v.BlobValue.Length} bytes]";
        return "NULL";
    }
}
