using Avalonia.Media;

namespace SHARD.Controls;

/// <summary>
/// Marks a contiguous byte range for background-colour highlighting inside a <see cref="HexView"/>.
/// </summary>
/// <param name="Offset">Zero-based byte offset into the data array.</param>
/// <param name="Length">Number of bytes to highlight.</param>
/// <param name="Colour">Fill colour (the control renders it at reduced opacity).</param>
/// <param name="Label">Optional human-readable name shown in tooltips (future use).</param>
public record HexHighlight(int Offset, int Length, Color Colour, string? Label = null);
