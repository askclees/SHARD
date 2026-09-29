using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using SHARD.ViewModels;

namespace SHARD.Views;

/// <summary>
/// Builds a query results <see cref="DataGrid"/>'s columns from a <see cref="QueryViewModel"/>'s
/// result shape — shared by both <see cref="MainWindow"/>'s embedded Query tab and the pop-out
/// <see cref="QueryWindow"/>, since neither can declare columns statically (the result shape is
/// arbitrary; see <see cref="QueryViewModel.ResultsUpdated"/>).
/// </summary>
internal static class QueryResultsGridHelper
{
    /// <summary>
    /// A BLOB-typed column (see <see cref="QueryViewModel.BlobColumnIndexes"/>) gets a "View…"
    /// button cell that opens a <see cref="BlobViewerWindow"/> instead of a plain text cell.
    /// </summary>
    public static void RebuildColumns(DataGrid grid, QueryViewModel queryTab, Window owner)
    {
        grid.Columns.Clear();

        for (int i = 0; i < queryTab.ColumnNames.Count; i++)
        {
            int columnIndex = i; // captured per-column below — must not be the shared loop variable
            string header = queryTab.ColumnNames[i];

            if (queryTab.BlobColumnIndexes.Contains(columnIndex))
            {
                grid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = header,
                    CellTemplate = new FuncDataTemplate<QueryResultRow>(
                        (row, _) => BuildBlobCell(row, columnIndex, owner)),
                });
            }
            else
            {
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = header,
                    Binding = new Binding($"[{columnIndex}]"),
                });
            }
        }
    }

    private static Control BuildBlobCell(QueryResultRow? row, int columnIndex, Window owner)
    {
        byte[]? blob = row?.GetBlob(columnIndex);

        var button = new Button
        {
            Content = blob is not null ? $"View ({blob.Length:N0} B)" : "NULL",
            IsEnabled = blob is not null,
            Padding = new Thickness(8, 2),
            FontSize = 11,
        };
        if (blob is not null)
            button.Click += (_, _) => new BlobViewerWindow(blob).Show(owner);

        return button;
    }
}
