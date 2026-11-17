namespace SHARD.Core.Enums;

public enum JournalRecordStatus
{
    /// <summary>Checksum matches its segment's nonce and playback has not stopped — SQLite would play this record back.</summary>
    Valid,

    /// <summary>Checksum matches its segment's nonce, but an earlier record (in this or an earlier segment) stopped playback, so SQLite would never apply it.</summary>
    AfterPlaybackStopped,

    /// <summary>Checksum does not match its segment's nonce — left over from an earlier transaction, or not a record at all.</summary>
    Stale,

    /// <summary>Page number is 0 or the lock-byte page, which SQLite never journals.</summary>
    InvalidPageNumber,

    /// <summary>From a journal whose header SQLite zeroed when the transaction committed
    /// (journal_mode=PERSIST), read with the header's values inferred — see
    /// <see cref="Journal.ZeroedHeaderJournalReader"/>. The transaction committed, so SQLite would
    /// never play it back: it is an image of the page from before that transaction.</summary>
    ZeroedHeader,
}
