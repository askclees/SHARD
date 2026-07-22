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
        return Frames.Take(idx).LastOrDefault(f => f.Header.PageNumber == frame.Header.PageNumber);
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