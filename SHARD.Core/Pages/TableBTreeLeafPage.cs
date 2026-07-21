using System.Buffers.Binary;
using SHARD.Core.Analysis;
using SHARD.Core.Comparison;
using SHARD.Core.Decoding;
using SHARD.Core.Enums;
using SHARD.Core.Records;

namespace SHARD.Core.Pages;

public sealed class TableBTreeLeafPage : BTreeLeafPage
{
    public override PageType PageType => PageType.BTreeLeafTable;
    public List<BTreeLeafCell> Cells { get; } = new();
    public List<PageFreeBlock> FreeBlocks { get; } = new();
    public List<PageUnallocatedRegion> UnallocatedRegions { get; } = new();

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

            var leafCell = new BTreeLeafCell(recordData.ToArray(), varintData, encoding, i, overflowPage);
            Cells.Add((leafCell));
        }

        FreeBlocks = MapFreeList(data);
        UnallocatedRegions = MapUnallocatedSpace(data, HeaderOffset);
    }

    private List<PageFreeBlock> MapFreeList(byte[] data)
    {
        if (this.FirstFreeblock == 0)
        {
            return new List<PageFreeBlock>();
        }
        return ExtractFreeBlock(data, this.FirstFreeblock);
    }

    private List<PageUnallocatedRegion> MapUnallocatedSpace(byte[] data, int headerOffset = 0)
    {
        List<PageUnallocatedRegion> retVal = new();
        int headerSize = headerOffset + 8 + (Cells.Count * 2);
        //new region for space between header and cellcontent start area/first cell
        int cellStartArea = CellContentAreaStart == 0 ? 65536 : CellContentAreaStart;
        //order cells by page offset
        List<BTreeLeafCell> cellsInOrder = Cells.OrderBy(x => x.PageOffset).ToList();
        int firstLiveCell = cellsInOrder.Count > 0 ? cellsInOrder.First().PageOffset : cellStartArea;
        if (headerSize < firstLiveCell)
        {
            retVal.Add(MapRegion(data[headerSize..(firstLiveCell)], headerSize));
        }

        if (cellsInOrder.Count > 0)
        for (int i = 0; i < cellsInOrder.Count; i++)
        {
            BTreeLeafCell current = cellsInOrder[i];
            int currentCellSize = (int)current.CellByteLengthOnPage;
            int nextCellOffset;
            //if no more cells, check till end of page
            if (i + 1 >= cellsInOrder.Count)
            {
                nextCellOffset = PageSize;
            }
            else
            {
                nextCellOffset = cellsInOrder[i + 1].PageOffset;
            }

            if (current.PageOffset + currentCellSize < nextCellOffset)
            {
                int gapStart = current.PageOffset + currentCellSize;
                retVal.Add(MapRegion(data[gapStart..nextCellOffset], gapStart));
            }
        }
        return retVal;
    }

    
    private PageUnallocatedRegion MapRegion(ReadOnlySpan<byte> data, int startOffset)
    {
        int nonzeroBytes = 0;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != 0x00)
            {
                nonzeroBytes++;
            }
        }

        return new PageUnallocatedRegion(startOffset, data.Length,nonzeroBytes, data);
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

    public TableBTreeLeafPageComparison Compare(TableBTreeLeafPage comparePage)
    {
        TableBTreeLeafPageComparison retVal = new();
        List<long> thisRecords = Cells.Select(x => x.RowId.Value).ToList();
        List<long> compareRecords = comparePage.Cells.Select(x => x.RowId.Value).ToList();
        List<long> removedIds = RecordDifference(thisRecords, compareRecords);
        List<long> addedIds = RecordDifference(compareRecords, thisRecords);
        retVal.AddedRecords = comparePage.Cells.Where(x => addedIds.Contains(x.RowId.Value)).ToList();
        retVal.RemovedRecords = Cells.Where(x => removedIds.Contains(x.RowId.Value)).ToList();
        //compare records to see if any modified
        foreach (var recordId in thisRecords)
        {
            if (compareRecords.Contains((recordId)))
            {
                var firstRecord = Cells.FirstOrDefault(x => x.RowId.Value == recordId);
                var secondRecord = comparePage.Cells.FirstOrDefault(x => x.RowId.Value == recordId);
                if (firstRecord != null && secondRecord != null)
                {
                    var comparison = firstRecord.Compare(secondRecord);
                    if (comparison.HasChanges)
                    {
                        retVal.UpdatedRecords.Add(comparison);
                    }
                }
            }
        }
        return retVal;
    }

    //This function finds all records in List 1 that are not in List 2 and returns them as a list.
    //List order can be reversed when calling to get both (i.e. added/deleted)
    private List<long> RecordDifference(List<long> list1, List<long> list2)
    {
        return list1.Where(x => !list2.Contains(x)).ToList();
    }

}
