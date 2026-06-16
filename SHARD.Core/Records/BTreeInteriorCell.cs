namespace SHARD.Core.Records;

public class BTreeInteriorCell
{
    public uint PageNumber { get;}
    public long RecordId { get; }

    public BTreeInteriorCell(uint pageNum, long recordId)
    {
        this.PageNumber = pageNum;
        this.RecordId = recordId;
    }
    
}