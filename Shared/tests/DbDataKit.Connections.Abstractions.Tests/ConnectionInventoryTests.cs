using System.Text.Json.Nodes;
using DbDataKit.Connections.Inventory;

namespace DbDataKit.Connections.Abstractions.Tests;

public sealed class ConnectionInventoryTests
{
    private static readonly ConnectionUse Server = new(ProcessKinds.Server, "this server", UseRoles.Read);
    private static readonly ConnectionUse Task = new(ProcessKinds.Task, "nightly/load", UseRoles.Run, "3", "main", "7f1", "production");

    [Fact]
    public void A_value_where_a_reference_belongs_is_never_echoed()
    {
        var typed = new InventoryConnection("erp", "sqlserver", "sqlserver", "user:hunter2@db.example", "Erp", "hunter2", [Server]);
        var text = ConnectionInventoryJson.Write("dbdatasync", [typed]).ToJsonString();
        Assert.DoesNotContain("hunter2", text);
        Assert.Contains("(not a reference)", text);
        Assert.Contains("db.example", text);
    }

    [Fact]
    public void A_reference_is_kept_and_a_dpapi_blob_is_elided()
    {
        Assert.Equal("env:ERP_DB_PASSWORD", ConnectionInventoryJson.Reference("env:ERP_DB_PASSWORD"));
        Assert.Equal("dpapi:<blob>", ConnectionInventoryJson.Reference("dpapi:AQAAANCMnd8BFdERjHoAwE"));
        Assert.Null(ConnectionInventoryJson.Reference("  "));
    }

    [Fact]
    public void The_document_reads_back_with_its_users_and_roles()
    {
        var node = ConnectionInventoryJson.Write("dbdatarunner", [new InventoryConnection("warehouse", "sql", null, null, null, "secret:dbdatakit:runner:connection:warehouse", [Task, Server])]);
        var back = Assert.Single(ConnectionInventoryJson.Read(JsonNode.Parse(node.ToJsonString())));
        Assert.Equal("warehouse", back.Name);
        Assert.Equal(["read", "run"], back.Roles);
        Assert.Contains(back.UsedBy, u => u is { Process: "task", Name: "nightly/load", Version: "3", Instance: "7f1", Channel: "production" });
    }

    [Fact]
    public void Removal_is_refused_naming_the_users_but_not_for_the_server_alone()
    {
        Assert.Null(ConnectionUsers.RemovalRefusal("erp", [Server]));
        var refusal = ConnectionUsers.RemovalRefusal("erp", [Server, new ConnectionUse(ProcessKinds.Replication, "orders", UseRoles.Run)]);
        Assert.Equal("The connection 'erp' is used by replication orders: change it to use another connection first, then remove it.", refusal);
    }
}
