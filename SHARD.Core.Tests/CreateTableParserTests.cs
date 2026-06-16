using SHARD.Core.Enums;
using SHARD.Core.Schema;

namespace SHARD.Core.Tests;

public class CreateTableParserTests
{
    [Fact]
    public void SimpleTable_ParsesColumnsAndRowIdAlias()
    {
        var schema = CreateTableParser.ExtractTableSchema(
            "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT, age INTEGER)");

        Assert.NotNull(schema);
        Assert.Equal("people", schema!.TableName);
        Assert.Equal(3, schema.Columns.Count);

        var id = schema.Columns[0];
        Assert.Equal("id", id.Name);
        Assert.True(id.IsPrimaryKey);
        Assert.True(id.IsRowIdAlias);
        Assert.Equal(TypeAffinity.Integer, id.Affinity);

        var name = schema.Columns[1];
        Assert.Equal("name", name.Name);
        Assert.Equal(TypeAffinity.Text, name.Affinity);
        Assert.False(name.IsRowIdAlias);
    }

    [Fact]
    public void NotNullAndUniqueConstraints_AreDetected()
    {
        var schema = CreateTableParser.ExtractTableSchema(
            "CREATE TABLE users (id INTEGER, email TEXT NOT NULL UNIQUE, bio TEXT)");

        var email = schema!.Columns.Single(c => c.Name == "email");
        Assert.True(email.IsNotNull);
        Assert.True(email.IsUnique);

        var bio = schema.Columns.Single(c => c.Name == "bio");
        Assert.False(bio.IsNotNull);
        Assert.False(bio.IsUnique);
    }

    [Fact]
    public void CompositePrimaryKey_TableConstraint_MarksBothColumns()
    {
        var schema = CreateTableParser.ExtractTableSchema(
            "CREATE TABLE membership (group_id INTEGER, user_id INTEGER, PRIMARY KEY (group_id, user_id))");

        Assert.Equal(2, schema!.Columns.Count);
        Assert.All(schema.Columns, c => Assert.True(c.IsPrimaryKey));
        // composite key -> neither column is a rowid alias
        Assert.All(schema.Columns, c => Assert.False(c.IsRowIdAlias));
    }

    [Fact]
    public void QuotedIdentifiers_AreUnquoted()
    {
        var schema = CreateTableParser.ExtractTableSchema(
            "CREATE TABLE \"my table\" (\"col one\" TEXT, [col two] INTEGER, `col three` BLOB)");

        Assert.Equal("my table", schema!.TableName);
        Assert.Equal(new[] { "col one", "col two", "col three" }, schema.Columns.Select(c => c.Name));
    }

    [Fact]
    public void CommasInsideTypeParensAndDefaults_DoNotSplitColumns()
    {
        var schema = CreateTableParser.ExtractTableSchema(
            "CREATE TABLE prices (id INTEGER, amount DECIMAL(10,2) DEFAULT 0, note TEXT DEFAULT 'a, b')");

        Assert.Equal(3, schema!.Columns.Count);
        var amount = schema.Columns.Single(c => c.Name == "amount");
        Assert.Equal("DECIMAL(10,2)", amount.DeclaredType);
        Assert.Equal(TypeAffinity.Numeric, amount.Affinity);
    }

    [Fact]
    public void WithoutRowid_DisablesRowIdAliasEvenForIntegerPrimaryKey()
    {
        var schema = CreateTableParser.ExtractTableSchema(
            "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT) WITHOUT ROWID");

        var id = schema!.Columns.Single(c => c.Name == "id");
        Assert.True(id.IsPrimaryKey);
        Assert.False(id.IsRowIdAlias);
    }

    [Fact]
    public void TypeAffinityResolution_FollowsSqliteRules()
    {
        Assert.Equal(TypeAffinity.Integer, CreateTableParser.ResolveAffinity("INT"));
        Assert.Equal(TypeAffinity.Integer, CreateTableParser.ResolveAffinity("BIGINT"));
        Assert.Equal(TypeAffinity.Text, CreateTableParser.ResolveAffinity("VARCHAR(255)"));
        Assert.Equal(TypeAffinity.Text, CreateTableParser.ResolveAffinity("CLOB"));
        Assert.Equal(TypeAffinity.Blob, CreateTableParser.ResolveAffinity("BLOB"));
        Assert.Equal(TypeAffinity.Blob, CreateTableParser.ResolveAffinity(null));
        Assert.Equal(TypeAffinity.Real, CreateTableParser.ResolveAffinity("DOUBLE"));
        Assert.Equal(TypeAffinity.Numeric, CreateTableParser.ResolveAffinity("DECIMAL(10,2)"));
    }

    [Fact]
    public void WideTableFixture_RealCreateStatement_ParsesAll25Columns()
    {
        using var db = SqliteForensicDatabase.Open(
            Path.Combine(AppContext.BaseDirectory, "TestData", "single_leaf_with_overflow.db"));

        var row = db.ReadSqliteMaster().Single(r => r.Name == "wide_table");
        var schema = CreateTableParser.ExtractTableSchema(row.Sql!);

        Assert.NotNull(schema);
        Assert.Equal("wide_table", schema!.TableName);
        Assert.Equal(25, schema.Columns.Count);
        Assert.Equal("column_with_a_fairly_long_name_00", schema.Columns[0].Name);
        Assert.Equal("column_with_a_fairly_long_name_24", schema.Columns[24].Name);
        Assert.All(schema.Columns, c => Assert.Equal(TypeAffinity.Text, c.Affinity));
    }
}
