using System.Globalization;
using System.Numerics;
using System.Text;

namespace DbDataSync.TestSupport;

/// <summary>
/// The loose values a source driver's reader can hand to staging (one per CLR type any reader in this repository returns, plus the edge
/// cases that decide whether a value fits), and the one way a stored cell is written down. Linked into the MsSql and Postgres test projects;
/// each runs every value into every target column type through the real staging provider and compares the outcomes with its checked-in
/// <c>StagingMatrix.*.expected.tsv</c>. See <c>DbDataSync/architecture/staging-value-types.md</c> for what the table means.
/// </summary>
public static class StagingMatrix
{
    public sealed record Value(string Name, object Instance)
    {
        /// <summary>The instance to stage: the same value, but a stream is a new one each time, since staging consumes it.</summary>
        public object Fresh() => Instance is MemoryStream m ? new MemoryStream(m.ToArray()) : Instance;
    }

    public static IReadOnlyList<Value> Values { get; } =
    [
        // integers and booleans, one per CLR type a reader returns
        new("bool", true), new("sbyte", (sbyte)1), new("byte", (byte)1), new("short", (short)1), new("ushort", (ushort)1),
        new("int", 1), new("uint", 1u), new("long", 1L), new("ulong", 1UL), new("BigInteger", new BigInteger(1)),
        // edges of the integers
        new("int:300", 300), new("long:2^40", 1L << 40), new("int:-1", -1), new("ulong:max", ulong.MaxValue),
        // approximate and exact numbers
        new("float", 1.5f), new("double", 1.5), new("decimal", 1.5m), new("decimal:fraction", 1.239m),
        new("double:NaN", double.NaN), new("double:Infinity", double.PositiveInfinity), new("decimal:18digits", 12345678901234567.89m),
        // text
        new("char", 'a'), new("string", "text"), new("string:1", "1"), new("string:1.5", "1.5"), new("string:true", "true"),
        new("string:date", "2024-03-17"), new("string:datetime", "2024-03-17 09:41:22"), new("string:time", "09:41:22"),
        new("string:guid", "6f9619ff-8b86-d011-b42d-00c04fc964ff"), new("string:json", "{\"a\":1}"), new("string:xml", "<a>1</a>"),
        new("string:NUL", "a\0b"), new("string:60chars", new string('x', 60)), new("string:non-BMP", "a\U0001F600b"),
        // binary and identifiers
        new("byte[]", new byte[] { 1, 2, 3 }), new("byte[]:16", new byte[] { 0xff, 0x19, 0x96, 0x6f, 0x86, 0x8b, 0x11, 0xd0, 0xb4, 0x2d, 0x00, 0xc0, 0x4f, 0xc9, 0x64, 0xff }),
        new("Guid", new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff")),
        // dates and times
        new("DateTime", new DateTime(2024, 3, 17, 9, 41, 22, 123, DateTimeKind.Unspecified).AddTicks(4567)),
        new("DateTime:Utc", new DateTime(2024, 3, 17, 9, 41, 22, DateTimeKind.Utc)),
        new("DateTime:midnight", new DateTime(2024, 3, 17)), new("DateTime:1500", new DateTime(1500, 1, 1)),
        new("DateTimeOffset", new DateTimeOffset(2024, 3, 17, 9, 41, 22, TimeSpan.FromHours(2))),
        new("DateOnly", new DateOnly(2024, 3, 17)), new("TimeOnly", new TimeOnly(9, 41, 22, 123)),
        new("TimeSpan", new TimeSpan(0, 9, 41, 22)), new("TimeSpan:3days", new TimeSpan(3, 1, 0, 0)),
        // what DuckDB (BLOB, LIST) and PostgreSQL (arrays) hand over for the types that have no scalar form
        new("Stream", new MemoryStream(new byte[] { 1, 2, 3 })), new("int[]", new[] { 1, 2, 3 }), new("List<int>", new List<int> { 1, 2, 3 }),
    ];

    /// <summary>How a stored cell is written in the tables: its CLR type and its value, culture independent, so a widened or truncated value shows.</summary>
    public static string Describe(object? value) => value switch
    {
        null or DBNull => "NULL",
        bool b => "bool:" + (b ? "true" : "false"),
        byte[] bytes => "byte[]:" + Convert.ToHexString(bytes),
        DateTime d => "DateTime:" + d.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + d.Kind switch { DateTimeKind.Utc => "Z", _ => "" },
        DateTimeOffset o => "DateTimeOffset:" + o.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
        DateOnly d => "DateOnly:" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => "TimeOnly:" + t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        TimeSpan s => "TimeSpan:" + s.ToString("c", CultureInfo.InvariantCulture),
        decimal m => "decimal:" + m.ToString(CultureInfo.InvariantCulture),
        double d => "double:" + d.ToString("R", CultureInfo.InvariantCulture),
        float f => "float:" + f.ToString("R", CultureInfo.InvariantCulture),
        string s => "string:" + Escape(s),
        IFormattable f => value.GetType().Name + ":" + f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.GetType().Name + ":" + value,
    };

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c is < ' ' or > '~' ? $"\\u{(int)c:x4}" : c.ToString());
        return sb.ToString();
    }

    /// <summary>The column every matrix table has; distinctive so that "the message names the column" cannot be an accident of a one-letter name.</summary>
    public const string Column = "matrix_col";

    /// <summary>The line a refusal is written as. The value is never part of it; the exception type and whether the message names the column and the value's type are.</summary>
    public static string Refused(Exception ex, string valueTypeName)
    {
        var text = ex.Message + " " + ex.InnerException?.Message;
        var names = new List<string>();
        if (text.Contains(Column, StringComparison.OrdinalIgnoreCase)) names.Add("column");
        if (text.Contains(valueTypeName, StringComparison.OrdinalIgnoreCase)) names.Add("value type");
        return $"REFUSED {ex.GetType().Name}{(names.Count > 0 ? " (names the " + string.Join(" and ", names) + ")" : "")}";
    }
}

/// <summary>Reads and writes a matrix's checked-in outcomes (<c>target type TAB value TAB outcome</c> per line) and says what differs.</summary>
public sealed class StagingMatrixFile(string path)
{
    public void Write(IEnumerable<string> lines) => File.WriteAllLines(path, lines);

    public string[] Read() => File.Exists(path) ? File.ReadAllLines(path) : [];

    public static bool Update => Environment.GetEnvironmentVariable("DBDATASYNC_MATRIX_UPDATE") == "1";

    public static string Diff(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var e = new HashSet<string>(expected); var a = new HashSet<string>(actual);
        var lines = expected.Where(x => !a.Contains(x)).Select(x => "expected  " + x)
            .Concat(actual.Where(x => !e.Contains(x)).Select(x => "actual    " + x)).Take(60);
        return string.Join(Environment.NewLine, lines);
    }
}
