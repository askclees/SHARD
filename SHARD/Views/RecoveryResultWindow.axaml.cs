using Avalonia.Controls;
using Avalonia.Interactivity;
using SHARD.ViewModels;

namespace SHARD.Views;

public partial class RecoveryResultWindow : Window
{
    public RecoveryResultWindow(RecoveryResultViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnAddClicked(object? sender, RoutedEventArgs e)  => Close(true);
    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close(false);
}
