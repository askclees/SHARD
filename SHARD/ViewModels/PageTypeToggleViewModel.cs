using Avalonia.Media;
using ReactiveUI;
using SHARD.Core.Enums;

namespace SHARD.ViewModels;

public sealed class PageTypeToggleViewModel : ViewModelBase
{
    public PageType PageType { get; }
    public string   Label    { get; }
    public IBrush   Brush    { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { this.RaiseAndSetIfChanged(ref _isSelected, value); _onChanged(); }
    }

    private readonly Action _onChanged;

    public PageTypeToggleViewModel(PageType pageType, Action onChanged)
    {
        PageType   = pageType;
        Label      = GetLabel(pageType);
        Brush      = PageTypeBrushes.For(pageType);
        _onChanged = onChanged;
    }

    private static string GetLabel(PageType t) => t switch
    {
        PageType.BTreeLeafTable     => "Leaf Table",
        PageType.BTreeLeafIndex     => "Leaf Index",
        PageType.BTreeInteriorTable => "Interior Table",
        PageType.BTreeInteriorIndex => "Interior Index",
        PageType.Overflow           => "Overflow",
        PageType.FreelistTrunk      => "Freelist Trunk",
        PageType.FreelistLeaf       => "Freelist Leaf",
        _                           => "Unknown",
    };
}
