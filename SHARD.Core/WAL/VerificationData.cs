using System.Buffers.Binary;

namespace SHARD.Core.WAL;

public class VerificationData
{
    public uint Salt1 { get;}
    public uint Salt2 { get;}
    public uint Checksum1 { get;}
    public uint Checksum2 { get;}

    public VerificationData(uint salt1, uint salt2, uint checksum1, uint checksum2)
    {
        Salt1 = salt1;
        Salt2 = salt2;
        Checksum1 = checksum1;
        Checksum2 = checksum2;
    }

    public VerificationData(ReadOnlySpan<byte> data)
    {
        if (data.Length != 16)
        {
            throw new InvalidDataException("Data must be 16 bytes");
        }

        Salt1 = BinaryPrimitives.ReadUInt32BigEndian(data[0..4]);
        Salt2 = BinaryPrimitives.ReadUInt32BigEndian(data[4..8]);
        Checksum1 = BinaryPrimitives.ReadUInt32BigEndian(data[8..12]);
        Checksum2 = BinaryPrimitives.ReadUInt32BigEndian(data[12..16]);
    }

    public bool Matches(VerificationData compare)
    {
        return Salt1 == compare.Salt1 && Salt2 == compare.Salt2 &&
               Checksum1 == compare.Checksum1 && Checksum2 == compare.Checksum2;
    }
    
}