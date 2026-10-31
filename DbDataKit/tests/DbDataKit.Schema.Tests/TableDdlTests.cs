using DbDataKit.Schema.Ddl;

namespace DbDataKit.Schema.Tests;

/// <summary>The statements of each engine, from columns already in its own types (plan 19, H03). Build's tests of its type mapping run through these too.</summary>
public class TableDdlTests
{
    private static NativeColumn Col(string name, string declaration, bool nullable = true, string? collation = null) =>
        new(name, declaration, collation, nullable, new ColumnShape(name, declaration.Split('(')[0], null, null, null, nullable, collation));

    private static readonly TableDdl SqlServer = new TSqlTableDdl("sqlserver");
    private static readonly TableDdl Fabric = new TSqlTableDdl("fabric");
    private static readonly TableDdl Postgres = new PostgresTableDdl();

    [Fact]
    public void Create_table_text_is_exact_for_both_families()
    {
        var sql = new[] { Col("id", "bigint", false), Col("label", "nvarchar(20)", true, "Latin1_General_100_CI_AS") };
        Assert.Equal("CREATE TABLE [marts].[fct] (\n  [id] bigint NOT NULL,\n  [label] nvarchar(20) COLLATE Latin1_General_100_CI_AS NULL\n);", SqlServer.CreateTable("marts", "fct", sql));

        var pg = new[] { Col("id", "bigint", false), Col("label", "varchar(20)", true, "en_US.utf8") };
        Assert.Equal("CREATE TABLE \"marts\".\"fct\" (\n  \"id\" bigint NOT NULL,\n  \"label\" varchar(20) COLLATE \"en_US.utf8\" NULL\n);", Postgres.CreateTable("marts", "fct", pg));
    }

    [Fact]
    public void Keys_are_added_after_the_table_with_the_same_text_on_both_families()
    {
        Assert.Equal("ALTER TABLE [s].[t] ADD CONSTRAINT [pk_t] PRIMARY KEY ([a], [b]);", SqlServer.AddPrimaryKey("s", "t", "pk_t", ["a", "b"]));
        Assert.Equal("ALTER TABLE \"s\".\"t\" ADD CONSTRAINT \"uq_t_a\" UNIQUE (\"a\");", Postgres.AddUnique("s", "t", "uq_t_a", ["a"]));
        Assert.Equal("ALTER TABLE [s].[t] ADD CONSTRAINT [fk_t_a] FOREIGN KEY ([a]) REFERENCES [s].[u] ([id]);", SqlServer.AddForeignKey("s", "t", "fk_t_a", ["a"], "s", "u", ["id"]));
        Assert.Equal("ALTER TABLE \"s\".\"t\" ADD CONSTRAINT \"fk_t_a\" FOREIGN KEY (\"a\") REFERENCES \"s\".\"u\" (\"id\");", Postgres.AddForeignKey("s", "t", "fk_t_a", ["a"], "s", "u", ["id"]));
    }

    [Fact]
    public void Altering_a_column_differs_by_engine_the_way_the_engines_do()
    {
        var c = Col("amount", "numeric(18, 4)", false);
        Assert.Equal("ALTER TABLE [s].[t] ALTER COLUMN [amount] numeric(18, 4) NOT NULL;", SqlServer.AlterColumn("s", "t", c));
        Assert.Equal("ALTER TABLE \"s\".\"t\" ALTER COLUMN \"amount\" TYPE numeric(18, 4), ALTER COLUMN \"amount\" SET NOT NULL;", Postgres.AlterColumn("s", "t", c));
        Assert.Contains("DROP NOT NULL", Postgres.AlterColumn("s", "t", c with { Nullable = true }));
        Assert.Equal("ALTER TABLE [s].[t] ADD [amount] numeric(18, 4) NOT NULL;", SqlServer.AddColumn("s", "t", c));
        Assert.Equal("ALTER TABLE \"s\".\"t\" ADD COLUMN \"amount\" numeric(18, 4) NOT NULL;", Postgres.AddColumn("s", "t", c));
    }

    [Fact]
    public void Renaming_a_column_is_a_stored_procedure_on_sql_server_and_a_statement_on_postgres()
    {
        Assert.Equal("EXEC sp_rename N'[s].[t].[a]', N'b', N'COLUMN';", SqlServer.RenameColumn("s", "t", "a", "b"));
        Assert.Equal("ALTER TABLE \"s\".\"t\" RENAME COLUMN \"a\" TO \"b\";", Postgres.RenameColumn("s", "t", "a", "b"));
        Assert.Contains("N'it''s'", SqlServer.RenameColumn("s", "t", "a", "it's"));       // a quote in a name cannot end the literal
    }

    [Fact]
    public void Schemas_and_views_follow_each_engines_idiom()
    {
        Assert.Equal("IF SCHEMA_ID(N'marts') IS NULL EXEC(N'CREATE SCHEMA [marts]');", SqlServer.CreateSchema("marts"));
        Assert.Equal("CREATE SCHEMA IF NOT EXISTS \"marts\";", Postgres.CreateSchema("marts"));
        var cols = new[] { Col("id", "bigint") };
        Assert.Equal("CREATE OR ALTER VIEW [m].[v] ([id]) AS\nSELECT 1 AS id;", Fabric.CreateOrReplaceView("m", "v", cols, "SELECT 1 AS id;"));
        Assert.Equal("DROP VIEW IF EXISTS \"m\".\"v\";\nCREATE VIEW \"m\".\"v\" (\"id\") AS\nSELECT 1 AS id;", Postgres.CreateOrReplaceView("m", "v", cols, "SELECT 1 AS id"));
    }

    [Fact]
    public void Identifiers_are_quoted_and_quotes_in_them_are_escaped()
    {
        Assert.Contains("[we]]ird]", SqlServer.CreateTable("s", "we]ird", [Col("c", "bigint")]));
        Assert.Contains("\"we\"\"ird\"", Postgres.CreateTable("s", "we\"ird", [Col("c", "bigint")]));
    }

    [Fact]
    public void Index_statements_are_exact_and_postgres_scopes_an_index_to_its_schema()
    {
        var unique = new IndexSpec("uq", true, ["id", "at"], ["amount"]);
        Assert.Equal("CREATE UNIQUE INDEX [uq] ON [marts].[fct] ([id], [at]) INCLUDE ([amount]);", SqlServer.CreateIndex("marts", "fct", unique));
        Assert.Equal("CREATE UNIQUE INDEX \"uq\" ON \"marts\".\"fct\" (\"id\", \"at\") INCLUDE (\"amount\");", Postgres.CreateIndex("marts", "fct", unique));
        Assert.Equal("CREATE INDEX [ix] ON [marts].[fct] ([id]);", SqlServer.CreateIndex("marts", "fct", new IndexSpec("ix", false, ["id"], [])));
        Assert.Equal("DROP INDEX [ix] ON [marts].[fct];", SqlServer.DropIndex("marts", "fct", "ix"));
        Assert.Equal("DROP INDEX \"marts\".\"ix\";", Postgres.DropIndex("marts", "fct", "ix"));
    }

    [Fact]
    public void A_model_name_splits_into_schema_and_name_with_dbo_as_the_default_schema()
    {
        Assert.Equal(("marts", "fct_orders"), TableDdl.Split("marts.fct_orders"));
        Assert.Equal(("a.b", "c"), TableDdl.Split("a.b.c"));
        Assert.Equal(("dbo", "t"), TableDdl.Split("t"));
    }

    [Fact]
    public void An_engine_with_no_shared_dialect_is_refused_by_name()
    {
        Assert.Contains("nowhere", Assert.Throws<ArgumentException>(() => new TSqlTableDdl("nowhere")).Message);
    }

    private static ColumnShape S(string type, int? len = null, int? p = null, int? s = null) => new("c", type, len, p, s, true, null);

    [Fact]
    public void Type_changes_are_classified_for_risk()
    {
        Assert.Equal(TypeChange.None, TableDdl.Classify(S("nvarchar", 20), S("nvarchar", 20)));
        Assert.Equal(TypeChange.Widening, TableDdl.Classify(S("nvarchar", 20), S("nvarchar", 40)));
        Assert.Equal(TypeChange.Widening, TableDdl.Classify(S("nvarchar", 20), S("nvarchar", -1)));
        Assert.Equal(TypeChange.Other, TableDdl.Classify(S("nvarchar", 40), S("nvarchar", 20)));
        Assert.Equal(TypeChange.Other, TableDdl.Classify(S("nvarchar", -1), S("nvarchar", 4000)));
        Assert.Equal(TypeChange.Widening, TableDdl.Classify(S("decimal", null, 14, 2), S("decimal", null, 18, 2)));
        Assert.Equal(TypeChange.Other, TableDdl.Classify(S("decimal", null, 14, 2), S("decimal", null, 18, 4)));
        Assert.Equal(TypeChange.Widening, TableDdl.Classify(S("int"), S("bigint")));
        Assert.Equal(TypeChange.Other, TableDdl.Classify(S("bigint"), S("int")));
        Assert.Equal(TypeChange.Widening, TableDdl.Classify(S("character varying", 20), S("text")));
    }
}
