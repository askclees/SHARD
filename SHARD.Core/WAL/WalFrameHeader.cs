using System.Buffers.Binary;

namespace SHARD.Core.WAL;

public class WalFrameHeader
{
    public uint PageNumber { get; }
    public uint SizeOfDatabaseInPages { get;}
    public VerificationData VerificationData { get; }
    public bool IsCurrent { get; }

    public WalFrameHeader(ReadOnlySpan<byte> headerBytes, VerificationData checksums)
    {
        //header is 24 bytes (always)
        if (headerBytes.Length != 24)
        {
            throw new InvalidDataException("WAL Frame Header bytes must be 24 bytes");
        }

        //read all values in sequence
        PageNumber = BinaryPrimitives.ReadUInt32BigEndian(headerBytes[0..4]);
        int pointer = 4;
        SizeOfDatabaseInPages = BinaryPrimitives.ReadUInt32BigEndian(headerBytes[pointer..(pointer+4)]);
        pointer += 4;
        VerificationData = new VerificationData(headerBytes[pointer..(pointer + 16)]);
        IsCurrent = VerificationData.Salt1 == checksums.Salt1 && VerificationData.Salt2 == checksums.Salt2;
    }
}