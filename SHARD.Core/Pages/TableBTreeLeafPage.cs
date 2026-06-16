using System.Buffers.Binary;
using SHARD.Core.Decoding;
using SHARD.Core.Enums;
using SHARD.Core.Records;

namespace SHARD.Core.Pages;

public sealed class TableBTreeLeafPage : BTreeLeafPage
{
    public override PageType PageType => PageType.BTreeLeafTable;

    public List<BTreeLeafCell> Cells { get; } = new();

    public TableBTreeLeafPage(uint pageNumber, int pageSize, byte[] data, TextEncoding encoding, int reservedBytes)
        : base(pageNumber, pageSize, data)
    {
        foreach (int i in this.CellPointers)
        {
            var varintData = Varint.ReadAt(data, i);
            var rowId = Varint.ReadAt(data, i + varintData.Length);
            int varlengths = varintData.Length + rowId.Length;

            int localSize = PayloadSizeCalculator.GetLocalPayloadSize(varintData.Value, pageSize, reservedBytes);
            bool hasOverflow = localSize < varintData.Value;

            var recordData = data.AsSpan(i..(i + varlengths + localSize));
            uint overflowPage = hasOverflow
                ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i + varlengths + localSize, 4))
                : 0;

            var leafCell = new BTreeLeafCell(recordData.ToArray(), varintData, encoding, overflowPage);
            Cells.Add((leafCell));
        }

    }


}
