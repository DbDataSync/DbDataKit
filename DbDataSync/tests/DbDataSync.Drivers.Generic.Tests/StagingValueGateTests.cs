using System.Net;
using DbDataKit.Dialect;
using DbDataSync.Drivers.Abstractions;
using Xunit;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>
/// The conversion layer's gate (A14a): refuses only what the measured table (<c>architecture/staging-value-types.md</c>) shows is refused for every column
/// of the kind, names the table, column and type but never the value, and lets everything else through to the provider, as before. The engine half
/// (every cell of the matrices) is <c>StagingValueMatrixTests</c> in the MsSql and Postgres test projects.
/// </summary>
public sealed class StagingValueGateTests
{
    private static readonly IReadOnlyDictionary<string, string> Native = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Id"] = "int", ["Name"] = "nvarchar(50)", ["Raw"] = "varbinary(50)", ["Odd"] = "geography", ["At"] = "datetime2(7)",
    };

    private static StagingValueGate Gate(EngineId engine, params string[] columns)
    {
        var schema = StagingBatches.Schema(engine, columns, Native, false, "__Operation");
        return StagingValueGate.For(engine, "dbo.Orders", columns, Native, schema);
    }

    [Theory]
    [InlineData(typeof(int), false)]       // an integer column takes every integer type
    [InlineData(typeof(string), false)]    // and strings (the provider parses them), as it always did
    [InlineData(typeof(double), false)]    // and floating point (the provider rounds them)
    [InlineData(typeof(IPAddress), false)] // a type the table never measured goes to the provider
    [InlineData(typeof(Guid), true)]
    [InlineData(typeof(DateTime), true)]
    [InlineData(typeof(byte[]), true)]
    [InlineData(typeof(System.Numerics.BigInteger), true)]
    [InlineData(typeof(MemoryStream), true)] // any Stream
    public void AnIntegerColumn_RefusesWhatNoIntegerColumnTakes(Type valueType, bool refused) =>
        Assert.Equal(refused, StagingValueRules.Refuses(EngineId.SqlServer, CanonicalType.Of(CanonicalKind.Int32), valueType));

    [Fact]
    public void ABinaryColumn_TakesBytes_AndNothingElseMeasured()
    {
        var binary = CanonicalType.Binary(50);
        Assert.False(StagingValueRules.Refuses(EngineId.SqlServer, binary, typeof(byte[])));
        Assert.True(StagingValueRules.Refuses(EngineId.SqlServer, binary, typeof(string)));
        Assert.True(StagingValueRules.Refuses(EngineId.SqlServer, binary, typeof(int)));
        Assert.False(StagingValueRules.Refuses(EngineId.SqlServer, binary, typeof(IPAddress)));
    }

    [Fact]
    public void TheEnginesDifferWhereTheirProvidersDo()
    {
        // SQL Server converts an integer into a bit column, PostgreSQL's binary COPY refuses it; the table is per engine for that reason
        Assert.False(StagingValueRules.Refuses(EngineId.SqlServer, CanonicalType.Of(CanonicalKind.Boolean), typeof(int)));
        Assert.True(StagingValueRules.Refuses(EngineId.SqlServer, CanonicalType.Of(CanonicalKind.Boolean), typeof(Guid)));
        // and a time-zone timestamp is its own kind: PostgreSQL takes a UTC DateTime into timestamptz, a DateTimeOffset into neither
        Assert.False(StagingValueRules.Refuses(EngineId.Postgres, CanonicalType.Timestamp(tz: true), typeof(DateTime)));
        Assert.True(StagingValueRules.Refuses(EngineId.Postgres, CanonicalType.Of(CanonicalKind.Int32), typeof(string)));
    }

    [Fact]
    public void AnEngineOrAKindTheTableDoesNotKnow_RefusesNothing()
    {
        Assert.False(StagingValueRules.Refuses(EngineId.MySql, CanonicalType.Of(CanonicalKind.Int32), typeof(Guid)));
        Assert.False(StagingValueRules.Refuses(EngineId.SqlServer, CanonicalType.Unmappable, typeof(Guid)));
        Assert.False(StagingValueRules.Refuses(EngineId.Postgres, CanonicalType.Of(CanonicalKind.Interval), typeof(string)));  // left to the provider's own advice
    }

    [Fact]
    public void ARefusal_NamesTheTableTheColumnItsTypeAndTheValuesType_NeverTheValue()
    {
        var gate = Gate(EngineId.SqlServer, "Id", "Name");
        var secret = new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff");

        var ex = Assert.Throws<StagingValueException>(() => gate.Check(0, secret));

        Assert.Equal("dbo.Orders", ex.Table);
        Assert.Equal("Id", ex.Column);
        Assert.Equal(typeof(Guid), ex.ValueType);
        Assert.Contains("dbo.Orders", ex.Message);
        Assert.Contains("'Id'", ex.Message);
        Assert.Contains("(int)", ex.Message);
        Assert.Contains("System.Guid", ex.Message);
        Assert.DoesNotContain(secret.ToString(), ex.Message);
    }

    [Fact]
    public void TheAdvice_IsAppendedWhenTheEngineHasAnotherWay()
    {
        var schema = StagingBatches.Schema(EngineId.Postgres, ["Id"], new Dictionary<string, string> { ["Id"] = "integer" }, false, "__Operation");
        var gate = StagingValueGate.For(EngineId.Postgres, "public.t", ["Id"], new Dictionary<string, string> { ["Id"] = "integer" }, schema, "Use the other Kind.");

        Assert.EndsWith("Use the other Kind.", Assert.Throws<StagingValueException>(() => gate.Check(0, new DateTime(2024, 1, 1))).Message);
    }

    [Fact]
    public void ThroughTheBatchReader_ARefusedCellThrowsOnRead_AndEveryOtherCellIsUntouched()
    {
        var schema = StagingBatches.Schema(EngineId.SqlServer, ["Id", "Name"], Native, false, "__Operation");
        var gate = StagingValueGate.For(EngineId.SqlServer, "dbo.Orders", ["Id", "Name"], Native, schema);
        var source = new ChangeSchema(["Id", "Name"]);
        async IAsyncEnumerable<ChangeRow> Rows()
        {
            yield return new ChangeRow(ChangeOperation.Insert, source, [1, "a"]);
            yield return new ChangeRow(ChangeOperation.Insert, source, [null, DBNull.Value]);
            yield return new ChangeRow(ChangeOperation.Insert, source, [Guid.Empty, "c"]);
            await Task.CompletedTask;
        }
        var batches = StagingBatches.Batch(
            Rows(), schema, ["Id", "Name"], new Dictionary<string, string> { ["Id"] = "Id", ["Name"] = "Name" }, false, new StagingBatches.Counter(), gate);

        var read = new List<object?>();
        Assert.Throws<StagingValueException>(() =>
        {
            var batch = batches.ToBlockingEnumerable().Single();
            using var reader = batch.AsDataReader();
            while (reader.Read())
            {
                read.Add(reader.GetValue(0));
                read.Add(reader.GetValue(1));
            }
        });
        Assert.Equal([1, "a", DBNull.Value, DBNull.Value], read);
    }
}
