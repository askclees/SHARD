using System.Buffers.Binary;
using SHARD.Core.Comparison;
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
    
    
    public TableBTreeInteriorPageComparison Compare(TableBTreeInteriorPage comparePage)
    {
        //Assume compare page is newest
        //Therefore existing in compare, but not This, added record
        //Exists in this but not compare, deleted
        TableBTreeInteriorPageComparison retVal = new();
        List<BTreeInteriorCell> matching = Cells.Where(x => comparePage.Cells.Any(y => y.PageNumber == x.PageNumber)).ToList();
        retVal.AddedRecords = comparePage.Cells.Where(x => !Cells.Any(y => y.PageNumber == x.PageNumber)).ToList();
        retVal.RemovedRecords = Cells.Where(x => !comparePage.Cells.Any(y => y.PageNumber == x.PageNumber)).ToList();
        foreach (var matchCell in matching)
        {
            if (comparePage.Cells.Any(x => x.PageNumber == matchCell.PageNumber && x.RecordId == matchCell.RecordId))
            {
                continue;
            }
            //difference
            retVal.UpdatedRecords.Add(new BTreeInteriorCellComparison()
            {
                PageNumber = matchCell.PageNumber,
                PreviousRecordId = matchCell.RecordId,
                NewRecordId = comparePage.Cells.Where(x => x.PageNumber == matchCell.PageNumber)
                    .Select(x => x.RecordId)
                    .First(),
            });
        }
        //check if right pointer has changed
        if (comparePage.RightmostPointer != this.RightmostPointer)
        {
            retVal.PreviousRightPointer = this.RightmostPointer;
            retVal.NewRightPointer = comparePage.RightmostPointer;
        }
        
        return retVal;
    }
    
    
    
}
