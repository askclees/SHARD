using Avalonia.Controls;
using Avalonia.Interactivity;
using SHARD.Settings;

namespace SHARD.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        this.FindControl<NumericUpDown>("ThresholdBox")!.Value = AppSettings.Current.LargeDatabaseThresholdMb;

        this.FindControl<Button>("SaveButton")!.Click   += OnSaveClick;
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        decimal? value = this.FindControl<NumericUpDown>("ThresholdBox")!.Value;
        int thresholdMb = value.HasValue ? (int)value.Value : AppSettings.Current.LargeDatabaseThresholdMb;
        if (thresholdMb < 1) thresholdMb = 1;

        new AppSettings { LargeDatabaseThresholdMb = thresholdMb }.Save();
        Close();
    }
}
