using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using SHARD.Core.Decoding;
using SHARD.ViewModels;

namespace SHARD.Controls;

public partial class DataInspectorControl : UserControl
{
    public static readonly StyledProperty<byte[]?> DataProperty =
        AvaloniaProperty.Register<DataInspectorControl, byte[]?>(nameof(Data));

    public static readonly StyledProperty<int> OffsetProperty =
        AvaloniaProperty.Register<DataInspectorControl, int>(nameof(Offset), defaultValue: -1);

    public static readonly StyledProperty<int> SelectionLengthProperty =
        AvaloniaProperty.Register<DataInspectorControl, int>(nameof(SelectionLength), defaultValue: 0);

    public byte[]? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public int Offset
    {
        get => GetValue(OffsetProperty);
        set => SetValue(OffsetProperty, value);
    }

    public int SelectionLength
    {
        get => GetValue(SelectionLengthProperty);
        set => SetValue(SelectionLengthProperty, value);
    }

    static DataInspectorControl()
    {
        DataProperty.Changed.AddClassHandler<DataInspectorControl>((c, _) => c.Refresh());
        OffsetProperty.Changed.AddClassHandler<DataInspectorControl>((c, _) => c.Refresh());
        SelectionLengthProperty.Changed.AddClassHandler<DataInspectorControl>((c, _) => c.RefreshSelection());
    }

    public DataInspectorControl()
    {
        InitializeComponent();
    }

    private void Refresh()
    {
        if (RowsControl is null) return;
        RowsControl.ItemsSource = ComputeRows(Data, Offset);
    }

    private void RefreshSelection()
    {
        if (SelectionPanel is null || SelectionRowsControl is null) return;
        int len = SelectionLength;
        if (len <= 0)
        {
            SelectionPanel.IsVisible = false;
            return;
        }
        long textType = (long)len * 2 + 13;
        long blobType = (long)len * 2 + 12;
        SelectionRowsControl.ItemsSource = new[]
        {
            new InfoRow("Length", $"{len}  (0x{len:X})"),
            new InfoRow("Text type", $"{textType}  (0x{textType:X})"),
            new InfoRow("Blob type", $"{blobType}  (0x{blobType:X})"),
        };
        SelectionPanel.IsVisible = true;
    }

    private static IReadOnlyList<InfoRow> ComputeRows(byte[]? data, int offset)
    {
        var rows = new List<InfoRow>();

        if (data is null || offset < 0 || offset >= data.Length)
        {
            rows.Add(new InfoRow("Offset", "—"));
            return rows;
        }

        rows.Add(new InfoRow("Offset", $"0x{offset:X4}  ({offset})"));

        // 1 byte
        rows.Add(new InfoRow("Int8",   ((sbyte)data[offset]).ToString()));
        rows.Add(new InfoRow("UInt8",  data[offset].ToString()));

        // 2 bytes
        if (offset + 2 <= data.Length)
        {
            var s = data.AsSpan(offset, 2);
            rows.Add(new InfoRow("Int16 BE",  BinaryPrimitives.ReadInt16BigEndian(s).ToString()));
            rows.Add(new InfoRow("UInt16 BE", BinaryPrimitives.ReadUInt16BigEndian(s).ToString()));
        }
        else
        {
            rows.Add(new InfoRow("Int16 BE",  "—"));
            rows.Add(new InfoRow("UInt16 BE", "—"));
        }

        // 3 bytes (SQLite Int24)
        if (offset + 3 <= data.Length)
        {
            int v = (data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2];
            if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
            rows.Add(new InfoRow("Int24 BE", v.ToString()));
        }
        else rows.Add(new InfoRow("Int24 BE", "—"));

        // 4 bytes
        if (offset + 4 <= data.Length)
        {
            var s = data.AsSpan(offset, 4);
            rows.Add(new InfoRow("Int32 BE",  BinaryPrimitives.ReadInt32BigEndian(s).ToString()));
            rows.Add(new InfoRow("UInt32 BE", BinaryPrimitives.ReadUInt32BigEndian(s).ToString()));
        }
        else
        {
            rows.Add(new InfoRow("Int32 BE",  "—"));
            rows.Add(new InfoRow("UInt32 BE", "—"));
        }

        // 6 bytes (SQLite Int48)
        if (offset + 6 <= data.Length)
        {
            long v = 0;
            for (int i = 0; i < 6; i++) v = (v << 8) | data[offset + i];
            if ((v & 0x800000000000L) != 0) v |= ~((1L << 48) - 1);
            rows.Add(new InfoRow("Int48 BE", v.ToString()));
        }
        else rows.Add(new InfoRow("Int48 BE", "—"));

        // 8 bytes
        if (offset + 8 <= data.Length)
        {
            var s = data.AsSpan(offset, 8);
            rows.Add(new InfoRow("Int64 BE",   BinaryPrimitives.ReadInt64BigEndian(s).ToString()));
            rows.Add(new InfoRow("UInt64 BE",  BinaryPrimitives.ReadUInt64BigEndian(s).ToString()));
            double d = BinaryPrimitives.ReadDoubleBigEndian(s);
            rows.Add(new InfoRow("Float64 BE", double.IsNaN(d) ? "NaN" : d.ToString("G9")));
        }
        else
        {
            rows.Add(new InfoRow("Int64 BE",   "—"));
            rows.Add(new InfoRow("UInt64 BE",  "—"));
            rows.Add(new InfoRow("Float64 BE", "—"));
        }

        // SQLite Varint (variable, up to 9 bytes)
        if (offset < data.Length)
        {
            var varint = Varint.ReadAt(data, offset);
            rows.Add(new InfoRow("Varint", $"{varint.Value}  ({varint.Length}B)"));
            rows.Add(new InfoRow("Serial type", SerialTypeLabel(varint.Value)));
        }
        else
        {
            rows.Add(new InfoRow("Varint", "—"));
            rows.Add(new InfoRow("Serial type", "—"));
        }

        return rows;
    }

    private static string SerialTypeLabel(long v) => v switch
    {
        0  => "NULL",
        1  => "Integer  1 byte",
        2  => "Integer  2 bytes",
        3  => "Integer  3 bytes",
        4  => "Integer  4 bytes",
        5  => "Integer  6 bytes",
        6  => "Integer  8 bytes",
        7  => "Float  8 bytes",
        8  => "Integer  0 (const)",
        9  => "Integer  1 (const)",
        10 => "Reserved",
        11 => "Reserved",
        _  when v >= 12 && v % 2 == 0 => $"Blob  {(v - 12) / 2} bytes",
        _  when v >= 13 && v % 2 == 1 => $"Text  {(v - 13) / 2} chars",
        _  => "—"
    };
}
