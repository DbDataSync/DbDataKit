using Microsoft.AnalysisServices.AdomdClient;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Semantic;

namespace DbDataKit.Semantic.Ssas;

/// <summary>
/// Runs the DAX the engine emits on SQL Server Analysis Services (a Tabular model) with ADOMD.NET. Anything that isn't DAX goes to the
/// fallback executor, so one executor can serve a catalog with SQL Server and SSAS sources.
///
/// ADOMD.NET's .NET client runs only on Windows (checked: on Linux both TCP and HTTP connections throw NotSupportedException), so a
/// process that uses this executor has to run on Windows; see docs/ssas.md. This code compiles everywhere and is exercised only when
/// a server is configured (QIS_SSAS in the conformance tests), so until it has met a real server, treat its handling of results as a draft.
/// </summary>
public sealed class SsasExecutor(string connectionString, ISourceExecutor fallback, int commandTimeoutSeconds = 60) : ISourceExecutor
{
    public bool RunsNativeText => fallback.RunsNativeText;

    public QueryRows Execute(CompiledQuery query, int maxRows) =>
        query.Native.Dialect == "dax" ? RunDax(query.Native.Text, maxRows) : fallback.Execute(query, maxRows);

    public QueryRows ExecuteNativeText(string source, NativeQuery native, int maxRows) =>
        native.Dialect == "dax" ? RunDax(native.Text, maxRows) : fallback.ExecuteNativeText(source, native, maxRows);

    private QueryRows RunDax(string dax, int maxRows)
    {
        using var connection = new AdomdConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = dax;
        command.CommandTimeout = commandTimeoutSeconds;
        using var reader = command.ExecuteReader();
        // A column can come back as [name] or name; the engine's names are plain.
        var columns = Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetName(i).Trim('[', ']')).ToList();
        var rows = new List<Dictionary<string, object?>>();
        while (rows.Count < maxRows && reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < columns.Count; i++)
                row[columns[i]] = reader.IsDBNull(i) ? null : Plain(reader.GetValue(i));
            rows.Add(row);
        }
        return new QueryRows(columns, rows);
    }

    /// <summary>A DAX value as the engine expects it: whole numbers as long, a date as an ISO string.</summary>
    private static object Plain(object value) => value switch
    {
        DateTime d when d.TimeOfDay == TimeSpan.Zero => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        double x when x == Math.Truncate(x) && Math.Abs(x) < 1e15 => (long)x,
        _ => value,
    };

    /// <summary>
    /// Creates or replaces the database a TMSL script describes (a <c>createOrReplace</c> command), so the sample model is deployed the same way
    /// every time. The script is <c>ssas/model.tmsl.json</c>.
    /// </summary>
    public static void DeployModel(string connectionString, string tmsl)
    {
        using var connection = new AdomdConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = tmsl;
        command.CommandTimeout = 300;
        command.ExecuteNonQuery();

        // Calculated tables (DATATABLE) hold no data until processed.
        var database = new System.Text.RegularExpressions.Regex("\"database\"\\s*:\\s*\"([^\"]+)\"").Match(tmsl).Groups[1].Value;
        command.CommandText = "{\"refresh\":{\"type\":\"full\",\"objects\":[{\"database\":\"" + database + "\"}]}}";
        command.ExecuteNonQuery();
    }

    /// <summary>Runs one DAX statement and describes the outcome as text (the rows, or the error), for probing what a server accepts.</summary>
    public static string Probe(string connectionString, string dax)
    {
        try
        {
            var rows = new SsasExecutor(connectionString, null!).RunDax(dax, 50);
            return string.Join(" | ", rows.Columns) + "\n" + string.Join("\n", rows.Rows.Select(r => string.Join(" | ", r.Values.Select(v => v ?? "(blank)"))));
        }
        catch (Exception e) { return "ERROR: " + e.Message; }
    }
}
