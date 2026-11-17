using Avalonia.Controls;
using Avalonia.Interactivity;
using SHARD.ViewModels;

namespace SHARD.Views;

public partial class RecoveryResultWindow : Window
{
    /// <summary>For the XAML loader and designer; the app uses the constructor below.</summary>
    public RecoveryResultWindow() => InitializeComponent();

    public RecoveryResultWindow(RecoveryResultViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnAddClicked(object? sender, RoutedEventArgs e)  => Close(true);
    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close(false);
}
