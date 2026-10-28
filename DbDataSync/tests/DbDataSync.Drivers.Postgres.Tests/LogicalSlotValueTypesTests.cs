using System.Runtime.CompilerServices;
using DbDataSync.TestSupport;
using Xunit;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>
/// What the logical-slot reader (<c>wal2json</c>, which writes every value as a JSON string, number or boolean) turns a value into, per declared type:
/// <see cref="PostgresValues.FromText(string,string)"/> is that conversion, so this runs it without a server. Note the differences from the table in
/// <c>ReaderTypesTests</c> for the same column: <c>date</c> is a <c>DateOnly</c> here and a <c>DateTime</c> there, <c>timestamptz</c> a
/// <c>DateTimeOffset</c> with the offset the text carried.
/// </summary>
public sealed class LogicalSlotValueTypesTests
{
    private static readonly (string NativeType, string Text)[] Cases =
    [
        ("smallint", "-300"), ("integer", "70000"), ("bigint", "5000000000"), ("boolean", "true"), ("boolean", "t"),
        ("numeric(18,2)", "12345.67"), ("numeric", "123456789012345678901234567890.5"), ("money", "12.34"), ("real", "1.5"), ("double precision", "1.5"),
        ("text", "text"), ("character varying(50)", "text"), ("character(5)", "ab"), ("name", "text"),
        ("bytea", "\\x010203"), ("uuid", "6f9619ff-8b86-d011-b42d-00c04fc964ff"),
        ("date", "2024-03-17"), ("time", "09:41:22.123456"), ("timetz", "09:41:22.123456+02"),
        ("timestamp", "2024-03-17 09:41:22.123456"), ("timestamptz", "2024-03-17 09:41:22.123456+02"), ("timestamptz", "2024-03-17 09:41:22.123456+00"),
        ("interval", "1 day 02:03:04"), ("json", "{\"a\":1}"), ("jsonb", "{\"a\":1}"), ("xml", "<a>1</a>"), ("inet", "192.168.0.1"), ("integer[]", "{1,2,3}"),
    ];

    private static string ExpectedPath([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "ReaderTypes.postgres-logical-slot.expected.tsv");

    [Fact]
    public void EveryDeclaredTypeReadsBackAsTheTableSays()
    {
        var actual = new List<string>();
        foreach (var (nativeType, text) in Cases)
        {
            string line;
            try
            {
                var value = PostgresValues.FromText(nativeType, text);
                line = $"{nativeType} from '{text}'\t-\t{value.GetType().Name}\t{StagingMatrix.Describe(value)}";
            }
            catch (Exception ex) { line = $"{nativeType} from '{text}'\t-\t-\tTHROWS {ex.GetType().Name}"; }
            actual.Add(line);
        }

        var file = new StagingMatrixFile(ExpectedPath());
        if (StagingMatrixFile.Update) { file.Write(actual); return; }
        var expected = file.Read();
        Assert.True(expected.SequenceEqual(actual), "logical-slot value types changed (DBDATASYNC_MATRIX_UPDATE=1 regenerates the file):" + Environment.NewLine + StagingMatrixFile.Diff(expected, actual));
    }
}
