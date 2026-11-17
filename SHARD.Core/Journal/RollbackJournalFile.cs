using SHARD.Core.Enums;

namespace SHARD.Core.Journal;

public class RollbackJournalFile
{
    public List<RollbackJournalSegment> Segments { get; } = new();
    //why parsing stopped after the last segment (null if it ran exactly to the end of the file)
    public string? StopReason { get; private set; }

    /// <summary>True when the header was zeroed and its values were inferred (see
    /// <see cref="ZeroedHeaderJournalReader"/>); each segment is then one transaction's records,
    /// grouped by checksum nonce, with an inferred header.</summary>
    public bool HeaderInferred { get; }

    /// <summary>How the header's values were inferred, when <see cref="HeaderInferred"/>.</summary>
    public string? Inference { get; }

    internal RollbackJournalFile(List<RollbackJournalSegment> segments, string inference, string? stopReason)
    {
        Segments.AddRange(segments);
        HeaderInferred = true;
        Inference = inference;
        StopReason = stopReason;
    }

    public RollbackJournalFile(string path, TextEncoding encoding, int reservedBytes)
    {
        using FileStream journalFile = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        //file is truncated after commit
        if (journalFile.Length < 28)
            throw new InvalidDataException("Journal file is too short to contain a header.");

        //the first segment must be valid - if it isn't, this isn't a (live) journal
        RollbackJournalSegment first = new(journalFile, 0, encoding, reservedBytes, false);
        Segments.Add(first);
        long offset = first.SegmentEnd;
        bool playbackEnded = first.PlaybackEnded;

        while (offset < journalFile.Length)
        {
            try
            {
                RollbackJournalSegment segment = new(journalFile, offset, encoding, reservedBytes, playbackEnded, first.Header);
                Segments.Add(segment);
                offset = segment.SegmentEnd;
                playbackEnded = segment.PlaybackEnded;
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                //no further valid header - normal end of the journal, anything after is slack
                StopReason = $"No valid segment header at offset {offset}: {ex.Message}";
                break;
            }
        }
    }
}
