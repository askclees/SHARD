using SHARD.Core.Decoding;
using SHARD.Core.Enums;

namespace SHARD.Core.Records;

/// <summary>
/// Decodes a SQLite serial type integer into a value kind and byte length.
///
/// Serial type encoding (from the spec):
///   0        → NULL
///   1        → 8-bit signed int    (1 byte)
///   2        → 16-bit signed int   (2 bytes)
///   3        → 24-bit signed int   (3 bytes)
///   4        → 32-bit signed int   (4 bytes)
///   5        → 48-bit signed int   (6 bytes)
///   6        → 64-bit signed int   (8 bytes)
///   7        → IEEE 754 float64    (8 bytes)
///   8        → integer 0           (0 bytes, value is constant)
///   9        → integer 1           (0 bytes, value is constant)
///   10, 11   → reserved (internal use)
///   N≥12, even → BLOB,  length = (N-12)/2
///   N≥13, odd  → TEXT,  length = (N-13)/2
/// </summary>
public readonly struct HeaderEntry
{
    /// <summary>The raw varint value read from the record header.</summary>
    public Varint RawValue { get;}

    /// <summary>The kind of value this serial type represents.</summary>
    public SerialTypeKind Kind { get;}

    /// <summary>Number of bytes of payload consumed by this value (0 for NULL, 8, 9).</summary>
    public int ContentLength { get;}

    /// <summary>Decode a raw serial type varint into a <see cref="HeaderEntry"/>.</summary>
    public HeaderEntry (Varint rawValue)
    {
        RawValue = rawValue;
        if (rawValue.Value < 12)
        {
            switch (rawValue.Value)
            {
                case (0): Kind = SerialTypeKind.Null;
                    ContentLength = 0;
                    break;
                case (1): Kind = SerialTypeKind.Integer;
                    ContentLength = 1;
                    break;
                case (2): Kind = SerialTypeKind.Integer;
                    ContentLength = 2;
                    break;
                case (3): Kind = SerialTypeKind.Integer;
                    ContentLength = 3;
                    break;
                case (4): Kind = SerialTypeKind.Integer;
                    ContentLength = 4;
                    break;
                case (5): Kind = SerialTypeKind.Integer;
                    ContentLength = 6;
                    break;
                case (6): Kind = SerialTypeKind.Integer;
                    ContentLength = 8;
                    break;
                case (7): Kind = SerialTypeKind.Float;
                    ContentLength = 8;
                    break;
                case (8): Kind = SerialTypeKind.Int0;
                    ContentLength = 0;
                    break;
                case (9): Kind = SerialTypeKind.Int1;
                    ContentLength = 0;
                    break;
                default:
                    //Don't crash, but these shouldn't be used
                    ContentLength = 0;
                    Kind = SerialTypeKind.Reserved;
                    break;
            }
        }
        else
        {
            if (rawValue.Value % 2 == 1)
            {
                //value is a string
                Kind = SerialTypeKind.Text;
                ContentLength = (int)((rawValue.Value - 13) / 2);
            }
            else
            {
                //value is a BLOB
                Kind = SerialTypeKind.Blob;
                ContentLength = (int)((rawValue.Value - 12) / 2);
            }
        }
    }
    
}


