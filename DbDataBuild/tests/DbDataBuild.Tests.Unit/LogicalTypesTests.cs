using DbDataBuild.Core.Questions;
using DbDataBuild.Define;

namespace DbDataBuild.Tests.Unit;

public class LogicalTypesTests
{
    public sealed record Case(string DuckType, string? Logical, ProposalCertainty? Certainty)
    {
        public override string ToString() => DuckType;
    }

    // The enumerated mapping (DESIGN.md principle 7): every DuckDB type the table knows, and a representative of each class it does not.
    public static readonly Case[] Cases =
    [
        new("BIGINT", "BIGINT", ProposalCertainty.High),
        new("INTEGER", "INTEGER", ProposalCertainty.High),
        new("SMALLINT", "SMALLINT", ProposalCertainty.High),
        new("DOUBLE", "DOUBLE", ProposalCertainty.High),
        new("BOOLEAN", "BOOLEAN", ProposalCertainty.High),
        new("DATE", "DATE", ProposalCertainty.High),
        new("TIMESTAMP", "TIMESTAMP", ProposalCertainty.High),
        new("TIME", "TIME", ProposalCertainty.High),
        new("DECIMAL(14,2)", "DECIMAL(14, 2)", ProposalCertainty.High),
        new("DECIMAL(18, 3)", "DECIMAL(18, 3)", ProposalCertainty.High),
        new("TINYINT", "TINYINT", ProposalCertainty.Normal),
        new("FLOAT", "FLOAT", ProposalCertainty.Normal),
        new("UBIGINT", "UBIGINT", ProposalCertainty.Normal),
        new("TIMESTAMP WITH TIME ZONE", "TIMESTAMP WITH TIME ZONE", ProposalCertainty.Normal),
        new("BLOB", "BLOB", ProposalCertainty.Normal),
        new("UUID", "UUID", ProposalCertainty.Normal),
        new("VARCHAR", "VARCHAR", ProposalCertainty.High),      // unlimited text stays unlimited
        new("HUGEINT", null, null),
        new("INTERVAL", null, null),
        new("JSON", null, null),
        new("BIGINT[]", null, null),
        new("STRUCT(a INTEGER)", null, null),
        new("MAP(VARCHAR, INTEGER)", null, null),
    ];

    public static TheoryData<Case> Data
    {
        get { var d = new TheoryData<Case>(); foreach (var c in Cases) d.Add(c); return d; }
    }

    [Theory, MemberData(nameof(Data))]
    public void Duckdb_types_map_to_logical_types_with_a_certainty(Case c)
    {
        var p = LogicalTypes.FromDuckDb(c.DuckType);
        Assert.Equal((c.Logical, c.Certainty), (p.LogicalType, p.Certainty));
        Assert.False(string.IsNullOrWhiteSpace(p.Reason));
    }

    [Fact]
    public void Only_clean_target_neutral_types_are_high_certainty()
    {
        Assert.All(LogicalTypes.ExactMapping.Where(kv => kv.Value == ProposalCertainty.High).Select(kv => kv.Key),
            t => Assert.Contains(t, new[] { "BIGINT", "INTEGER", "SMALLINT", "DOUBLE", "BOOLEAN", "DATE", "TIMESTAMP", "TIME" }));
    }

    [Theory]
    [InlineData("TEXT(20)", "VARCHAR(20)")]
    [InlineData("VARCHAR(5)", "VARCHAR(5)")]
    [InlineData("TEXT", null)]
    [InlineData("INTEGER", null)]
    [InlineData(null, null)]
    public void A_written_sized_text_cast_is_an_explicit_length(string? castType, string? expected) =>
        Assert.Equal(expected, LogicalTypes.SizedVarchar(castType));

    [Theory]
    [InlineData("decimal(14,2)", "DECIMAL(14, 2)")]
    [InlineData("  Decimal ( 14 ,  2 ) ", "DECIMAL(14, 2)")]
    [InlineData("timestamp  with  time zone", "TIMESTAMP WITH TIME ZONE")]
    [InlineData("varchar(20)", "VARCHAR(20)")]
    public void Normalize_gives_the_canonical_spelling(string input, string expected) => Assert.Equal(expected, LogicalTypes.Normalize(input));

    [Theory]
    [InlineData("INT", "INTEGER", true)]
    [InlineData("int4", "INTEGER", true)]
    [InlineData("BIGINT", "BIGINT", true)]
    [InlineData("INTEGER", "BIGINT", false)]
    [InlineData("BOOL", "BOOLEAN", true)]
    [InlineData("DATETIME", "TIMESTAMP", true)]
    [InlineData("DECIMAL(14, 2)", "DECIMAL(14,2)", true)]
    [InlineData("NUMERIC(14, 2)", "DECIMAL(14,2)", true)]
    [InlineData("DECIMAL(14, 2)", "DECIMAL(14,3)", false)]
    [InlineData("DECIMAL(14, 2)", "DECIMAL(18,2)", false)]
    [InlineData("DECIMAL", "DECIMAL(18,3)", true)]
    [InlineData("DECIMAL(10)", "DECIMAL(10,0)", true)]
    [InlineData("VARCHAR(20)", "VARCHAR", true)]          // DuckDB cannot report a length
    [InlineData("VARCHAR(20)", "VARCHAR(20)", true)]
    [InlineData("VARCHAR(20)", "VARCHAR(30)", false)]
    [InlineData("TEXT", "VARCHAR", true)]
    [InlineData("VARCHAR", "VARCHAR(20)", false)]         // a declared bare VARCHAR does not match a sized one
    [InlineData("DATE", "TIMESTAMP", false)]
    [InlineData("TIMESTAMP WITH TIME ZONE", "TIMESTAMPTZ", true)]
    [InlineData("BIGINT[]", "BIGINT[]", true)]
    [InlineData("BIGINT[]", "INTEGER[]", false)]
    public void Equivalent_decides_whether_two_spellings_are_the_same_type(string declared, string resolved, bool equal)
    {
        Assert.Equal(equal, LogicalTypes.Equivalent(declared, resolved));
        if (declared != "VARCHAR(20)" || resolved != "VARCHAR")   // equivalence is symmetric except for DuckDB's missing VARCHAR length
            if (!(declared == "VARCHAR" && resolved == "VARCHAR(20)")) Assert.Equal(equal, LogicalTypes.Equivalent(resolved, declared));
    }
}
