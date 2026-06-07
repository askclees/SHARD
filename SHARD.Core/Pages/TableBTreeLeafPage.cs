using SHARD.Core.Decoding;
using SHARD.Core.Enums;
using SHARD.Core.Records;

namespace SHARD.Core.Pages;

public sealed class TableBTreeLeafPage : BTreeLeafPage
{
    public override PageType PageType => PageType.BTreeLeafTable;

    public List<BTreeLeafCell> Cells { get; } = new();

    public TableBTreeLeafPage(uint pageNumber, int pageSize, byte[] data)
        : base(pageNumber, pageSize, data)
    {
        foreach (int i in this.CellPointers)
        {
            var varintData = new Varint(data.AsSpan(i..(i + 9)));
            var rowId = new Varint(data.AsSpan((i + varintData.Length).. (i + varintData.Length + 9)));
            int varlengths = varintData.Length + rowId.Length;
            var recordData = data.AsSpan(i..(int)(i + varintData.Value+varlengths));
            var leafCell = new BTreeLeafCell(recordData.ToArray(), varintData);
            Cells.Add((leafCell));
        }
        
    }
    
    
}
