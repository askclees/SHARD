using Avalonia.Controls;
using Avalonia.Interactivity;
using SHARD.Controls;

namespace SHARD.Views;

public partial class DataInspectorWindow : Window
{
    public DataInspectorControl Inspector => InspectorControl;

    /// <summary>For the XAML loader and designer; the app uses the constructor below.</summary>
    public DataInspectorWindow() => InitializeComponent();

    public DataInspectorWindow(string title)
    {
        InitializeComponent();
        Title = title;
    }

    private void OnDockClick(object? sender, RoutedEventArgs e) => Close();
}
