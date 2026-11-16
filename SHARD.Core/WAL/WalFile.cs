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

    /// <summary>True when the checksum words are big-endian (magic 0x377f0683), false for little-endian (0x377f0682).</summary>
    public bool BigEndianChecksums => Header.MagicNumber == 0x377f0683;

    /// <summary>Whether the header's own checksum (over its first 24 bytes) is correct. When it
    /// isn't, SQLite ignores every frame in the WAL.</summary>
    public bool HeaderChecksumValid { get; }

    /// <summary>How many frames SQLite would read: those up to and including the last commit
    /// frame of the valid run (SQLite's mxFrame). Frames <c>[0, CommittedFrameCount)</c> are
    /// <see cref="WalFrameStatus.Committed"/>.</summary>
    public int CommittedFrameCount { get; }

    /// <summary>Frames SQLite would read, in order — the committed state of the WAL.</summary>
    public IEnumerable<WalFrame> CommittedFrames => Frames.Take(CommittedFrameCount);

    /// <summary>Why SQLite would stop reading the WAL before its last whole frame, or null if it
    /// reads every current-generation frame.</summary>
    public string? ValidationStopReason { get; private set; }

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

        var (h0, h1) = WalChecksum.Compute(headerData.AsSpan(0, 24), BigEndianChecksums);
        HeaderChecksumValid = h0 == Header.VerificationData.Checksum1 && h1 == Header.VerificationData.Checksum2;
        CommittedFrameCount = ValidateFrames();
    }

    /// <summary>
    /// Assigns every frame its <see cref="WalFrameStatus"/> and <see cref="WalFrame.ChecksumValid"/>
    /// the way SQLite's walIndexRecover reads a WAL: frames are valid while their salts match the
    /// header, their page number is non-zero and each checksum continues the running chain from
    /// the header; reading stops at the first frame that fails. Only frames up to the last commit
    /// frame of that valid run are used. Returns that count (SQLite's mxFrame).
    /// </summary>
    private int ValidateFrames()
    {
        var salts = Header.VerificationData;
        int committed = 0;
        int lastValid = -1;
        bool stopped = !HeaderChecksumValid;
        if (stopped)
            ValidationStopReason = "the WAL header checksum is invalid, so SQLite ignores every frame";

        for (int i = 0; i < Frames.Count; i++)
        {
            var frame = Frames[i];
            var fh = frame.Header;
            bool currentGeneration = fh.IsCurrent;

            // Chain seed: the header for the first frame, otherwise the previous frame's stored
            // checksum — identical to SQLite's running value for as long as the chain is valid.
            (uint S0, uint S1)? seed = null;
            if (i == 0)
            {
                if (currentGeneration) seed = (salts.Checksum1, salts.Checksum2);
            }
            else
            {
                var prev = Frames[i - 1].Header.VerificationData;
                if (prev.Salt1 == fh.VerificationData.Salt1 && prev.Salt2 == fh.VerificationData.Salt2)
                    seed = (prev.Checksum1, prev.Checksum2);
            }

            if (seed is { } s)
            {
                var (c0, c1) = WalChecksum.ComputeFrame(fh.PageNumber, fh.SizeOfDatabaseInPages, frame.PageData, BigEndianChecksums, s.S0, s.S1);
                frame.ChecksumValid = c0 == fh.VerificationData.Checksum1 && c1 == fh.VerificationData.Checksum2;
            }
            else
            {
                frame.ChecksumValid = null;
            }

            if (!currentGeneration)
            {
                frame.Status = WalFrameStatus.OldGeneration;
                if (!stopped)
                {
                    stopped = true;
                    ValidationStopReason = $"frame {i + 1} has salts from an earlier generation of the WAL";
                }
                continue;
            }

            if (stopped)
            {
                frame.Status = WalFrameStatus.AfterInvalid;
                continue;
            }

            if (fh.PageNumber == 0)
            {
                frame.Status = WalFrameStatus.InvalidPageNumber;
                stopped = true;
                ValidationStopReason = $"frame {i + 1} has page number 0";
                continue;
            }

            if (frame.ChecksumValid != true)
            {
                frame.Status = WalFrameStatus.InvalidChecksum;
                stopped = true;
                ValidationStopReason = $"frame {i + 1}'s checksum doesn't continue the chain";
                continue;
            }

            lastValid = i;
            if (fh.SizeOfDatabaseInPages > 0) committed = i + 1;
        }

        // Valid frames after the last commit frame belong to a transaction that never committed.
        for (int i = 0; i <= lastValid; i++)
            Frames[i].Status = i < committed ? WalFrameStatus.Committed : WalFrameStatus.Uncommitted;

        return committed;
    }
}