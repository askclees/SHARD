namespace SHARD.ViewModels;

/// <summary>
/// Common surface for the WAL Changes tab's per-page-type comparison view models, so the tab
/// can render whichever type a given page's own Compare() call actually produced (currently
/// <see cref="WalPageComparisonViewModel"/> for table leaf pages,
/// <see cref="WalInteriorPageComparisonViewModel"/> for table interior pages) without the
/// view model layer needing to know which one it's holding — Avalonia picks the matching
/// DataTemplate at render time from the object's runtime type.
/// </summary>
public interface IWalPageComparisonViewModel
{
    string ComparedAgainst { get; }
    bool HasAnyChanges { get; }
}
