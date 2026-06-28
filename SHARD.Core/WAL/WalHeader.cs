using System.Buffers.Binary;

namespace SHARD.Core.WAL;

public class WalHeader
{
    public uint MagicNumber { get; }
    public uint FileFormatVersion { get;}
    public uint DatabasePageSize { get;}
    public uint CheckpointSequenceNumber { get;}
    public uint Salt1 { get;}
    public uint Salt2 { get;}
    public uint Checksum1 { get;}
    public uint Checksum2 { get;}

    private uint[] _validHeaders =
    {
        0x377f0682,
        0x377f0683,
    };

    public WalHeader(ReadOnlySpan<byte> headerBytes)
    {
        //header is 32 bytes (always)
        if (headerBytes.Length != 32)
        {
            throw new InvalidDataException("Header bytes must be 32 bytes");
        }

        
        //check for magic number
        MagicNumber = BinaryPrimitives.ReadUInt32BigEndian(headerBytes[0..4]);
        if (!_validHeaders.Contains(MagicNumber))
        {
            throw new InvalidDataException("Magic number at offset 0 not recognised");
        }
        int pointer = 4;
        //read all values in sequence
        FileFormatVersion = BinaryPrimitives.ReadUInt32BigEndian(headerBytes[pointer..(pointer+4)]);
        pointer += 4;
        DatabasePageSize = BinaryPrimitives.ReadUInt32BigEndian(headerBytes[pointer..(pointer+4)]);
        pointer += 4;
        CheckpointSequenceNumber = BinaryPrimitives.ReadUInt32BigEndian(headerBytes[pointer..(pointer+4)]);
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