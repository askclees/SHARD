using SHARD.Core.Enums;

namespace SHARD.Core.Journal;

public class RollbackJournalSegment
{
    public RollbackJournalHeader Header { get; }
    public List<RollbackJournalPageRecord> PageRecords { get;} = new ();
    public long SegmentStart { get;}
    public long SegmentEnd { get; }
    public bool PlaybackEnded { get; }
    private readonly uint _lockBytePage;

    /// <param name="firstHeader">
    /// Header of the journal's first segment, or null when this is the first segment. SQLite only
    /// reads the page and sector size from the first header and ignores them in later ones.
    /// </param>
    public RollbackJournalSegment(FileStream journalFile, long offset, TextEncoding encoding, int reservedBytes
        , bool playbackEnded, RollbackJournalHeader? firstHeader = null)
    {
        PlaybackEnded = playbackEnded;
        SegmentStart = offset;
        journalFile.Seek(offset, SeekOrigin.Begin);
        byte[] headerData = new byte[28];
        journalFile.ReadExactly(headerData, 0, 28);
        Header = new RollbackJournalHeader(headerData.AsSpan());
        RollbackJournalHeader sizeHeader = firstHeader ?? Header;
        int pageSize = sizeHeader.SizeOfJournalPages;
        int sectorSize = sizeHeader.SizeOfDiskSector;
        if (firstHeader is null)
        {
            ValidatePageSize(pageSize);
            ValidateSectorSize(sectorSize);
        }
        _lockBytePage = (uint)(0x40000000 / pageSize) + 1;
        
        //Header valid, move to start of next sector
        long recordStart = offset + sectorSize;
        journalFile.Seek(recordStart, SeekOrigin.Begin);
        
        //Start parsing
        uint entrySize = (uint)(pageSize + 8);
        long available = Math.Max(0, (journalFile.Length - recordStart) / entrySize) * entrySize;
        //0xFFFFFFFF means read to the end of the file; 0 means the count was never filled in,
        //so read whatever whole records are there rather than dropping them
        bool readToEnd = Header.PageCount == uint.MaxValue || Header.PageCount == 0;
        long dataSize = readToEnd
            ? available
            : Math.Min((long)Header.PageCount * entrySize, available);
        
        //read each record in this segment
        long pointer = recordStart;
        byte[] frameData = new byte[entrySize];
        while (pointer < dataSize + recordStart)
        {
            journalFile.ReadExactly(frameData, 0, (int)(entrySize));
            RollbackJournalPageRecord record = new(frameData, (uint)pageSize, encoding, reservedBytes, Header.Nonce, pointer);
            record.Status = GetJournalRecordStatus(record);
            if (record.Status != JournalRecordStatus.Valid)
            {
                PlaybackEnded = true;
            }

            PageRecords.Add(record);
            pointer += entrySize;
        }

        SegmentEnd = ((recordStart + dataSize + sectorSize - 1) / sectorSize) * sectorSize;
    }


    private static void ValidatePageSize(int pageSize)
    {
        //check page size is between 512 and 65536 bytes and a power of 2
        if (pageSize < 512 || pageSize > 65536 ||
            (pageSize & (pageSize- 1)) != 0)
        {
            throw new InvalidDataException($"Invalid page size: {pageSize}");
        }
    }
    
    private static void ValidateSectorSize(int sectorSize)
    {
        //check sector size is between 32 and 65536 bytes and a power of 2
        if (sectorSize < 32 || sectorSize > 65536 ||
            (sectorSize & (sectorSize- 1)) != 0)
        {
            throw new InvalidDataException($"Invalid sector size: {sectorSize}");
        }
    }
    
    private JournalRecordStatus GetJournalRecordStatus(RollbackJournalPageRecord record)
    {
        //SQLite stops playback at the first invalid record - nothing after it, in any segment, is replayed
        if (record.PageNumber == 0 || record.PageNumber == _lockBytePage)
        {
            return JournalRecordStatus.InvalidPageNumber;
        }
        else if (!record.IsChecksumValid)
        {
            return JournalRecordStatus.Stale;
        }
        else if (PlaybackEnded)
        {
            return JournalRecordStatus.AfterPlaybackStopped;
        }
        else
        {
            return JournalRecordStatus.Valid;
        }
    }
}