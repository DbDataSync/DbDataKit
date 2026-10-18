using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.State;
using DbDataBuild.Targets;
using DbDataBuild.Targets.Ddl;

namespace DbDataBuild.Tests.Unit;

public class DdlGeneratorTests
{
    private static ProjectConfig Config => ProjectConfig.Default with
    {
        StringSemantics = ProjectConfig.Default.StringSemantics with
        {
            Collations = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["default"] = new Dictionary<string, string> { ["duckdb"] = "NOCASE", ["sqlserver"] = "Latin1_General_100_CI_AS", ["fabric"] = "Latin1_General_100_CI_AS_KS_WS_SC_UTF8", ["postgres"] = "en_US.utf8" },
                ["exact"] = new Dictionary<string, string> { ["duckdb"] = "BINARY", ["sqlserver"] = "Latin1_General_100_BIN2", ["postgres"] = "C" },
            },
        },
    };

    private static DdlGenerator Gen(string target) => TargetRegistry.Get(target).CreateDdl(Config);
    private static ColumnDefinition Col(string name, string type, bool nullable = true, string? collation = null) => new(name, type, nullable, collation);

    // logical type, then (declaration, catalog type) for sqlserver, fabric, postgres
    public static TheoryData<string, string, string, string> Mappings => new()
    {
        { "BIGINT", "bigint", "bigint", "bigint" },
        { "INTEGER", "int", "int", "integer" },
        { "INT", "int", "int", "integer" },
        { "SMALLINT", "smallint", "smallint", "smallint" },
        { "TINYINT", "smallint", "smallint", "smallint" },
        { "DOUBLE", "float(53)", "float(53)", "double precision" },
        { "FLOAT", "real", "real", "real" },
        { "BOOLEAN", "bit", "bit", "boolean" },
        { "DATE", "date", "date", "date" },
        { "TIMESTAMP", "datetime2(6)", "datetime2(6)", "timestamp(6)" },
        { "TIME", "time(6)", "time(6)", "time(6)" },
        { "TIMESTAMP WITH TIME ZONE", "datetimeoffset(6)", "datetimeoffset(6)", "timestamp(6) with time zone" },
        { "UUID", "uniqueidentifier", "uniqueidentifier", "uuid" },
        { "BLOB", "varbinary(max)", "varbinary(max)", "bytea" },
        { "DECIMAL(14, 2)", "decimal(14, 2)", "decimal(14, 2)", "numeric(14, 2)" },
        { "decimal(14,2)", "decimal(14, 2)", "decimal(14, 2)", "numeric(14, 2)" },
        { "DECIMAL", "decimal(18, 3)", "decimal(18, 3)", "numeric(18, 3)" },
        { "VARCHAR(20)", "nvarchar(20)", "varchar(20)", "varchar(20)" },
        { "TEXT(20)", "nvarchar(20)", "varchar(20)", "varchar(20)" },
        { "VARCHAR", "nvarchar(max)", "varchar(max)", "text" },
        { "TEXT", "nvarchar(max)", "varchar(max)", "text" },
        { "VARCHAR(5000)", "nvarchar(max)", "varchar(5000)", "varchar(5000)" },
        { "VARCHAR(9000)", "nvarchar(max)", "varchar(max)", "varchar(9000)" },
    };

    [Theory, MemberData(nameof(Mappings))]
    public void Types_map_to_the_documented_native_types(string logical, string sqlserver, string fabric, string postgres)
    {
        Assert.Equal(sqlserver, Gen("sqlserver").Map("m.t", Col("c", logical)).Declaration);
        Assert.Equal(fabric, Gen("fabric").Map("m.t", Col("c", logical)).Declaration);
        Assert.Equal(postgres, Gen("postgres").Map("m.t", Col("c", logical)).Declaration);
    }

    [Theory]
    [InlineData("UBIGINT"), InlineData("HUGEINT"), InlineData("VARCHAR(0)"), InlineData("DECIMAL(40, 2)"), InlineData("DECIMAL(5, 6)"), InlineData("STRUCT(a INTEGER)"), InlineData("INTEGER[]"), InlineData("JSON")]
    public void Types_without_a_faithful_native_form_are_refused_not_guessed(string logical)
    {
        foreach (var target in new[] { "sqlserver", "fabric", "postgres" })
        {
            var ex = Assert.Throws<DdlUnsupportedException>(() => Gen(target).Map("marts.fct", Col("c", logical)));
            Assert.Equal("DDB-321", ex.Diagnostic.Code);
            Assert.Contains("marts.fct", ex.Diagnostic.Found);
        }
    }

    [Fact]
    public void Text_columns_carry_the_profile_collation_and_only_text_columns_do()
    {
        var sql = Gen("sqlserver");
        Assert.Equal("Latin1_General_100_CI_AS", sql.Map("m.t", Col("s", "VARCHAR(10)")).Collation);
        Assert.Equal("Latin1_General_100_BIN2", sql.Map("m.t", Col("s", "VARCHAR(10)", collation: "exact")).Collation);
        Assert.Equal("C", Gen("postgres").Map("m.t", Col("s", "VARCHAR(10)", collation: "exact")).Collation);
        Assert.Null(sql.Map("m.t", Col("n", "BIGINT")).Collation);
        Assert.Equal("Latin1_General_100_CI_AS", sql.Map("m.t", Col("s", "VARCHAR(10)")).Expected.Collation);
    }

    [Fact]
    public void A_missing_collation_for_a_target_is_refused_with_the_collation_code()
    {
        var config = Config with { StringSemantics = Config.StringSemantics with { Collations = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["default"] = new Dictionary<string, string> { ["duckdb"] = "NOCASE" } } } };
        var ex = Assert.Throws<DdlUnsupportedException>(() => TargetRegistry.Get("postgres").CreateDdl(config).Map("m.t", Col("s", "VARCHAR(10)")));
        Assert.Equal("DDB-312", ex.Diagnostic.Code);
        // non-text columns need none
        Assert.Equal("bigint", TargetRegistry.Get("postgres").CreateDdl(config).Map("m.t", Col("n", "BIGINT")).Declaration);
    }

    [Fact]
    public void A_collation_name_cannot_carry_sql()
    {
        var bad = Config with { StringSemantics = Config.StringSemantics with { Collations = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["default"] = new Dictionary<string, string> { ["sqlserver"] = "x; DROP TABLE t" } } } };
        Assert.Throws<DdlUnsupportedException>(() => TargetRegistry.Get("sqlserver").CreateDdl(bad).Map("m.t", Col("s", "VARCHAR(10)")));
    }

    [Theory, InlineData("sqlserver"), InlineData("fabric"), InlineData("postgres")]
    public void Every_statement_shape_parses_with_the_offline_validator(string target)
    {
        var t = TargetRegistry.Get(target);
        var g = Gen(target);
        var cols = new[] { Col("id", "BIGINT", false), Col("label", "VARCHAR(20)"), Col("amount", "DECIMAL(14, 2)"), Col("at", "TIMESTAMP") }.Select(c => g.Map("marts.fct", c)).ToList();
        var statements = new[]
        {
            g.CreateSchema("marts"), g.CreateTable("marts", "fct", cols), g.AddColumn("marts", "fct", cols[1]), g.DropColumn("marts", "fct", "label"),
            g.RenameColumn("marts", "fct", "a", "b"), g.AlterColumn("marts", "fct", cols[1]), g.DropTable("marts", "fct"),
            g.CreateOrReplaceView("marts", "v", cols, "SELECT 1 AS id"),
            g.CreateIndex("marts", "fct", new IndexDefinition("ix", ["id", "at"], false, [], null)),
            g.CreateIndex("marts", "fct", new IndexDefinition("uq", ["id"], true, ["amount"], null)),
            g.DropIndex("marts", "fct", "ix"),
        };
        foreach (var s in statements) Assert.Empty(t.Validate(s, "ddl"));
    }

    [Fact]
    public void Identifiers_are_quoted_and_quotes_in_them_are_escaped()
    {
        Assert.Contains("[we]]ird]", Gen("sqlserver").CreateTable("s", "we]ird", [Gen("sqlserver").Map("s.t", Col("c", "BIGINT"))]));
        Assert.Contains("\"we\"\"ird\"", Gen("postgres").CreateTable("s", "we\"ird", [Gen("postgres").Map("s.t", Col("c", "BIGINT"))]));
        Assert.Contains("N'it''s'", Gen("sqlserver").RenameColumn("s", "t", "a", "it's"));
    }

    [Fact]
    public void Create_table_text_is_exact_for_both_dialect_families()
    {
        var cols = new[] { Col("id", "BIGINT", false), Col("label", "VARCHAR(20)") };
        Assert.Equal("CREATE TABLE [marts].[fct] (\n  [id] bigint NOT NULL,\n  [label] nvarchar(20) COLLATE Latin1_General_100_CI_AS NULL\n);",
            Gen("sqlserver").CreateTable("marts", "fct", cols.Select(c => Gen("sqlserver").Map("marts.fct", c)).ToList()));
        Assert.Equal("CREATE TABLE \"marts\".\"fct\" (\n  \"id\" bigint NOT NULL,\n  \"label\" varchar(20) COLLATE \"en_US.utf8\" NULL\n);",
            Gen("postgres").CreateTable("marts", "fct", cols.Select(c => Gen("postgres").Map("marts.fct", c)).ToList()));
    }

    [Fact]
    public void Index_statements_are_exact_and_quote_their_identifiers()
    {
        var unique = new IndexDefinition("uq", ["id", "at"], true, ["amount"], null);
        Assert.Equal("CREATE UNIQUE INDEX [uq] ON [marts].[fct] ([id], [at]) INCLUDE ([amount]);", Gen("sqlserver").CreateIndex("marts", "fct", unique));
        Assert.Equal("CREATE UNIQUE INDEX \"uq\" ON \"marts\".\"fct\" (\"id\", \"at\") INCLUDE (\"amount\");", Gen("postgres").CreateIndex("marts", "fct", unique));
        Assert.Equal("CREATE INDEX [ix] ON [marts].[fct] ([id]);", Gen("sqlserver").CreateIndex("marts", "fct", new IndexDefinition("ix", ["id"], false, [], null)));
        Assert.Equal("DROP INDEX [ix] ON [marts].[fct];", Gen("sqlserver").DropIndex("marts", "fct", "ix"));
        Assert.Equal("DROP INDEX \"marts\".\"ix\";", Gen("postgres").DropIndex("marts", "fct", "ix"));      // PostgreSQL index names live in the schema
        Assert.Contains("[we]]ird]", Gen("sqlserver").CreateIndex("s", "t", new IndexDefinition("we]ird", ["c"], false, [], null)));
    }

    private static ColumnShape S(string type, int? len = null, int? p = null, int? s = null) => new("c", type, len, p, s, true, null);

    [Fact]
    public void Type_changes_are_classified_for_risk()
    {
        Assert.Equal(TypeChange.None, DdlGenerator.Classify(S("nvarchar", 20), S("nvarchar", 20)));
        Assert.Equal(TypeChange.Widening, DdlGenerator.Classify(S("nvarchar", 20), S("nvarchar", 40)));
        Assert.Equal(TypeChange.Widening, DdlGenerator.Classify(S("nvarchar", 20), S("nvarchar", -1)));
        Assert.Equal(TypeChange.Other, DdlGenerator.Classify(S("nvarchar", 40), S("nvarchar", 20)));      // narrowing
        Assert.Equal(TypeChange.Other, DdlGenerator.Classify(S("nvarchar", -1), S("nvarchar", 4000)));    // max to sized
        Assert.Equal(TypeChange.Widening, DdlGenerator.Classify(S("decimal", null, 14, 2), S("decimal", null, 18, 2)));
        Assert.Equal(TypeChange.Other, DdlGenerator.Classify(S("decimal", null, 14, 2), S("decimal", null, 18, 4)));   // scale change
        Assert.Equal(TypeChange.Other, DdlGenerator.Classify(S("decimal", null, 18, 2), S("decimal", null, 14, 2)));
        Assert.Equal(TypeChange.Widening, DdlGenerator.Classify(S("int"), S("bigint")));
        Assert.Equal(TypeChange.Widening, DdlGenerator.Classify(S("integer"), S("bigint")));
        Assert.Equal(TypeChange.Other, DdlGenerator.Classify(S("bigint"), S("int")));
        Assert.Equal(TypeChange.Other, DdlGenerator.Classify(S("int"), S("nvarchar", 20)));
        Assert.Equal(TypeChange.Other, DdlGenerator.Classify(S("date"), S("datetime2", null, null, 6)));
    }
}
