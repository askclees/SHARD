using System.Buffers.Binary;
using SHARD.Core.Decoding;
using SHARD.Core.Enums;
using SHARD.Core.Records;

namespace SHARD.Core.Pages;

public sealed class TableBTreeInteriorPage : BTreeInteriorPage
{
    public override PageType PageType => PageType.BTreeInteriorTable;

    public List<BTreeInteriorCell> Cells { get; } = new();
    
    public TableBTreeInteriorPage(uint pageNumber, int pageSize, byte[] data)
        : base(pageNumber, pageSize, data)
    {
        foreach (var cell in this.CellPointers)
        {
            uint PageNumber = BinaryPrimitives.ReadUInt32BigEndian(data[cell..(cell + 4)].AsSpan());
            Varint RecordKey = Varint.ReadAt(data, cell + 4);
            Cells.Add(new BTreeInteriorCell(PageNumber, RecordKey.Value));
        }
    }
}
