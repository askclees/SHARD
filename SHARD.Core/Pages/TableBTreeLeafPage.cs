using System.Buffers.Binary;
using SHARD.Core.Decoding;
using SHARD.Core.Enums;
using SHARD.Core.Records;

namespace SHARD.Core.Pages;

public sealed class TableBTreeLeafPage : BTreeLeafPage
{
    public override PageType PageType => PageType.BTreeLeafTable;

    public List<BTreeLeafCell> Cells { get; } = new();
    public List<PageFreeBlock> FreeBlocks { get; } = new(); 

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

        FreeBlocks = MapFreeList(data);
    }

    private List<PageFreeBlock> MapFreeList(byte[] data)
    {
        if (this.FirstFreeblock == 0)
        {
            return new List<PageFreeBlock>();
        }
        return ExtractFreeBlock(data, this.FirstFreeblock);
    }

    private List<PageFreeBlock> ExtractFreeBlock(byte[] data, uint offset)
    {
        List<PageFreeBlock> retVal = new List<PageFreeBlock>();
        //check data won't go off end of page
        if (offset + 4 > data.Length)
        {
            return retVal;
        }
        uint nextFreeblock =  BinaryPrimitives.ReadUInt16BigEndian(data[(int)offset..(int)(offset + 2)].AsSpan());
        uint freeblockSize =  BinaryPrimitives.ReadUInt16BigEndian(data[(int)(offset+2)..(int)(offset + 4)].AsSpan());
        //Size must be greater than >= 4 and freeblocks are in offset order (i.e beginning to end of page)
        if (freeblockSize < 4 || (nextFreeblock <= offset && nextFreeblock != 0))
        {
            return retVal;
        }
        retVal.Add(new PageFreeBlock(offset, nextFreeblock,freeblockSize));
        if (nextFreeblock != 0)
        {
            //ensure we don't go past page edge
            if (nextFreeblock < data.Length)
            {
                retVal.AddRange(ExtractFreeBlock(data, nextFreeblock));
            }
        }
        return retVal;
    }


}
