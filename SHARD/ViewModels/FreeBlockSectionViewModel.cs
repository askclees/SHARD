using System.Collections.Generic;
using SHARD.Core.Records;

namespace SHARD.ViewModels;

public sealed class FreeBlockSectionViewModel
{
    public string Header { get; }
    public IReadOnlyList<InfoRow> Rows { get; }
    public int ByteOffset { get; }

    public FreeBlockSectionViewModel(PageFreeBlock block, int index)
    {
        ByteOffset = (int)block.PageOffset;
        Header     = $"Freeblock {index}  —  Offset: {block.PageOffset},  Size: {block.BlockSize} bytes";

        Rows = new List<InfoRow>
        {
            new("Offset",   $"{block.PageOffset} (0x{block.PageOffset:X4})"),
            new("Size",     $"{block.BlockSize} bytes  ({block.BlockSize - 4} usable)"),
            new("Next",     block.NextFreeblockPageOffset == 0
                                ? "None"
                                : $"{block.NextFreeblockPageOffset} (0x{block.NextFreeblockPageOffset:X4})"),
        };
    }
}
