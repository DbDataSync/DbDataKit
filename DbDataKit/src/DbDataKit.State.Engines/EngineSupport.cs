using System.Data.Common;
using System.Globalization;

namespace DbDataKit.State;

/// <summary>What <see cref="EngineSupport.Check"/> says about one engine version.</summary>
/// <param name="Supported">False for a version older than the oldest this release supports.</param>
/// <param name="Name">The engine and its release as a person says it: <c>PostgreSQL 17</c>, <c>SQL Server 2025</c>.</param>
/// <param name="Message">One sentence: what was found and, when it is not supported, what to do.</param>
/// <param name="Tested">False for a version newer than the newest this release was tested on: it is allowed, and said.</param>
public sealed record EngineVersionCheck(bool Supported, string Name, string Message, bool Tested = true);

/// <summary>
/// The engine versions the state layer supports (plan 29b, C01): SQL Server 2022 and 2025, PostgreSQL 16 and 17, SQLite 3.35 and newer. Older ones are refused with a message naming the oldest that works; a newer one is
/// allowed and noted as untested. The version table is the floor; what a module needs beyond it (<see cref="EngineFeature"/>: pgvector, SQL Server 2025's vector type) is <see cref="Features(string, string?, FeatureStatus)"/> and the migrator's refusal.
/// </summary>
public static class EngineSupport
{
    private static readonly (int Major, string Name)[] SqlServerReleases = [(10, "SQL Server 2008"), (11, "SQL Server 2012"), (12, "SQL Server 2014"), (13, "SQL Server 2016"), (14, "SQL Server 2017"), (15, "SQL Server 2019"), (16, "SQL Server 2022"), (17, "SQL Server 2025")];

    public const int OldestSqlServerMajor = 16;
    public const int NewestSqlServerMajor = 17;
    public const int OldestPostgresMajor = 16;
    public const int NewestPostgresMajor = 17;
    public static readonly Version OldestSqlite = new(3, 35);

    /// <summary>Judges <paramref name="version"/> as the engine reports it (<c>17.2</c>, <c>16.0.4135.4</c>, <c>3.45.1</c>) for <paramref name="engine"/> (a <see cref="StateEngineIds"/> value).</summary>
    public static EngineVersionCheck Check(string engine, string version)
    {
        if (!Version.TryParse(version.Trim().Split(' ', '-')[0], out var parsed) && !TryMajor(version, out parsed))
            return new EngineVersionCheck(false, engine, $"The engine reported the version '{version}', which is not a version number; use a supported release: {Supported(engine)}.");
        return engine switch
        {
            StateEngineIds.MsSql => Server(parsed.Major, version, SqlServerReleases.FirstOrDefault(r => r.Major == parsed.Major).Name ?? $"SQL Server {parsed.Major}.x", OldestSqlServerMajor, NewestSqlServerMajor, "SQL Server 2022", "SQL Server 2025"),
            StateEngineIds.Postgres => Server(parsed.Major, version, $"PostgreSQL {parsed.Major}", OldestPostgresMajor, NewestPostgresMajor, "PostgreSQL 16", "PostgreSQL 17"),
            StateEngineIds.Sqlite => parsed >= OldestSqlite
                ? new EngineVersionCheck(true, $"SQLite {parsed}", $"SQLite {parsed} is supported.")
                : new EngineVersionCheck(false, $"SQLite {parsed}", $"SQLite {parsed} is older than 3.35, the oldest this release supports; use a build of the product with a newer SQLite or another engine (the library comes with the product, so update the product)."),
            _ => new EngineVersionCheck(false, engine, $"The engine '{engine}' is not one of sqlite, mssql or postgres."),
        };
    }

    /// <summary>
    /// The features an engine of this version has (plan 29b, U01): SQL Server 2025 (17) and newer has the vector type; PostgreSQL has pgvector when <paramref name="postgresVector"/> says the extension is installed
    /// (<see cref="FeatureStatus.Enabled"/>) or available to create (<see cref="FeatureStatus.Creatable"/>), which only the server can tell (<see cref="Features(DbConnection, string, IEngineFeatureProbe?)"/> asks it); SQLite has none.
    /// </summary>
    public static EngineFeatureReport Features(string engine, string? version, FeatureStatus postgresVector = FeatureStatus.Missing) => EngineFeatures.Judge(engine, version, postgresVector);

    /// <summary>Asks the connected engine which features it has: its version, and on PostgreSQL <c>pg_available_extensions</c>. Reads only.</summary>
    public static EngineFeatureReport Features(DbConnection connection, string engine, IEngineFeatureProbe? probe = null) => (probe ?? EngineFeatures.Default).Detect(connection, engine);

    /// <summary>The modules among <paramref name="modules"/> that need a feature <paramref name="report"/> says the engine lacks, each as the sentence the migrator refuses with.</summary>
    public static IReadOnlyList<(StateModule Module, EngineFeature Feature, string Message)> Unmet(IEnumerable<StateModule> modules, EngineFeatureReport report) =>
    [
        .. modules.SelectMany(m => m.RequiresFeatures.Where(f => !report.Has(f)).Select(f => (m, f, EngineFeatures.Refusal(m, f, report.Engine, EngineFeatures.EngineName(report.Engine, report.Version))))),
    ];

    private static EngineVersionCheck Server(int major, string version, string name, int oldest, int newest, string oldestName, string newestName)
    {
        if (major < oldest)
            return new EngineVersionCheck(false, name, $"{name} ({version}) is older than {oldestName}, the oldest this release supports; upgrade the server to {oldestName} or {newestName}, or use another database.");
        if (major > newest)
            return new EngineVersionCheck(true, name, $"{name} ({version}) is newer than {newestName}, the newest this release was tested on; it is allowed.", Tested: false);
        return new EngineVersionCheck(true, name, $"{name} ({version}) is supported.");
    }

    private static bool TryMajor(string text, out Version version)
    {
        version = new Version(0, 0);
        var digits = new string(text.Trim().TakeWhile(char.IsDigit).ToArray());
        if (digits.Length == 0 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var major)) return false;
        version = new Version(major, 0);
        return true;
    }

    private static string Supported(string engine) => engine switch
    {
        StateEngineIds.MsSql => "SQL Server 2022 or 2025",
        StateEngineIds.Postgres => "PostgreSQL 16 or 17",
        _ => "SQLite 3.35 or newer",
    };
}
