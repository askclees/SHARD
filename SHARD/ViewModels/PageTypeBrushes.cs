using Avalonia.Media;
using SHARD.Core.Enums;

namespace SHARD.ViewModels;

/// <summary>Shared colour swatch lookup for <see cref="PageType"/>, used by the page list and detail views.</summary>
public static class PageTypeBrushes
{
    public static IBrush For(PageType type) => type switch
    {
        PageType.BTreeLeafTable     => new SolidColorBrush(Color.Parse("#4A9ECA")),
        PageType.BTreeInteriorTable => new SolidColorBrush(Color.Parse("#2D7DB3")),
        PageType.BTreeLeafIndex     => new SolidColorBrush(Color.Parse("#4CAF82")),
        PageType.BTreeInteriorIndex => new SolidColorBrush(Color.Parse("#2E8B57")),
        PageType.FreelistTrunk      => new SolidColorBrush(Color.Parse("#E05C5C")),
        PageType.FreelistLeaf       => new SolidColorBrush(Color.Parse("#C0392B")),
        PageType.Overflow           => new SolidColorBrush(Color.Parse("#E0924A")),
        _                           => new SolidColorBrush(Color.Parse("#888888")),
    };
}
