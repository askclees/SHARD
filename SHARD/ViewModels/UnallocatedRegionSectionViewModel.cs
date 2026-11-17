using ReactiveUI;
using SHARD.Core.Analysis;
using SHARD.Core.Records;

namespace SHARD.ViewModels;

public sealed class UnallocatedRegionSectionViewModel : ReactiveObject
{
    private readonly PageUnallocatedRegion _region;
    private readonly Func<PageUnallocatedRegion, ResidualCount>? _residual;

    public string Header { get; }
    public int ByteOffset { get; }
    public int Size { get; }

    /// <summary>Non-zero bytes when the page was read, before recovery (what the page filters use).</summary>
    public int NonZeroBytes { get; }

    /// <param name="residual">Counts what recovery leaves unexplained in the region now; null
    /// leaves that row out.</param>
    public UnallocatedRegionSectionViewModel(PageUnallocatedRegion region, int index,
        Func<PageUnallocatedRegion, ResidualCount>? residual = null)
    {
        _region      = region;
        _residual    = residual;
        ByteOffset   = region.Offset;
        Size         = region.Size;
        NonZeroBytes = region.NonZeroBytes;
        Header     = $"Unallocated Region {index}  —  Offset: {region.Offset},  Size: {region.Size} bytes";
    }

    public IReadOnlyList<InfoRow> Rows
    {
        get
        {
            var rows = new List<InfoRow>
            {
                new("Offset",          $"{_region.Offset} (0x{_region.Offset:X4})"),
                new("Size",            $"{_region.Size} bytes"),
                new("Non-zero bytes",  $"{_region.NonZeroBytes}"),
            };
            if (_residual is not null && _region.NonZeroBytes > 0)
            {
                var count = _residual(_region);
                rows.Add(new("Unexplained", $"{count.UnexplainedNonZeroBytes} of {count.NonZeroBytes} after recovery"));
            }
            return rows;
        }
    }

    /// <summary>Non-zero bytes recovery leaves unexplained now, or <see cref="NonZeroBytes"/> without a counter.</summary>
    public int UnexplainedNonZeroBytes => _residual?.Invoke(_region).UnexplainedNonZeroBytes ?? NonZeroBytes;

    /// <summary>Re-counts after recovered cells were added or removed.</summary>
    public void Refresh() => this.RaisePropertyChanged(nameof(Rows));
}
