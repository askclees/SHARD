using System.Buffers.Binary;
using SHARD.Core.Decoding;
using SHARD.Core.Enums;
using SHARD.Core.Records;

namespace SHARD.Core.Pages;

public sealed class IndexBTreeLeafPage : BTreeLeafPage
{
    public override PageType PageType => PageType.BTreeLeafIndex;

    public List<IndexBTreeLeafCell> Cells { get; } = new();

    public IndexBTreeLeafPage(uint pageNumber, int pageSize, byte[] data, TextEncoding encoding, int reservedBytes)
        : base(pageNumber, pageSize, data)
    {
        foreach (int i in this.CellPointers)
        {
            var payloadSize = Varint.ReadAt(data, i);
            int localSize = PayloadSizeCalculator.GetIndexLocalPayloadSize(payloadSize.Value, pageSize, reservedBytes);
            bool hasOverflow = localSize < payloadSize.Value;

            var cellData = data[i..(i + payloadSize.Length + localSize)];
            uint overflowPage = hasOverflow
                ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i + payloadSize.Length + localSize, 4))
                : 0;

            Cells.Add(new IndexBTreeLeafCell(cellData, payloadSize, encoding, overflowPage));
        }
    }
}
