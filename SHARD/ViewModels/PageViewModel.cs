using Avalonia.Media;
using ReactiveUI;
using SHARD.Core.Pages;

namespace SHARD.ViewModels;

/// <summary>ViewModel wrapping a single <see cref="SqlitePage"/> for display.</summary>
public sealed class PageViewModel : ViewModelBase
{
    public SqlitePage Page { get; }

    // ── List display ──────────────────────────────────────────────────────
    public uint   PageNumber => Page.PageNumber;
    public string TypeLabel  => Page.PageType.ToString();

    /// <summary>Colour swatch shown next to each page in the list.</summary>
    public IBrush TypeBrush => Page.PageType switch
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

    // ── Detail panel ──────────────────────────────────────────────────────
    /// <summary>Human-readable summary of page header fields.</summary>
    public string Summary
    {
        get
        {
            try   { return BuildSummary(Page); }
            catch (NotImplementedException) { return $"[Implement BuildSummary()]\n\nPage {Page.PageNumber}  ·  {Page.PageType}  ·  {Page.PageSize} bytes"; }
        }
    }

    /// <summary>Hex + ASCII dump of the raw page bytes.</summary>
    public string HexDump
    {
        get
        {
            try   { return BuildHexDump(Page.Data); }
            catch (NotImplementedException) { return "[Implement BuildHexDump()]"; }
        }
    }

    public PageViewModel(SqlitePage page) => Page = page;

    // ── Implement these ───────────────────────────────────────────────────
    private static string BuildSummary(SqlitePage page) =>
        throw new NotImplementedException();

    private static string BuildHexDump(byte[] data) =>
        throw new NotImplementedException();
}
