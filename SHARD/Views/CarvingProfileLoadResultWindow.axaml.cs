using Avalonia.Controls;
using Avalonia.Interactivity;
using SHARD.ViewModels;

namespace SHARD.Views;

public partial class CarvingProfileLoadResultWindow : Window
{
    /// <summary>For the XAML loader and designer; the app uses the constructor below.</summary>
    public CarvingProfileLoadResultWindow() => InitializeComponent();

    public CarvingProfileLoadResultWindow(CarvingProfileLoadResultViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
