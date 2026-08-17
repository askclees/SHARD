using SHARD.Core.Enums;
using SHARD.Core.Records;
using SHARD.Core.Schema;
using Xunit;

namespace SHARD.Core.Tests;

public class RecordStructureTests
{
    private static TableSchema TwoIntColumnSchema() => new()
    {
        TableName = "Notes",
        Columns =
        {
            new ColumnDefinition { Name = "id",    Affinity = TypeAffinity.Integer, IsNotNull = true },
            new ColumnDefinition { Name = "score",  Affinity = TypeAffinity.Integer, IsNotNull = true },
        },
    };

    private static TableSchema TextColumnSchema() => new()
    {
        TableName = "Notes",
        Columns =
        {
            new ColumnDefinition { Name = "body", Affinity = TypeAffinity.Text, IsNotNull = true },
        },
    };

    private static TableRow RowWith(params SqliteValue?[] values) => new()
    {
        RowId = 1,
        FieldValues = values.ToList(),
    };

    [Fact]
    public void Tighten_NarrowsColumnToObservedContentLengthRange()
    {
        var schema = TwoIntColumnSchema();
        var rows = new[]
        {
            RowWith(new SqliteValue(10L, 1), new SqliteValue(1000L, 4)),
            RowWith(new SqliteValue(20L, 1), new SqliteValue(2000L, 4)),
            RowWith(new SqliteValue(30L, 1), new SqliteValue(3000L, 4)),
        };

        var tight = RecordStructure.Tighten(schema, rows);

        Assert.Equal(new[] { SerialTypeKind.Integer }, tight.AllowedKindsPerColumn[0]);
        Assert.Equal((1, 1), tight.AllowedContentLengthRangePerColumn[0]);

        Assert.Equal(new[] { SerialTypeKind.Integer }, tight.AllowedKindsPerColumn[1]);
        Assert.Equal((4, 4), tight.AllowedContentLengthRangePerColumn[1]);
    }

    [Fact]
    public void Tighten_NarrowsTextColumnToObservedByteRange()
    {
        var schema = TextColumnSchema();
        var rows = new[]
        {
            RowWith(new SqliteValue("hi", 2)),
            RowWith(new SqliteValue("hello there", 11)),
            RowWith(new SqliteValue("mid", 3)),
        };

        var tight = RecordStructure.Tighten(schema, rows);

        Assert.Equal(new[] { SerialTypeKind.Text }, tight.AllowedKindsPerColumn[0]);
        Assert.Equal((2, 11), tight.AllowedContentLengthRangePerColumn[0]);
    }

    [Fact]
    public void Tighten_LeavesUnobservedColumnLoose()
    {
        var schema = TwoIntColumnSchema();
        // Every observed row is missing the second column entirely.
        var rows = new[] { RowWith(new SqliteValue(10L, 1)) };

        var tight = RecordStructure.Tighten(schema, rows);
        var loose = RecordStructure.FromSchema(schema);

        Assert.Null(tight.AllowedContentLengthRangePerColumn[1]);
        Assert.Equal(loose.AllowedKindsPerColumn[1], tight.AllowedKindsPerColumn[1]);
    }

    [Fact]
    public void NarrowColumn_AppliesManualOverrideIndependentOfObservedData()
    {
        var schema = TwoIntColumnSchema();
        var rs = RecordStructure.FromSchema(schema);

        rs.NarrowColumn(0, allowedKinds: [SerialTypeKind.Int0, SerialTypeKind.Int1]);

        Assert.Equal(new[] { SerialTypeKind.Int0, SerialTypeKind.Int1 }, rs.AllowedKindsPerColumn[0]);
        Assert.Null(rs.AllowedContentLengthRangePerColumn[0]); // untouched — range param was omitted

        rs.NarrowColumn(1, allowedContentLengthRange: (6, 6));

        Assert.Equal((6, 6), rs.AllowedContentLengthRangePerColumn[1]);
    }
}
