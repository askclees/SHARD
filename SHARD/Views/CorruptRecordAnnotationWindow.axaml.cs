using Avalonia.Controls;
using Avalonia.Interactivity;
using SHARD.ViewModels;

namespace SHARD.Views;

public partial class CorruptRecordAnnotationWindow : Window
{
    public CorruptRecordAnnotationWindow(CorruptRecordAnnotationViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private CorruptRecordAnnotationViewModel Vm => (CorruptRecordAnnotationViewModel)DataContext!;

    private void OnDecodeClicked(object? sender, RoutedEventArgs e) => Vm.Decode();

    private void OnRegisterSchemaClicked(object? sender, RoutedEventArgs e)
    {
        Vm.WantToRegisterSchema = true;
        Close(false); // save-to-project not triggered; parent uses WantToRegisterSchema
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close(false);
}
