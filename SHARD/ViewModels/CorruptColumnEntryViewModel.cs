using ReactiveUI;
using SHARD.Core.Enums;

namespace SHARD.ViewModels;

public sealed class CorruptColumnEntryViewModel : ViewModelBase
{
    public int Index { get; }
    public string ColumnName { get; }
    public string TypeLabel { get; }
    public TypeAffinity Affinity { get; }
    public bool IsAnchor { get; }
    public bool IsBeforeAnchor { get; }
    public bool IsAutoDecoded => !IsBeforeAnchor;
    public string ColumnNameLabel => IsAnchor ? $"{ColumnName} ★" : ColumnName;

    private string _manualLength = "0";
    public string ManualLength
    {
        get => _manualLength;
        set => this.RaiseAndSetIfChanged(ref _manualLength, value);
    }

    private string _serialTypeLabel = "";
    public string SerialTypeLabel
    {
        get => _serialTypeLabel;
        set => this.RaiseAndSetIfChanged(ref _serialTypeLabel, value);
    }

    private string _contentLengthLabel = "";
    public string ContentLengthLabel
    {
        get => _contentLengthLabel;
        set => this.RaiseAndSetIfChanged(ref _contentLengthLabel, value);
    }

    private string _decodedValue = "";
    public string DecodedValue
    {
        get => _decodedValue;
        set => this.RaiseAndSetIfChanged(ref _decodedValue, value);
    }

    public CorruptColumnEntryViewModel(int index, string columnName, TypeAffinity affinity, bool isBeforeAnchor, bool isAnchor)
    {
        Index         = index;
        ColumnName    = columnName;
        Affinity      = affinity;
        TypeLabel     = affinity.ToString();
        IsBeforeAnchor = isBeforeAnchor;
        IsAnchor      = isAnchor;
    }
}
