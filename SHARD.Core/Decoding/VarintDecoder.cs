namespace SHARD.Core.Decoding;

public sealed class Varint
{
    private const int MAX_VARINT_LENGTH = 9;
    public int Length;
    public long Value;
    
    public Varint(ReadOnlySpan<byte> data)
    {
        if (data.Length > MAX_VARINT_LENGTH)
        {
            throw new InvalidDataException("Data must be 9 bytes or less long");
        }

        byte[] paddedData = new byte[MAX_VARINT_LENGTH];
        Array.Copy(data.ToArray(),0,paddedData, 0, data.Length);

        long tempValue = 0;
        for (int i = 0; i < MAX_VARINT_LENGTH; i++)
        {
            long current = paddedData[i];
            //if greater than or equal to, more bytes to come
            if (current >= 128)
            {
                if (i == 8)
                {
                    tempValue = tempValue | current;
                    Length = 9;
                    Value = tempValue;
                    break;
                }
                else
                {
                    tempValue = tempValue | (current -128);
                    tempValue = tempValue << 7;
                }
            }
            //under 128 is last byte
            else
            {
                tempValue = tempValue | current;
                Length = i+1;
                Value = tempValue;
                break;
            }
        }
        
    }

    public bool Equals(Varint other)
    {
        return (other.Length == this.Length) && (other.Value == this.Value);
    }
    
}