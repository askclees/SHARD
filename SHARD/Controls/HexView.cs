using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SHARD.Controls;

/// <summary>
/// Renders a byte array as a classic  offset | hex | ASCII  hex dump, with
/// optional coloured highlights over arbitrary byte ranges.
///
/// Usage:
///   &lt;controls:HexView Data="{Binding MyBytes}" Highlights="{Binding MyHighlights}" /&gt;
///
/// Wrap in a ScrollViewer — the control reports its full unclipped size.
/// Click to place a cursor; drag to select a range; Ctrl+C copies selected bytes as hex.
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
    private const int ColHex   = 6;
    private const int ColSep   = 6 + 49;
    private const int ColAscii = 6 + 49 + 2;
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

    public static readonly StyledProperty<int> CursorOffsetProperty =
        AvaloniaProperty.Register<HexView, int>(nameof(CursorOffset), defaultValue: -1);

    /// <summary>Byte offset of the last clicked position, or -1 when no cursor is set.</summary>
    public int CursorOffset
    {
        get => GetValue(CursorOffsetProperty);
        private set => SetValue(CursorOffsetProperty, value);
    }

    static HexView()
    {
        DataProperty.Changed.AddClassHandler<HexView>((v, _) => { v.ClearSelection(); v.InvalidateMeasure(); v.InvalidateVisual(); });
        HighlightsProperty.Changed.AddClassHandler<HexView>((v, _) => v.InvalidateVisual());
        UseDecimalOffsetsProperty.Changed.AddClassHandler<HexView>((v, _) => v.InvalidateVisual());
        ShowHighlightsProperty.Changed.AddClassHandler<HexView>((v, _) => v.InvalidateVisual());
    }

    public HexView()
    {
        Focusable = true;
    }

    // ── Parent ScrollViewer scroll-lock ───────────────────────────────────────
    //
    // Avalonia can scroll the parent ScrollViewer back to offset 0 whenever the
    // HexView gains focus — either via base.OnPointerPressed auto-focusing on a
    // left-click, or via focus being restored to HexView after a popup (e.g. the
    // right-click context menu) closes.
    //
    // We fix this by:
    //  • subscribing to both ScrollViewer AND ScrollContentPresenter PropertyChanged
    //    so we catch the Offset change no matter which layer applies it;
    //  • suppressing RequestBringIntoViewEvent so keyboard-navigation focus doesn't
    //    trigger a scroll either;
    //  • locking in OnGotFocus (not just OnPointerPressed) so that the focus-return
    //    after a popup close is also protected;
    //  • releasing the lock at Background priority, which outlives the layout pass
    //    that would otherwise apply the unwanted scroll.

    private ScrollViewer?          _parentSv;
    private ScrollContentPresenter? _parentPresenter;
    private Vector?                 _scrollLock;
    private bool                    _inScrollRestore;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _parentSv        = this.FindAncestorOfType<ScrollViewer>();
        _parentPresenter = this.FindAncestorOfType<ScrollContentPresenter>();
        if (_parentSv        is not null) _parentSv.PropertyChanged        += OnSvPropertyChanged;
        if (_parentPresenter is not null) _parentPresenter.PropertyChanged += OnPresenterPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_parentSv        is not null) _parentSv.PropertyChanged        -= OnSvPropertyChanged;
        if (_parentPresenter is not null) _parentPresenter.PropertyChanged -= OnPresenterPropertyChanged;
        _parentSv = null;
        _parentPresenter = null;
    }

    // Veto any Offset change on either layer while the lock is held.
    private void OnSvPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_scrollLock is not { } locked || _inScrollRestore) return;
        if (e.Property.Name != "Offset") return;
        _inScrollRestore = true;
        ((ScrollViewer)sender!).Offset = locked;
        _inScrollRestore = false;
    }

    private void OnPresenterPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_scrollLock is not { } locked || _inScrollRestore) return;
        if (e.Property.Name != "Offset") return;
        _inScrollRestore = true;
        if (_parentSv is not null) _parentSv.Offset = locked;
        _inScrollRestore = false;
    }

    // Lock before base.OnGotFocus so we catch BringIntoView called inside it,
    // and also catch focus-return after a popup (ContextMenu) closes.
    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        _scrollLock = _parentSv?.Offset ?? default;
        base.OnGotFocus(e);
        Dispatcher.UIThread.Post(() => _scrollLock = null, DispatcherPriority.Background);
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

    // ── Hovered-field label (bound in the toolbar TextBlock) ─────────────────

    public static readonly StyledProperty<string?> HoveredLabelProperty =
        AvaloniaProperty.Register<HexView, string?>(nameof(HoveredLabel));

    public string? HoveredLabel
    {
        get => GetValue(HoveredLabelProperty);
        private set => SetValue(HoveredLabelProperty, value);
    }

    private string? _lastLabel;

    // ── Selection info / length ───────────────────────────────────────────────

    public static readonly StyledProperty<string?> SelectionInfoProperty =
        AvaloniaProperty.Register<HexView, string?>(nameof(SelectionInfo));

    /// <summary>Human-readable selection length, e.g. "12 bytes  (0x0C)". Null when no range is selected.</summary>
    public string? SelectionInfo
    {
        get => GetValue(SelectionInfoProperty);
        private set => SetValue(SelectionInfoProperty, value);
    }

    public static readonly StyledProperty<int> SelectionLengthProperty =
        AvaloniaProperty.Register<HexView, int>(nameof(SelectionLength), defaultValue: 0);

    /// <summary>Number of bytes in the current selection range; 0 when no range is selected.</summary>
    public int SelectionLength
    {
        get => GetValue(SelectionLengthProperty);
        private set => SetValue(SelectionLengthProperty, value);
    }

    private void UpdateSelectionInfo()
    {
        if (_selStart < 0 || _selEnd < 0 || _selStart == _selEnd)
        {
            SelectionInfo  = null;
            SelectionLength = 0;
            return;
        }

        int lo  = Math.Min(_selStart, _selEnd);
        int hi  = Math.Max(_selStart, _selEnd);
        int len = hi - lo + 1;

        SelectionInfo   = $"{len} byte{(len == 1 ? "" : "s")}  (0x{len:X})";
        SelectionLength = len;
    }

    // ── Selection / cursor ────────────────────────────────────────────────────

    private int  _selStart  = -1;
    private int  _selEnd    = -1;
    private bool _isDragging;

    private void ClearSelection()
    {
        _selStart      = -1;
        _selEnd        = -1;
        CursorOffset   = -1;
        SelectionInfo  = null;
        SelectionLength = 0;
    }

    // ── Scroll to offset ──────────────────────────────────────────────────────

    /// <summary>Scrolls the parent <see cref="ScrollViewer"/> to bring the row containing
    /// <paramref name="byteOffset"/> to the top of the viewport.</summary>
    public void ScrollToByteOffset(int byteOffset)
    {
        _scrollLock = null;   // programmatic scroll must not be blocked by a held lock
        EnsureMetrics();
        if (_lh <= 0 || _parentSv is null) return;
        int row = Math.Max(0, byteOffset / BytesPerRow);
        _parentSv.SetCurrentValue(ScrollViewer.OffsetProperty, new Vector(0, row * _lh));
    }

    /// <summary>Moves the cursor to <paramref name="byteOffset"/> without scrolling.</summary>
    public void SetCursorOffset(int byteOffset)
    {
        _selStart      = byteOffset;
        _selEnd        = byteOffset;
        CursorOffset   = byteOffset;
        SelectionInfo  = null;
        SelectionLength = 0;
        InvalidateVisual();
    }

    // ── Pointer input ─────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        EnsureMetrics();
        int hit = HitTestByte(e.GetPosition(this));

        // Pre-lock before any focus change (OnGotFocus re-locks too, but setting
        // it here means it is always held even if GotFocus fires synchronously
        // inside base.OnPointerPressed before we return to this method body).
        _scrollLock = _parentSv?.Offset ?? default;

        base.OnPointerPressed(e);   // may auto-focus on left-click → OnGotFocus fires
        Focus();                    // ensure focus for keyboard input (right-click, etc.)

        if (hit < 0) return;
        CursorOffset = hit;

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _selStart   = hit;
            _selEnd     = hit;
            _isDragging = true;
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        EnsureMetrics();

        // Hover label
        var label = ShowHighlights ? HitTestHighlight(e.GetPosition(this)) : null;
        if (label != _lastLabel)
        {
            _lastLabel   = label;
            HoveredLabel = label;
        }

        // Drag selection — also check button state in case release happened outside the control
        if (!_isDragging || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        int hit = HitTestByte(e.GetPosition(this));
        if (hit >= 0 && hit != _selEnd)
        {
            _selEnd = hit;
            UpdateSelectionInfo();
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isDragging = false;
        UpdateSelectionInfo();
        e.Handled   = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isDragging  = false;
        _lastLabel   = null;
        HoveredLabel = null;
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.C || e.KeyModifiers != KeyModifiers.Control) return;

        var data = Data;
        if (data is null || _selStart < 0 || _selEnd < 0) { e.Handled = true; return; }

        int lo = Math.Clamp(Math.Min(_selStart, _selEnd), 0, data.Length - 1);
        int hi = Math.Clamp(Math.Max(_selStart, _selEnd), 0, data.Length - 1);

        var sb = new StringBuilder((hi - lo + 1) * 3);
        for (int i = lo; i <= hi; i++)
        {
            if (i > lo) sb.Append(' ');
            sb.Append($"{data[i]:X2}");
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(sb.ToString());

        e.Handled = true;
    }

    // ── Hit testing ───────────────────────────────────────────────────────────

    private int HitTestByte(Point pos)
    {
        var data = Data;
        if (data is not { Length: > 0 } || _lh <= 0) return -1;

        int row = (int)(pos.Y / _lh);
        if (row < 0) return -1;
        int rowStart = row * BytesPerRow;
        if (rowStart >= data.Length) return -1;
        int rowLen = Math.Min(BytesPerRow, data.Length - rowStart);

        double charX = pos.X / _cw;
        int byteInRow;

        if (charX >= ColHex && charX < ColSep)
        {
            double rel = charX - ColHex;
            if (rel > 24) rel -= 1;  // skip the extra gap between the two groups of 8
            byteInRow = Math.Clamp((int)(rel / 3), 0, rowLen - 1);
        }
        else if (charX >= ColAscii && charX < ColAscii + BytesPerRow)
        {
            byteInRow = Math.Clamp((int)(charX - ColAscii), 0, rowLen - 1);
        }
        else
        {
            return -1;
        }

        int offset = rowStart + byteInRow;
        return offset < data.Length ? offset : -1;
    }

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

            double hexX1 = (ColHex + lo * 3 + (lo >= 8 ? 1 : 0)) * _cw;
            double hexX2 = (ColHex + hi * 3 + (hi >= 8 ? 1 : 0) + 3) * _cw;
            if (pos.X >= hexX1 && pos.X < hexX2) return h.Label;

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

        bool hasSelection = _selStart >= 0 && _selEnd >= 0;
        int  selMin       = hasSelection ? Math.Min(_selStart, _selEnd) : -1;
        int  selMax       = hasSelection ? Math.Max(_selStart, _selEnd) : -1;
        bool isCursor     = hasSelection && _selStart == _selEnd;

        var selBrush    = new SolidColorBrush(Color.FromRgb(51, 102, 153), 0.70);
        var cursorFill  = new SolidColorBrush(Color.FromRgb(255, 200, 0));
        var cursorText  = Brushes.Black;

        for (int row = 0; row < rows; row++)
        {
            int    rowStart = row * BytesPerRow;
            int    rowLen   = Math.Min(BytesPerRow, data.Length - rowStart);
            double y        = row * _lh;

            // ── 1. Highlight backgrounds ──────────────────────────────────────
            if (ShowHighlights && highlights is { Count: > 0 })
            {
                foreach (var h in highlights)
                {
                    int absStart = Math.Max(h.Offset, rowStart);
                    int absEnd   = Math.Min(h.Offset + h.Length, rowStart + rowLen);
                    if (absStart >= absEnd) continue;

                    int lo = absStart - rowStart;
                    int hi = absEnd   - rowStart - 1;

                    var brush = new SolidColorBrush(h.Colour, 0.40);

                    double hexX1 = (ColHex + lo * 3 + (lo >= 8 ? 1 : 0)) * _cw;
                    double hexX2 = (ColHex + hi * 3 + (hi >= 8 ? 1 : 0) + 3) * _cw;
                    ctx.FillRectangle(brush, new Rect(hexX1, y, hexX2 - hexX1, _lh));

                    double ascX1 = (ColAscii + lo) * _cw;
                    double ascX2 = (ColAscii + hi + 1) * _cw;
                    ctx.FillRectangle(brush, new Rect(ascX1, y, ascX2 - ascX1, _lh));
                }
            }

            // ── 2. Selection background ───────────────────────────────────────
            if (hasSelection && !isCursor)
            {
                int absStart = Math.Max(selMin, rowStart);
                int absEnd   = Math.Min(selMax + 1, rowStart + rowLen);
                if (absStart < absEnd)
                {
                    int lo = absStart - rowStart;
                    int hi = absEnd   - rowStart - 1;

                    double hexX1 = (ColHex + lo * 3 + (lo >= 8 ? 1 : 0)) * _cw;
                    double hexX2 = (ColHex + hi * 3 + (hi >= 8 ? 1 : 0) + 3) * _cw;
                    ctx.FillRectangle(selBrush, new Rect(hexX1, y, hexX2 - hexX1, _lh));

                    double ascX1 = (ColAscii + lo) * _cw;
                    double ascX2 = (ColAscii + hi + 1) * _cw;
                    ctx.FillRectangle(selBrush, new Rect(ascX1, y, ascX2 - ascX1, _lh));
                }
            }

            // ── 3. Row text ───────────────────────────────────────────────────
            sb.Clear();

            if (decOffsets)
                sb.Append($"{rowStart:D5} ");
            else
                sb.Append($"{rowStart:X4}  ");

            for (int i = 0; i < BytesPerRow; i++)
            {
                sb.Append(i < rowLen ? $"{data[rowStart + i]:X2} " : "   ");
                if (i == 7) sb.Append(' ');
            }

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

            // ── 4. Cursor ─────────────────────────────────────────────────────
            if (isCursor && _selStart >= rowStart && _selStart < rowStart + rowLen)
            {
                int  byteInRow = _selStart - rowStart;
                byte b         = data[_selStart];

                double hexX = (ColHex + byteInRow * 3 + (byteInRow >= 8 ? 1 : 0)) * _cw;
                ctx.FillRectangle(cursorFill, new Rect(hexX, y, 2 * _cw, _lh));
                ctx.DrawText(new FormattedText($"{b:X2}", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Mono, Em, cursorText), new Point(hexX, y));

                double ascX = (ColAscii + byteInRow) * _cw;
                ctx.FillRectangle(cursorFill, new Rect(ascX, y, _cw, _lh));
                ctx.DrawText(new FormattedText((b is >= 32 and < 127 ? (char)b : '.').ToString(),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, Em, cursorText),
                    new Point(ascX, y));
            }
        }
    }
}
