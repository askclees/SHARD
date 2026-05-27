using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace SHARD.Controls;

/// <summary>
/// Renders a byte array as a classic  offset | hex | ASCII  hex dump, with
/// optional coloured highlights over arbitrary byte ranges.
///
/// Usage:
///   &lt;controls:HexView Data="{Binding MyBytes}" Highlights="{Binding MyHighlights}" /&gt;
///
/// Wrap in a ScrollViewer — the control reports its full unclipped size.
/// </summary>
public sealed class HexView : Control
{
    private const int BytesPerRow = 16;

    // Column layout (monospace characters):
    //
    //   "XXXX  "                          →  6 chars  (offset + 2 spaces)
    //   "XX " × 8 + " " + "XX " × 8      → 49 chars  (hex bytes, gap between groups)
    //   " |"                              →  2 chars  (separator)
    //   16 ASCII chars                    → 16 chars
    //   "|"                               →  1 char   (closing pipe)
    //   ─────────────────────────────────   74 chars total
    private const int ColOffset = 6;   // chars before hex section starts
    private const int ColHex    = 6;
    private const int ColSep    = 6 + 49;
    private const int ColAscii  = 6 + 49 + 2;
    private const int TotalCols = 6 + 49 + 2 + 16 + 1;  // 74

    private static readonly Typeface Mono = new("Courier New");
    private const double Em = 12.0;

    // ── Styled properties ─────────────────────────────────────────────────────

    public static readonly StyledProperty<byte[]?> DataProperty =
        AvaloniaProperty.Register<HexView, byte[]?>(nameof(Data));

    public static readonly StyledProperty<IReadOnlyList<HexHighlight>?> HighlightsProperty =
        AvaloniaProperty.Register<HexView, IReadOnlyList<HexHighlight>?>(nameof(Highlights));

    public static readonly StyledProperty<bool> UseDecimalOffsetsProperty =
        AvaloniaProperty.Register<HexView, bool>(nameof(UseDecimalOffsets), defaultValue: false);

    public static readonly StyledProperty<bool> ShowHighlightsProperty =
        AvaloniaProperty.Register<HexView, bool>(nameof(ShowHighlights), defaultValue: true);

    public byte[]? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IReadOnlyList<HexHighlight>? Highlights
    {
        get => GetValue(HighlightsProperty);
        set => SetValue(HighlightsProperty, value);
    }

    /// <summary>When true, the offset column shows decimal; otherwise hexadecimal.</summary>
    public bool UseDecimalOffsets
    {
        get => GetValue(UseDecimalOffsetsProperty);
        set => SetValue(UseDecimalOffsetsProperty, value);
    }

    /// <summary>When false, highlight backgrounds are not drawn.</summary>
    public bool ShowHighlights
    {
        get => GetValue(ShowHighlightsProperty);
        set => SetValue(ShowHighlightsProperty, value);
    }

    static HexView()
    {
        DataProperty.Changed.AddClassHandler<HexView>((v, _) => { v.InvalidateMeasure(); v.InvalidateVisual(); });
        HighlightsProperty.Changed.AddClassHandler<HexView>((v, _) => v.InvalidateVisual());
        UseDecimalOffsetsProperty.Changed.AddClassHandler<HexView>((v, _) => v.InvalidateVisual());
        ShowHighlightsProperty.Changed.AddClassHandler<HexView>((v, _) => v.InvalidateVisual());
    }

    // ── Font metrics (initialised lazily on first render) ─────────────────────

    private double _cw;  // character width
    private double _lh;  // line height

    private void EnsureMetrics()
    {
        if (_cw > 0) return;
        var ft = new FormattedText("X", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                   Mono, Em, Brushes.White);
        _cw = ft.Width;
        _lh = ft.Height + 2;
    }

    // ── Measure ───────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size available)
    {
        EnsureMetrics();
        var data = Data;
        if (data is not { Length: > 0 }) return new Size(0, 0);
        int rows = (data.Length + BytesPerRow - 1) / BytesPerRow;
        return new Size(TotalCols * _cw, rows * _lh);
    }

    // ── Tooltip hit-testing ───────────────────────────────────────────────────

    private string? _lastTooltipLabel; // avoids redundant SetTip calls

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        EnsureMetrics();
        var label = ShowHighlights ? HitTestHighlight(e.GetPosition(this)) : null;
        if (label == _lastTooltipLabel) return;
        _lastTooltipLabel = label;
        ToolTip.SetTip(this, label);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _lastTooltipLabel = null;
        ToolTip.SetTip(this, null);
    }

    /// <summary>
    /// Returns the label of the first highlight whose drawn rectangle contains
    /// <paramref name="pos"/>, or null if the cursor is not over any highlight.
    /// Uses the same geometry as the renderer so hit areas match exactly.
    /// </summary>
    private string? HitTestHighlight(Point pos)
    {
        var data       = Data;
        var highlights = Highlights;
        if (data is not { Length: > 0 } || highlights is not { Count: > 0 }) return null;

        int row = (int)(pos.Y / _lh);
        if (row < 0) return null;

        int rowStart = row * BytesPerRow;
        int rowLen   = Math.Min(BytesPerRow, data.Length - rowStart);
        if (rowStart >= data.Length) return null;

        // Y bounds for this row
        double rowY = row * _lh;
        if (pos.Y < rowY || pos.Y >= rowY + _lh) return null;

        foreach (var h in highlights)
        {
            if (h.Label is null) continue;

            int absStart = Math.Max(h.Offset, rowStart);
            int absEnd   = Math.Min(h.Offset + h.Length, rowStart + rowLen);
            if (absStart >= absEnd) continue;

            int lo = absStart - rowStart;
            int hi = absEnd   - rowStart - 1;

            // Hex section bounds (mirrors the renderer exactly)
            double hexX1 = (ColHex + lo * 3 + (lo >= 8 ? 1 : 0)) * _cw;
            double hexX2 = (ColHex + hi * 3 + (hi >= 8 ? 1 : 0) + 3) * _cw;
            if (pos.X >= hexX1 && pos.X < hexX2) return h.Label;

            // ASCII section bounds
            double ascX1 = (ColAscii + lo) * _cw;
            double ascX2 = (ColAscii + hi + 1) * _cw;
            if (pos.X >= ascX1 && pos.X < ascX2) return h.Label;
        }

        return null;
    }

    // ── Render ────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        EnsureMetrics();
        var data = Data;
        if (data is not { Length: > 0 }) return;

        var  highlights  = Highlights;
        bool decOffsets  = UseDecimalOffsets;
        int  rows        = (data.Length + BytesPerRow - 1) / BytesPerRow;
        var  sb          = new StringBuilder(TotalCols + 4);

        for (int row = 0; row < rows; row++)
        {
            int    rowStart = row * BytesPerRow;
            int    rowLen   = Math.Min(BytesPerRow, data.Length - rowStart);
            double y        = row * _lh;

            // ── 1. Highlight backgrounds ──────────────────────────────────────
            //
            // For each highlight that overlaps this row we draw a single
            // rectangle across the entire highlighted range — this keeps the
            // colour contiguous over the spaces between bytes and over the
            // mid-row gap between byte 7 and byte 8.
            if (ShowHighlights && highlights is { Count: > 0 })
            {
                foreach (var h in highlights)
                {
                    // Clamp highlight to bytes present on this row
                    int absStart = Math.Max(h.Offset, rowStart);
                    int absEnd   = Math.Min(h.Offset + h.Length, rowStart + rowLen);
                    if (absStart >= absEnd) continue;

                    int lo = absStart - rowStart;          // 0-based, inclusive
                    int hi = absEnd   - rowStart - 1;      // 0-based, inclusive

                    var brush = new SolidColorBrush(h.Colour, 0.40);

                    // Hex section: one rect from start of `lo` to end of `hi` (including trailing space).
                    // The (i >= 8 ? 1 : 0) term accounts for the extra gap between groups;
                    // drawing from lo..hi in one rectangle automatically covers that gap.
                    double hexX1 = (ColHex + lo * 3 + (lo >= 8 ? 1 : 0)) * _cw;
                    double hexX2 = (ColHex + hi * 3 + (hi >= 8 ? 1 : 0) + 3) * _cw;
                    ctx.FillRectangle(brush, new Rect(hexX1, y, hexX2 - hexX1, _lh));

                    // ASCII section: one rect from start of `lo` to end of `hi`
                    double ascX1 = (ColAscii + lo) * _cw;
                    double ascX2 = (ColAscii + hi + 1) * _cw;
                    ctx.FillRectangle(brush, new Rect(ascX1, y, ascX2 - ascX1, _lh));
                }
            }

            // ── 2. Row text ───────────────────────────────────────────────────
            sb.Clear();

            // Offset column — 6 chars in both modes
            if (decOffsets)
                sb.Append($"{rowStart:D5} ");   // "DDDDD " — up to 99 999
            else
                sb.Append($"{rowStart:X4}  ");  // "XXXX  "

            // Hex bytes (two groups of 8, separated by an extra space)
            for (int i = 0; i < BytesPerRow; i++)
            {
                sb.Append(i < rowLen ? $"{data[rowStart + i]:X2} " : "   ");
                if (i == 7) sb.Append(' ');
            }

            // ASCII column
            sb.Append(" |");
            for (int i = 0; i < rowLen; i++)
            {
                byte b = data[rowStart + i];
                sb.Append(b is >= 32 and < 127 ? (char)b : '.');
            }
            sb.Append('|');

            var ft = new FormattedText(sb.ToString(), CultureInfo.InvariantCulture,
                                       FlowDirection.LeftToRight, Mono, Em, Brushes.White);
            ctx.DrawText(ft, new Point(0, y));
        }
    }
}
