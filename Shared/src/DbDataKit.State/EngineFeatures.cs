using System.Data.Common;
using System.Globalization;

namespace DbDataKit.State;

/// <summary>
/// A capability of the database engine beyond the floor every module needs (plan 29b, U01). A module lists the ones it cannot work without in <see cref="StateModule.RequiresFeatures"/>. Only a feature some
/// current or planned module needs is listed: the knowledge index's vector search (plan 27 section 2.4) is the first; full-text search is "not built at first" there, and no module needs a JSON index or row-level
/// security yet, so none is declared until one does.
/// </summary>
public enum EngineFeature
{
    /// <summary>Vector columns and similarity search: SQL Server 2025's <c>vector</c> type, PostgreSQL's <c>pgvector</c> extension. SQLite has none here.</summary>
    Vector,
}

/// <summary>How far an engine is from having a feature.</summary>
public enum FeatureStatus
{
    /// <summary>The engine cannot do it (a version too old, an extension that is not installed on the server).</summary>
    Missing,

    /// <summary>The server has it installed but this database has not switched it on (PostgreSQL's extension is in <c>pg_available_extensions</c> and not created): a migration's <c>CREATE EXTENSION</c> turns it on.</summary>
    Creatable,

    /// <summary>Usable now.</summary>
    Enabled,
}

/// <summary>Which features one connected engine has.</summary>
/// <param name="Engine">A <see cref="StateEngineIds"/> value.</param>
/// <param name="Version">The engine's own version text, or null when it was not asked.</param>
/// <param name="Statuses">The status of every <see cref="EngineFeature"/>.</param>
public sealed record EngineFeatureReport(string Engine, string? Version, IReadOnlyDictionary<EngineFeature, FeatureStatus> Statuses)
{
    public FeatureStatus Status(EngineFeature feature) => Statuses.GetValueOrDefault(feature, FeatureStatus.Missing);

    /// <summary>True when a module needing <paramref name="feature"/> can be applied: it is usable, or the extension can be created.</summary>
    public bool Has(EngineFeature feature) => Status(feature) != FeatureStatus.Missing;

    /// <summary>The features this engine has, as <c>vector (ready)</c>; <c>none</c> when it has none.</summary>
    public string Describe()
    {
        var have = Statuses.Where(s => s.Value != FeatureStatus.Missing).Select(s => $"{EngineFeatures.Name(s.Key)} ({(s.Value == FeatureStatus.Enabled ? "ready" : "extension installed, not created yet")})").ToList();
        return have.Count == 0 ? "none" : string.Join(", ", have);
    }
}

/// <summary>Finds what features the connected engine has. The real one asks the engine (<see cref="EngineFeatures.Default"/>); a test passes one that answers as it likes, to show a refusal on an engine that has the feature.</summary>
public interface IEngineFeatureProbe
{
    EngineFeatureReport Detect(DbConnection connection, string engine);
}

/// <summary>What each feature is called, what it needs on each engine, and the one sentence that tells an operator what to change.</summary>
public static class EngineFeatures
{
    /// <summary>SQL Server 2025's major version, the first with the vector type.</summary>
    public const int SqlServerVectorMajor = 17;

    /// <summary>Asks the engine: the version (<c>SERVERPROPERTY</c>, <c>server_version_num</c>) and, on PostgreSQL, <c>pg_available_extensions</c>. Reads only.</summary>
    public static IEngineFeatureProbe Default { get; } = new EngineProbe();

    /// <summary>The name a person says: <c>vector</c>.</summary>
    public static string Name(EngineFeature feature) => feature switch
    {
        EngineFeature.Vector => "vector",
        _ => feature.ToString().ToLowerInvariant(),
    };

    /// <summary>What the feature is on each engine, for a message: <c>SQL Server 2025's vector type</c>.</summary>
    public static string Means(EngineFeature feature, string engine) => (feature, engine) switch
    {
        (EngineFeature.Vector, StateEngineIds.MsSql) => "SQL Server 2025's vector type",
        (EngineFeature.Vector, StateEngineIds.Postgres) => "the pgvector extension",
        (EngineFeature.Vector, _) => "vector columns and search",
        _ => Name(feature),
    };

    /// <summary>The report for an engine whose facts are known: the pure rules (SQL Server 17 and newer has the vector type; PostgreSQL has pgvector as <paramref name="postgresVector"/> says; SQLite has no feature).</summary>
    public static EngineFeatureReport Judge(string engine, string? version, FeatureStatus postgresVector = FeatureStatus.Missing)
    {
        var statuses = new Dictionary<EngineFeature, FeatureStatus>();
        foreach (var feature in Enum.GetValues<EngineFeature>())
            statuses[feature] = (feature, engine) switch
            {
                (EngineFeature.Vector, StateEngineIds.MsSql) => MajorOf(version) >= SqlServerVectorMajor ? FeatureStatus.Enabled : FeatureStatus.Missing,
                (EngineFeature.Vector, StateEngineIds.Postgres) => postgresVector,
                _ => FeatureStatus.Missing,
            };
        return new EngineFeatureReport(engine, version, statuses);
    }

    /// <summary>
    /// The refusal: names the module, the feature and the fix, and says that nothing was created. <paramref name="engineName"/> is how the engine introduced itself (<c>SQL Server 2022 (16.0.4135.4)</c>).
    /// </summary>
    public static string Refusal(StateModule module, EngineFeature feature, string engine, string? engineName)
    {
        var what = Means(feature, engine);
        var found = engineName ?? engine;
        var fix = (feature, engine) switch
        {
            (EngineFeature.Vector, StateEngineIds.MsSql) => "use SQL Server 2025 or later for this install, or pick another engine (PostgreSQL with pgvector)",
            (EngineFeature.Vector, StateEngineIds.Postgres) => "install the pgvector extension on the server (the package for your PostgreSQL version, or the pgvector/pgvector image) so that CREATE EXTENSION vector works, or pick another engine for this install",
            (EngineFeature.Vector, _) => "pick SQL Server 2025 or later, or PostgreSQL with pgvector, for this install",
            _ => $"use an engine that has {Name(feature)}",
        };
        return $"The module '{module.Name}' needs the engine feature '{Name(feature)}' ({what}), and this {found} does not have it; nothing of the module was created. To fix it: {fix}.";
    }

    /// <summary>How the engine is named in a message: <c>SQL Server 2022 (16.0.4135.4)</c>, <c>PostgreSQL 16 (16.4)</c>, <c>SQLite</c>.</summary>
    public static string EngineName(string engine, string? version)
    {
        var major = MajorOf(version);
        var name = engine switch
        {
            StateEngineIds.MsSql => major switch { 17 => "SQL Server 2025", 16 => "SQL Server 2022", 15 => "SQL Server 2019", 14 => "SQL Server 2017", 13 => "SQL Server 2016", > 0 => $"SQL Server {major}.x", _ => "SQL Server" },
            StateEngineIds.Postgres => major > 0 ? $"PostgreSQL {major}" : "PostgreSQL",
            _ => "SQLite",
        };
        return version is null ? name : $"{name} ({version})";
    }

    /// <summary>The first number of a version text (<c>17.0.1000.7</c> gives 17); 0 when there is none.</summary>
    public static int MajorOf(string? version)
    {
        if (version is null) return 0;
        var digits = new string(version.Trim().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ? major : 0;
    }

    private sealed class EngineProbe : IEngineFeatureProbe
    {
        public EngineFeatureReport Detect(DbConnection connection, string engine)
        {
            switch (engine)
            {
                case StateEngineIds.MsSql:
                    return Judge(engine, Convert.ToString(Scalar(connection, "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64));"), CultureInfo.InvariantCulture));
                case StateEngineIds.Postgres:
                    var number = Convert.ToInt32(Scalar(connection, "SHOW server_version_num;"), CultureInfo.InvariantCulture);
                    var version = $"{number / 10000}.{number % 10000}";
                    var installed = Scalar(connection, "SELECT installed_version IS NOT NULL FROM pg_available_extensions WHERE name = 'vector';");
                    var status = installed is null or DBNull ? FeatureStatus.Missing : Convert.ToBoolean(installed, CultureInfo.InvariantCulture) ? FeatureStatus.Enabled : FeatureStatus.Creatable;
                    return Judge(engine, version, status);
                default:
                    return Judge(engine, null);
            }
        }

        private static object? Scalar(DbConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
    }
}
