using Avalonia.Controls;
using Avalonia.Interactivity;
using SHARD.ViewModels;

namespace SHARD.Views;

public partial class CarvingProfileLoadResultWindow : Window
{
    public CarvingProfileLoadResultWindow(CarvingProfileLoadResultViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
