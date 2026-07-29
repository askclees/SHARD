using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SHARD.ViewModels;

namespace SHARD.Views;

public partial class QueryWindow : Window
{
    public QueryWindow(QueryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.ResultsUpdated += (_, _) => RebuildColumns(vm);
    }

    private void RebuildColumns(QueryViewModel vm)
    {
        var grid = this.FindControl<DataGrid>("ResultsGrid");
        if (grid is null) return;
        grid.Columns.Clear();
        for (int i = 0; i < vm.ColumnNames.Count; i++)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header  = vm.ColumnNames[i],
                Binding = new Binding($"[{i}]"),
            });
        }
    }

    private void OnQueryBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var vm = (QueryViewModel)DataContext!;
        if (e.KeyModifiers == KeyModifiers.Shift)
        {
            if (sender is TextBox tb)
            {
                int pos = tb.CaretIndex;
                tb.Text = (tb.Text ?? string.Empty).Insert(pos, "\n");
                tb.CaretIndex = pos + 1;
            }
        }
        else
        {
            vm.RunQueryCommand.Execute(default).Subscribe();
        }
        e.Handled = true;
    }

    private async void OnExportCsvClick(object? sender, RoutedEventArgs e)
    {
        var vm = (QueryViewModel)DataContext!;
        var file = await (TopLevel.GetTopLevel(this)?.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title                = "Export Query Results as CSV",
                SuggestedFileName    = "query_results.csv",
                FileTypeChoices      = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
            }) ?? Task.FromResult<IStorageFile?>(null));

        if (file is null) return;
        string csv = vm.BuildCsv();
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8);
        await writer.WriteAsync(csv);
    }

    private void OnTableDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: QueryTableViewModel table })
            ((QueryViewModel)DataContext!).RunQueryForTable(table.ActualName);
    }

    private void OnQueryResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid { SelectedItem: QueryResultRow row }) return;
        ((QueryViewModel)DataContext!).RequestNavigationFromRow(row);
    }
}
