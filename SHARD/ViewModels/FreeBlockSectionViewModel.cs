using System.Collections.Generic;
using System.Linq;
using SHARD.Core.Records;

namespace SHARD.ViewModels;

public sealed class FreeBlockSectionViewModel
{
    public string Header { get; }
    public IReadOnlyList<InfoRow> Rows { get; }
    public int ByteOffset { get; }
    public IReadOnlyList<FreeBlockRecordEntry> RecoveredRecords { get; }
    public bool HasRecoveredRecords => RecoveredRecords.Count > 0;

    public FreeBlockSectionViewModel(PageFreeBlock block, int index, IEnumerable<BTreeLeafCell>? freeblockCells = null)
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

        if (freeblockCells is not null)
        {
            int blockEnd = (int)(block.PageOffset + block.BlockSize);
            RecoveredRecords = freeblockCells
                .Where(c => c.PageOffset >= (int)block.PageOffset && c.PageOffset < blockEnd)
                .Select(c => new FreeBlockRecordEntry(
                    c.RowId.Value >= 0 ? $"Row {c.RowId.Value}" : $"Offset 0x{c.PageOffset:X4}",
                    c.PageOffset))
                .ToList();
        }
        else
        {
            RecoveredRecords = [];
        }
    }
}
