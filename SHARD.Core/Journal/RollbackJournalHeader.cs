using System.Buffers.Binary;

namespace SHARD.Core.Journal;

public class RollbackJournalHeader
{
    private static readonly byte[] ExpectedHeader = new byte[] {
        0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7
    };

    //unsigned in SQLite - 0xFFFFFFFF means read records to the end of the file
    public uint PageCount { get; }
    public uint Nonce { get; }
    public int DatabaseSize { get; }
    public int SizeOfDiskSector { get; }
    public int SizeOfJournalPages { get; }

    public RollbackJournalHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 28 || !data.StartsWith(ExpectedHeader))
        {
            throw new InvalidDataException("Not a valid rollback journal header.");
        }
        
        int pointer = 8;
        PageCount = BinaryPrimitives.ReadUInt32BigEndian(data[pointer..(pointer+4)]);
        pointer += 4;
        Nonce = BinaryPrimitives.ReadUInt32BigEndian(data[pointer..(pointer+4)]);
        pointer += 4;
        DatabaseSize = BinaryPrimitives.ReadInt32BigEndian(data[pointer..(pointer+4)]);
        pointer += 4;
        SizeOfDiskSector = BinaryPrimitives.ReadInt32BigEndian(data[pointer..(pointer+4)]);
        pointer += 4;
        SizeOfJournalPages = BinaryPrimitives.ReadInt32BigEndian(data[pointer..(pointer + 4)]);
        
    }
    
}