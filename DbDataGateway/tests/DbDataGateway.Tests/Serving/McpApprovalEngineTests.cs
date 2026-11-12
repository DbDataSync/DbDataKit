using System.Text.Json.Nodes;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;
using DbDataGateway.Tests.Support;
using Microsoft.Data.SqlClient;

namespace DbDataGateway.Tests.Serving;

/// <summary>Plan 20 O12: the approvals of the <c>mcp</c> module in the group's state on SQL Server and PostgreSQL (the agents module's tables, migrated by the Gateway): ask, approve once, a second approval and a late one change nothing, and a denial and an expiry never run.</summary>
public sealed class McpApprovalEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbgateway-mcp-engine-").FullName;

    public void Dispose()
    {
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        Npgsql.NpgsqlConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData("mssql", "DBDATAKIT_TEST_MSSQL")]
    [InlineData("postgres", "DBDATAKIT_TEST_POSTGRES")]
    public async Task A_request_waits_runs_once_when_approved_and_never_when_denied_or_expired(string engine, string variable)
    {
        var server = Environment.GetEnvironmentVariable(variable) ?? Environment.GetEnvironmentVariable(engine == "mssql" ? "DBDATAKIT_TEST_SQLSERVER" : "DBDATAKIT_TEST_POSTGRES");
        if (server is null) Assert.Skip($"set {variable} to run on {engine}");
        var name = $"dbgateway_mcp_{Guid.NewGuid():N}";
        void Admin(string sql)
        {
            using System.Data.Common.DbConnection admin = engine == "mssql" ? new SqlConnection(server) : new Npgsql.NpgsqlConnection(server);
            admin.Open();
            using var cmd = admin.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        Admin(engine == "mssql" ? $"CREATE DATABASE [{name}];" : $"CREATE DATABASE {name};");
        try
        {
            var connection = engine == "mssql" ? new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString : new Npgsql.NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
            var state = GatewayState.Open(_dir, new StateSettings(engine, connection));
            var callers = new StoreCallers(state, "srv", null);
            var runs = 0;
            var tool = new McpTool("touch", "Changes something.", new JsonObject(), true, _ => { Interlocked.Increment(ref runs); return Task.FromResult<JsonNode>(new JsonObject { ["done"] = true }); }, Summary: _ => "Touch.");
            var tools = new Dictionary<string, McpTool> { ["touch"] = tool };
            var approvals = new McpApprovals(state, () => callers, tools, TimeSpan.FromHours(1));
            var expiring = new McpApprovals(state, () => callers, tools, TimeSpan.FromMilliseconds(1));

            StoreCaller Make(string who, ServerRole roles)
            {
                var (user, token) = HomeSeed.Person(state, who, $"{who.ToLowerInvariant()}@example.invalid");
                new HomeDirectory(state).Assign(AssignmentSubject.User(user.Id), "srv", roles);
                return callers.Authenticate("Bearer " + token)!;
            }
            var ada = Make("Ada", ServerRole.Viewer | ServerRole.Author);
            var bea = Make("Bea", ServerRole.Viewer | ServerRole.Approver);

            var first = approvals.Request(ada, tool, []);
            Assert.Equal("pending", approvals.Get(first)!.Status);
            Assert.Equal(0, runs);
            Assert.Equal(200, (await approvals.DecideAsync(bea, first, true, null, default)).Status);
            Assert.Equal(409, (await approvals.DecideAsync(bea, first, true, null, default)).Status);
            Assert.Equal(1, runs);

            var second = approvals.Request(ada, tool, []);
            Assert.Equal("denied", (string)(await approvals.DecideAsync(bea, second, false, null, default)).Body["status"]!);
            Assert.Equal(409, (await approvals.DecideAsync(bea, second, true, null, default)).Status);

            var third = approvals.Request(ada, tool, []);
            await Task.Delay(200);
            Assert.Equal(409, (await expiring.DecideAsync(bea, third, true, null, default)).Status);
            Assert.Equal("expired", approvals.Get(third)!.Status);
            Assert.Equal(1, runs);
            Assert.Equal(["approval-approved", "approval-asked", "approval-asked", "approval-asked", "approval-denied", "tool-called"], new AgentStore(state).Audit(origin: "mcp").Select(a => a.Kind).Order().ToList());
        }
        finally
        {
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            Npgsql.NpgsqlConnection.ClearAllPools();
            Admin(engine == "mssql" ? $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];" : $"DROP DATABASE IF EXISTS {name} WITH (FORCE);");
        }
    }
}
