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

    // A14b: strict at the type level. A pair is refused when the provider would store a different value; neighbours that are value-fit questions pass.
    [Theory]
    [InlineData(CanonicalKind.Date, typeof(int), true)]
    [InlineData(CanonicalKind.Date, typeof(ushort), true)]
    [InlineData(CanonicalKind.Date, typeof(DateTime), false)]   // the neighbour that must still pass
    [InlineData(CanonicalKind.Date, typeof(DateOnly), false)]
    [InlineData(CanonicalKind.Time, typeof(uint), true)]
    [InlineData(CanonicalKind.Time, typeof(long), true)]
    [InlineData(CanonicalKind.Time, typeof(TimeSpan), false)]
    [InlineData(CanonicalKind.Time, typeof(TimeOnly), false)]
    [InlineData(CanonicalKind.Timestamp, typeof(uint), true)]
    [InlineData(CanonicalKind.Timestamp, typeof(long), true)]
    [InlineData(CanonicalKind.Timestamp, typeof(DateTime), false)]
    [InlineData(CanonicalKind.String, typeof(byte[]), true)]
    [InlineData(CanonicalKind.String, typeof(MemoryStream), true)]
    [InlineData(CanonicalKind.String, typeof(char), false)]
    [InlineData(CanonicalKind.String, typeof(string), false)]
    [InlineData(CanonicalKind.Int32, typeof(double), false)]    // rounding is a value-fit question: stays lenient
    [InlineData(CanonicalKind.Int32, typeof(int), false)]
    public void PostgresRefusesWhatItWouldStoreAsADifferentValue(CanonicalKind kind, Type valueType, bool refused) =>
        Assert.Equal(refused, StagingValueRules.Refuses(EngineId.Postgres, CanonicalType.Of(kind), valueType));

    [Fact]
    public void PostgresTimestampWithTimeZone_RefusesIntegersAsMicroseconds_ButTakesAUtcDateTime()
    {
        var tz = CanonicalType.Timestamp(tz: true);
        Assert.True(StagingValueRules.Refuses(EngineId.Postgres, tz, typeof(long)));
        Assert.True(StagingValueRules.Refuses(EngineId.Postgres, tz, typeof(uint)));
        Assert.False(StagingValueRules.Refuses(EngineId.Postgres, tz, typeof(DateTime)));
    }

    [Theory]
    [InlineData(CanonicalKind.Int8, typeof(char), true)]
    [InlineData(CanonicalKind.Int16, typeof(char), true)]
    [InlineData(CanonicalKind.Int32, typeof(char), true)]
    [InlineData(CanonicalKind.Int64, typeof(char), true)]
    [InlineData(CanonicalKind.Int32, typeof(bool), false)]      // true as 1 is a convention: lenient
    [InlineData(CanonicalKind.Int32, typeof(double), false)]
    [InlineData(CanonicalKind.Int32, typeof(string), false)]
    [InlineData(CanonicalKind.String, typeof(char), false)]
    [InlineData(CanonicalKind.Boolean, typeof(int), false)]     // a number into bit stays lenient
    [InlineData(CanonicalKind.Date, typeof(string), false)]     // text that parses as a date stays lenient
    [InlineData(CanonicalKind.Date, typeof(int), false)]        // SQL Server's provider refuses it itself: an engine refusal, not a reinterpretation
    public void SqlServerRefusesACharacterAsItsCodePoint_AndNothingMoreThanThat(CanonicalKind kind, Type valueType, bool refusedAsReinterpreted) =>
        Assert.Equal(refusedAsReinterpreted, StagingValueRules.Verdict(EngineId.SqlServer, CanonicalType.Of(kind), valueType) == StagingValueRules.Outcome.Reinterpreted);

    [Fact]
    public void AnIntegerIntoAPostgresDate_IsRefusedAsAReinterpretation_AndTheMessageNamesTableColumnAndTypeButNotTheValue()
    {
        var types = new Dictionary<string, string> { ["Due"] = "date" };
        var schema = StagingBatches.Schema(EngineId.Postgres, ["Due"], types, false, "__Operation");
        var gate = StagingValueGate.For(EngineId.Postgres, "public.invoices", ["Due"], types, schema, "Use the other Kind.");

        var ex = Assert.Throws<StagingValueException>(() => gate.Check(0, 8675309));

        Assert.True(ex.Reinterpreted);
        Assert.Equal(typeof(int), ex.ValueType);
        Assert.Contains("public.invoices", ex.Message);
        Assert.Contains("'Due'", ex.Message);
        Assert.Contains("(date)", ex.Message);
        Assert.Contains("System.Int32", ex.Message);
        Assert.Contains("different value", ex.Message);
        Assert.Contains("Transform", ex.Message);
        Assert.DoesNotContain("8675309", ex.Message);
        Assert.DoesNotContain("Use the other Kind.", ex.Message); // the other Kind's behaviour for these pairs was not measured: no advice
    }

    [Fact]
    public void ARefusedTypeStaysRefused_AndALenientNeighbourStillPassesOnTheSameColumn()
    {
        var types = new Dictionary<string, string> { ["Due"] = "date" };
        var schema = StagingBatches.Schema(EngineId.Postgres, ["Due"], types, false, "__Operation");
        var gate = StagingValueGate.For(EngineId.Postgres, "public.invoices", ["Due"], types, schema);

        gate.Check(0, new DateTime(2024, 3, 17));
        gate.Check(0, new DateOnly(2024, 3, 17));
        Assert.Throws<StagingValueException>(() => gate.Check(0, 1));
        Assert.Throws<StagingValueException>(() => gate.Check(0, 1));
        gate.Check(0, new DateOnly(2024, 3, 18));
    }

    [Fact]
    public void AnEngineRefusal_IsNotMarkedAsAReinterpretation()
    {
        var gate = Gate(EngineId.SqlServer, "Id");
        var ex = Assert.Throws<StagingValueException>(() => gate.Check(0, Guid.Empty));
        Assert.False(ex.Reinterpreted);
        Assert.Contains("refuses that type", ex.Message);
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
