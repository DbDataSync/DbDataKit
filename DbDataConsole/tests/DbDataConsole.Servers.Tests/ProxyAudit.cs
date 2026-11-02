using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace DbDataConsole.Servers.Tests;

/// <summary>The management audit of a Proxy that runs in <paramref name="proxyDirectory"/>, oldest first: in its state database (plan 20 O02), as the JSON each entry is kept as.</summary>
internal static class ProxyAudit
{
    public static List<JsonNode> Read(string proxyDirectory)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(proxyDirectory, "state.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Entry FROM ManagementAudit ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        var entries = new List<JsonNode>();
        while (reader.Read()) entries.Add(JsonNode.Parse(reader.GetString(0))!);
        return entries;
    }
}
