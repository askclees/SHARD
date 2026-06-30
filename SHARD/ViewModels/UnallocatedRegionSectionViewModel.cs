using SHARD.Core.Analysis;
using SHARD.Core.Records;

namespace SHARD.ViewModels;

public sealed class UnallocatedRegionSectionViewModel
{
    public string Header { get; }
    public IReadOnlyList<InfoRow> Rows { get; }
    public int ByteOffset { get; }
    public int Size { get; }
    public int NonZeroBytes { get; }

    public UnallocatedRegionSectionViewModel(PageUnallocatedRegion region, int index)
    {
        ByteOffset   = region.Offset;
        Size         = region.Size;
        NonZeroBytes = region.NonZeroBytes;
        Header     = $"Unallocated Region {index}  —  Offset: {region.Offset},  Size: {region.Size} bytes";

        Rows = new List<InfoRow>
        {
            new("Offset",          $"{region.Offset} (0x{region.Offset:X4})"),
            new("Size",            $"{region.Size} bytes"),
            new("Non-zero bytes",  $"{region.NonZeroBytes}"),
        };
    }
}
