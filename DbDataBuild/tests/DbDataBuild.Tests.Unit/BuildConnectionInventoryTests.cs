using DbDataBuild.Cli.Server;
using DbDataKit.Connections.Inventory;

namespace DbDataBuild.Tests.Unit;

/// <summary>Plan 28c E23: what a Build server reports as its connections and who uses them.</summary>
public sealed class BuildConnectionInventoryTests : IDisposable
{
    private readonly string project;

    public BuildConnectionInventoryTests()
    {
        project = RefreshPlansTests.Project("refresh: { hourly: incremental }\n", "refresh:\n  plans:\n    hourly: { schedule: \"15 * * * *\", time_zone: UTC }\n");
        Assert.Equal(0, RefreshPlansTests.Compile(project).Exit);
        // the connection files of a project (the fixture declares its connection inline; a real project has the file)
        Directory.CreateDirectory(Path.Combine(project, "connections"));
        File.WriteAllText(Path.Combine(project, "connections", "sqlserver.yaml"), "schema: connection/1\nname: sqlserver\nengine: sqlserver\n");
    }

    public void Dispose() { try { Directory.Delete(project, true); } catch (IOException) { } }

    [Fact]
    public void A_connection_is_listed_with_the_server_the_scheduled_plan_and_the_flows_that_write()
    {
        var connection = Assert.Single(BuildConnectionInventory.Of([("sales", project)]), c => c.Name == "sqlserver");

        Assert.Equal("sqlserver", connection.Engine);
        Assert.Equal("secret:dbdatakit:build:connection:sqlserver:read", connection.SecretRef);
        Assert.Contains(connection.UsedBy, u => u is { Process: "server", Role: "read" });
        Assert.Contains(connection.UsedBy, u => u is { Process: "plan", Name: "sales/sqlserver/hourly", Role: "write" });
        Assert.Contains(connection.UsedBy, u => u is { Process: "flow", Name: "sales: deploy --apply", Role: "write" });
        Assert.Equal(["read", "write"], connection.Roles);
    }

    [Fact]
    public void A_read_only_connection_is_used_by_no_writing_process()
    {
        File.WriteAllText(Path.Combine(project, "connections", "lake.yaml"), "schema: connection/1\nname: lake\nengine: duckdb\naccess: readonly\n");

        var lake = Assert.Single(BuildConnectionInventory.Of([("sales", project)]), c => c.Name == "lake");

        Assert.All(lake.UsedBy, u => Assert.Equal("read", u.Role));
    }

    [Fact]
    public void The_document_holds_references_only()
    {
        var text = ConnectionInventoryJson.Write("dbdatabuild", BuildConnectionInventory.Of([("sales", project)])).ToJsonString();

        Assert.Contains("secret:dbdatakit:build:connection:sqlserver:read", text);
        Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
    }
}
