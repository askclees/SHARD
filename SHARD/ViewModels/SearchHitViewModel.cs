namespace SHARD.ViewModels;

public sealed class SearchHitViewModel
{
    public int Offset { get; }
    public int Length { get; }

    /// <summary>Offset + printable preview of the matched bytes, shown in the hit list.</summary>
    public string Preview { get; }

    public SearchHitViewModel(int offset, int length, byte[] pageData, string? context = null)
    {
        Offset = offset;
        Length = length;

        int end     = Math.Min(offset + Math.Max(length, 1), pageData.Length);
        var matched = pageData[offset..end];
        var ascii   = string.Concat(matched.Select(b => b is >= 32 and < 127 ? (char)b : '.'));
        var ctx     = string.IsNullOrEmpty(context) ? "" : $"  —  {context}";
        Preview     = $"0x{offset:X4}  ({length} byte{(length == 1 ? "" : "s")})  |{ascii}|{ctx}";
    }
}
