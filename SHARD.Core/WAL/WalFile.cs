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

    /// <summary>File offset where bytes too few to form a whole frame begin, or null if the file
    /// ends exactly on a frame boundary.</summary>
    public long? TrailingBytesOffset { get; }

    /// <summary>The bytes after the last whole frame (empty if none). Usually a frame that was
    /// being written when the file was copied or the writer stopped; SQLite ignores them.</summary>
    public byte[] TrailingBytes { get; } = [];

    /// <summary>Why parsing stopped before the end of the file (null if it ran exactly to the end).</summary>
    public string? StopReason { get; }

    public bool HasTrailingBytes => TrailingBytes.Length > 0;

    public WalFile(string path, TextEncoding encoding, int reservedBytes)
    {
        using FileStream walFile = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        byte[] headerData = new byte[32];
        walFile.ReadExactly(headerData, 0, 32);

        Header = new WalHeader(headerData.AsSpan());
        long fileSize = walFile.Length;
        long pointer = 32;
        uint pageSize = Header.DatabasePageSize;
        long frameSize = 24 + pageSize;
        byte[] frameData = new byte[frameSize];
        while (pointer < fileSize)
        {
            long remaining = fileSize - pointer;
            if (remaining < frameSize)
            {
                // A partial frame — typically one being written when the file was copied. SQLite
                // stops reading at the last whole frame, so do the same and keep the rest for review.
                TrailingBytesOffset = pointer;
                TrailingBytes = new byte[remaining];
                walFile.ReadExactly(TrailingBytes, 0, (int)remaining);
                StopReason = $"{remaining:N0} byte{(remaining == 1 ? "" : "s")} at offset {pointer:N0} " +
                             $"are less than a whole frame ({frameSize:N0} bytes) and were ignored";
                break;
            }

            walFile.ReadExactly(frameData, 0, (int)frameSize);
            WalFrame newFrame = new(frameData, pageSize, encoding, reservedBytes, Header.VerificationData);
            Frames.Add(newFrame);
            pointer += frameSize;
        }
    }
}