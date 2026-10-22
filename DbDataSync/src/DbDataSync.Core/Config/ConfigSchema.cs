namespace DbDataSync.Core.Config;

/// <summary>The format version Sync writes into every connection, replication, table-mapping and script file, and the only one it reads.</summary>
internal static class ConfigSchema
{
    public const int Current = 1;
}
