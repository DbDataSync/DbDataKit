namespace DbDataKit.Cli;

/// <summary>What a command may touch. Printed in --help and in each run header, and the line the management APIs and the MCP server draw to decide what is offered by default.</summary>
public enum EffectClass
{
    OfflineOnly,
    RepoFilesOnly,
    TargetReadOnly,
    TargetWrites,
    TargetDataWrites,
    TrackingTablesOnly,
}

public static class EffectClasses
{
    public static string Describe(this EffectClass e) => e switch
    {
        EffectClass.OfflineOnly => "Offline only",
        EffectClass.RepoFilesOnly => "Repo files only (no target connection)",
        EffectClass.TargetReadOnly => "Target read-only",
        EffectClass.TargetWrites => "Target writes (DDL and/or data, as the plan states)",
        EffectClass.TargetDataWrites => "Target writes (data only)",
        EffectClass.TrackingTablesOnly => "Tracking tables only",
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };
}

/// <summary>
/// One command. <paramref name="Name"/> is its whole path (`connection deploy`); <paramref name="Lane"/> is where it belongs in the life of a deployment (`inspect`, `deploy`, `refresh`, or empty for authoring and reference);
/// <paramref name="Disposition"/> says what it reads and writes in one code: P is the project, C a connection, T its tracking tables, S its structure, D its data, `→` reads, `⇒` writes.
/// </summary>
public sealed record CommandSpec(string Name, EffectClass Effect, string Purpose, bool Implemented, string Lane = "", string Disposition = "")
{
    /// <summary>The words of the path: `connection deploy` is [connection, deploy].</summary>
    public string[] Path => Name.Split(' ');

    /// <summary>What a header says after the effect: the reads-and-writes code and the lane (`reads/writes: P⇒S  |  lane: deploy`).</summary>
    public string Marks => $"reads/writes: {Disposition}" + (Lane.Length > 0 ? $"  |  lane: {Lane}" : "");

    /// <summary>The name as one token, for file names, tool names and schema keys (`connection_deploy`).</summary>
    public string Id => Name.Replace(' ', '_').Replace('-', '_');
}
