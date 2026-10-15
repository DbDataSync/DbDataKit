using Xunit;

namespace DbData.Dialect.Conformance;

/// <summary>The minimum a live engine must offer the conformance suite. Implemented by product repos or a docker fixture.</summary>
public interface ILiveEngine : IAsyncDisposable
{
    /// <summary>Executes the query and returns the result column names.</summary>
    Task<IReadOnlyList<string>> ColumnNamesAsync(string sql);
    /// <summary>Executes the query and returns the first column of the first row.</summary>
    Task<object?> ScalarAsync(string sql);
}

/// <summary>
/// Live conformance: identical claims to the offline suite, checked against a real server. Every test is skipped unless the
/// environment variable <see cref="EnvironmentVariable"/> holds a connection string, so CI without the engine stays green and
/// says so. Derive per engine; supply <see cref="CreateDialect"/>, <see cref="Connect"/> and the env var name
/// (convention: <c>DBDATA_CONFORMANCE_&lt;ENGINE&gt;</c>).
/// </summary>
public abstract class LiveDialectConformanceTests
{
    protected abstract string EnvironmentVariable { get; }
    protected abstract ISqlDialect CreateDialect();
    protected abstract Task<ILiveEngine> Connect(string connectionString);

    /// <summary>SELECT text for a constant expression (Oracle needs "FROM dual").</summary>
    protected virtual string SelectExpression(string expression) => "SELECT " + expression;

    private async Task<ILiveEngine> OpenOrSkip()
    {
        string? cs = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(cs), $"live conformance skipped: set {EnvironmentVariable} to a connection string");
        return await Connect(cs!);
    }

    [Fact]
    public async Task Hostile_identifiers_come_back_as_the_same_column_name()
    {
        await using var engine = await OpenOrSkip();
        var d = CreateDialect();
        foreach (var name in DialectConformanceTests.HostileNames)
        {
            if (d.ValidateName(name) is not null) continue;
            string q;
            try { q = d.Quote(name); } catch (NotSupportedException) { continue; }
            var cols = await engine.ColumnNamesAsync(SelectExpression($"1 AS {q}"));
            Assert.Equal([name], cols);
        }
    }

    [Fact]
    public async Task Hostile_string_literals_come_back_unchanged()
    {
        await using var engine = await OpenOrSkip();
        var d = CreateDialect();
        foreach (var s in DialectConformanceTests.HostileStrings.Where(x => x.Length > 0))   // Oracle: '' is NULL
        {
            var got = await engine.ScalarAsync(SelectExpression(d.Literal(s, CanonicalType.String())));
            Assert.Equal(s, got);
        }
    }

    [Fact]
    public async Task Numeric_literals_come_back_equal()
    {
        await using var engine = await OpenOrSkip();
        var d = CreateDialect();
        var got = await engine.ScalarAsync(SelectExpression(d.Literal(12.50m, CanonicalType.Decimal(10, 2))));
        Assert.Equal(12.5m, Convert.ToDecimal(got, System.Globalization.CultureInfo.InvariantCulture));
    }
}
