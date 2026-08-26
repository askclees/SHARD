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
    public void SingleQuotedColumnNames_AreParsedCorrectly()
    {
        var sql = "Create table members( 'mid' INT Unsigned not null, 'mname' text not null, 'msurname' text null, 'mcodea' int null, 'mcodeb' float null)";

        var schema = CreateTableParser.ExtractTableSchema(sql);

        Assert.NotNull(schema);
        Assert.Equal("members", schema!.TableName);
        Assert.Equal(5, schema.Columns.Count);
        Assert.Equal("mid",      schema.Columns[0].Name);
        Assert.Equal("mname",    schema.Columns[1].Name);
        Assert.Equal("msurname", schema.Columns[2].Name);
        Assert.Equal("mcodea",   schema.Columns[3].Name);
        Assert.Equal("mcodeb",   schema.Columns[4].Name);
        Assert.True(schema.Columns[0].IsNotNull);
        Assert.True(schema.Columns[1].IsNotNull);
    }

    [Fact]
    public void InlineLineComments_AreStrippedBeforeParsing()
    {
        var sql = """
            CREATE TABLE t (
                id INTEGER PRIMARY KEY,
                status TEXT NOT NULL, -- deprecated, use status_v2
                value INTEGER -- bool
            )
            """;

        var schema = CreateTableParser.ExtractTableSchema(sql);

        Assert.NotNull(schema);
        Assert.Equal(3, schema!.Columns.Count);
        Assert.Equal("id", schema.Columns[0].Name);
        Assert.Equal("status", schema.Columns[1].Name);
        Assert.Equal("TEXT", schema.Columns[1].DeclaredType);
        Assert.Equal("value", schema.Columns[2].Name);
    }

    [Fact]
    public void InlineComments_WithHyphenInQuotedValue_AreNotStripped()
    {
        // Ensure -- inside a string literal is not treated as a comment
        var sql = "CREATE TABLE t (id INTEGER, note TEXT DEFAULT 'a--b')";

        var schema = CreateTableParser.ExtractTableSchema(sql);

        Assert.NotNull(schema);
        Assert.Equal(2, schema!.Columns.Count);
        Assert.Equal("note", schema.Columns[1].Name);
    }

    [Fact]
    public void SnapchatConversationTable_WithManyInlineComments_ParsesCorrectly()
    {
        var sql = """
            CREATE TABLE conversation (
                client_conversation_id text primary key not null,
                conversation_metadata blob not null,
                send_state_type text not null,
                creation_timestamp integer,
                conversation_version integer not null,
                sync_watermark integer not null, -- this field is deprecated
                tombstoned_at_timestamp integer, -- when this conversation was locally left by user
                nullable_sync_watermark integer, -- if the conversation has synced, the server message id we synced to. null if no sync has happened
                has_more_messages integer not null default 1, -- bool. if the queryMessagesResponse.has_more is true or false. Once the server says theres no more messages it doesn't change.
                source_page integer not null, -- creation source page of conversation. This is used for a business metric.
                last_senders blob,
                latest_received_reaction_version_seen integer not null default 0, -- latest received reaction version seen. Not updated for sent reactions.
                latest_received_reaction_version_unseen integer not null default 0, -- latest received reaction version unseen. Not updated for sent reactions.
                client_resolution_id integer, -- nullable if conversation is committed or if this is a 1:1 conversation. We don't create 1:1 conversations.
                local_conversation_type integer, -- null if the conversation is comitted. Otherwise contains how the local conversation was created
                foreign key(send_state_type) references send_state(send_state_type)
            )
            """;

        var schema = CreateTableParser.ExtractTableSchema(sql);

        Assert.NotNull(schema);
        Assert.Equal("conversation", schema!.TableName);
        Assert.Equal(15, schema.Columns.Count);
        Assert.Equal("client_conversation_id", schema.Columns[0].Name);
        Assert.Equal("local_conversation_type", schema.Columns[14].Name);
        Assert.All(schema.Columns, c => Assert.DoesNotContain("--", c.Name));
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

    [Fact]
    public void ExtractTableSchema_RetainsOriginalSqlVerbatim()
    {
        const string sql = "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT)";

        var schema = CreateTableParser.ExtractTableSchema(sql);

        Assert.Equal(sql, schema!.Sql);
    }
}
