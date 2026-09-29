using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SHARD.BlobViewers;
using SHARD.Core.BlobViewers;

namespace SHARD.Views;

/// <summary>
/// Renders a BLOB value via the first matching plugin in <see cref="BlobViewerRegistryFactory.Shared"/>
/// (see SHARD.Core.BlobViewers for the plugin protocol). "Save As" always exports the original,
/// untranscoded bytes passed to this window — never a plugin's decoded preview output — so a
/// forensic export never silently substitutes re-encoded data for the actual recovered bytes.
/// </summary>
public partial class BlobViewerWindow : Window
{
    private readonly byte[] _originalData;

    public BlobViewerWindow(byte[] data)
    {
        InitializeComponent();
        _originalData = data;

        this.FindControl<Button>("SaveAsButton")!.Click += OnSaveAsClick;
        this.FindControl<Button>("CloseButton")!.Click  += (_, _) => Close();

        Render(data);
    }

    private void Render(byte[] data)
    {
        var host   = this.FindControl<Border>("ContentHost")!;
        var status = this.FindControl<TextBlock>("StatusText")!;

        var plugin = BlobViewerRegistryFactory.Shared.FindHandler(data);
        if (plugin is null)
        {
            host.Child = Placeholder("No viewer plugin recognizes this data.");
            status.Text = $"{data.Length:N0} bytes  ·  no matching plugin";
            return;
        }

        var result = plugin.Decode(data);
        if (result is null)
        {
            host.Child = Placeholder($"'{plugin.Manifest.Name}' matched but failed to decode this blob.");
            status.Text = $"{data.Length:N0} bytes  ·  {plugin.Manifest.Name} (decode failed)";
            return;
        }

        host.Child = result.Kind switch
        {
            BlobViewerKind.Image        => RenderImage(result.Payload),
            BlobViewerKind.Text         => RenderText(result.Payload),
            BlobViewerKind.Html         => RenderText(result.Payload), // no HTML renderer yet — shown as text
            BlobViewerKind.Audio        => Placeholder(
                $"'{plugin.Manifest.Name}' recognized this as audio, which SHARD doesn't play inline yet. Use Save As to export it."),
            _                            => Placeholder(
                $"'{plugin.Manifest.Name}' recognized this data but has no inline preview for it. Use Save As to export it."),
        };

        status.Text = $"{data.Length:N0} bytes  ·  {plugin.Manifest.Name}  ·  {result.Kind}";
    }

    private static Control RenderImage(byte[] payload)
    {
        using var stream = new MemoryStream(payload);
        return new Image
        {
            Source = new Bitmap(stream),
            Stretch = Stretch.Uniform,
        };
    }

    private static Control RenderText(byte[] payload) => new ScrollViewer
    {
        Content = new TextBlock
        {
            Text = Encoding.UTF8.GetString(payload),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = "Courier New",
            FontSize = 12,
        },
    };

    private static Control Placeholder(string message) => new TextBlock
    {
        Text = message,
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.6,
        Margin = new Avalonia.Thickness(20),
    };

    private async void OnSaveAsClick(object? sender, RoutedEventArgs e)
    {
        var file = await (TopLevel.GetTopLevel(this)?.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions { Title = "Save Blob As", SuggestedFileName = "blob.bin" })
            ?? Task.FromResult<IStorageFile?>(null));

        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(_originalData);
    }
}
