using System.Buffers.Binary;

namespace SHARD.Core.WAL;

public class WalFrameHeader
{
    public uint PageNumber { get; }
    public uint SizeOfDatabaseInPages { get;}
    public uint Salt1 { get;}
    public uint Salt2 { get;}
    public uint Checksum1 { get;}
    public uint Checksum2 { get;}

    public WalFrameHeader(ReadOnlySpan<byte> headerBytes)
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
        Salt1 = BinaryPrimitives.ReadUInt32BigEndian(headerBytes[pointer..(pointer+4)]);
        pointer += 4;
        Salt2 = BinaryPrimitives.ReadUInt32BigEndian(headerBytes[pointer..(pointer+4)]);
        pointer += 4;
        Checksum1 = BinaryPrimitives.ReadUInt32BigEndian(headerBytes[pointer..(pointer+4)]);
        pointer += 4;
        Checksum2 = BinaryPrimitives.ReadUInt32BigEndian(headerBytes[pointer..(pointer+4)]);
    }
}