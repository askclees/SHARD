using SHARD.Core.Enums;

namespace SHARD.Core.WAL;

public class WalFile
{
    public WalHeader Header { get; }
    public List<WalFrame> Frames { get;} = new ();

    public WalFrame? GetPreviousFrame(WalFrame frame)
    {
        int idx = Frames.IndexOf(frame);
        if (idx <= 0) return null;
        return GetLastFrameForPage(frame.Header.PageNumber, idx);
    }

    /// <summary>
    /// The last frame carrying <paramref name="pageNumber"/> strictly before <paramref name="beforeIndex"/>
    /// — the baseline to diff a page against as of that point in the WAL (e.g. immediately
    /// before a given transaction started, rather than immediately before one specific frame).
    /// </summary>
    public WalFrame? GetLastFrameForPage(uint pageNumber, int beforeIndex) =>
        Frames.Take(beforeIndex).LastOrDefault(f => f.Header.PageNumber == pageNumber);

    /// <summary>
    /// The index of the first frame in the same transaction as <paramref name="frame"/> — i.e.
    /// just after the previous commit frame (one whose header declares a non-zero database
    /// size), or 0 if <paramref name="frame"/> is in the WAL's first transaction.
    /// </summary>
    public int GetTransactionStartIndex(WalFrame frame)
    {
        int idx = Frames.IndexOf(frame);
        if (idx < 0) return -1;

        for (int i = idx - 1; i >= 0; i--)
            if (Frames[i].Header.SizeOfDatabaseInPages > 0) return i + 1;
        return 0;
    }

    /// <summary>
    /// Every frame in the same transaction as <paramref name="frame"/> — the contiguous run
    /// from <see cref="GetTransactionStartIndex"/> through the next commit frame at or after
    /// <paramref name="frame"/>, or through the end of the file if the WAL is truncated
    /// mid-transaction (no terminating commit frame present).
    /// </summary>
    public IReadOnlyList<WalFrame> GetTransactionFrames(WalFrame frame)
    {
        int idx = Frames.IndexOf(frame);
        if (idx < 0) return [];

        int start = GetTransactionStartIndex(frame);

        int end = Frames.Count - 1;
        for (int i = idx; i < Frames.Count; i++)
        {
            if (Frames[i].Header.SizeOfDatabaseInPages > 0) { end = i; break; }
        }

        return Frames.GetRange(start, end - start + 1);
    }

    public WalFile(string path, TextEncoding encoding, int reservedBytes)
    {
        using FileStream walFile = File.Open(path, FileMode.Open);

        byte[] headerData = new byte[32];
        walFile.ReadExactly(headerData, 0, 32);

        Header = new WalHeader(headerData.AsSpan());
        long fileSize = walFile.Length;
        long pointer = 32;
        uint pageSize = Header.DatabasePageSize;
        byte[] frameData = new byte[24 + pageSize];
        while (pointer < fileSize)
        {
            try
            {
                walFile.ReadExactly(frameData, 0, (int)(24 + pageSize));
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException($"WAL file has a partial frame at offset {pointer} — file may be corrupted");
            }

            WalFrame newFrame = new(frameData, pageSize, encoding, reservedBytes, Header.VerificationData);
            Frames.Add(newFrame);
            pointer += pageSize + 24;
        }
    }
}