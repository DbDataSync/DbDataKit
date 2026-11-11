using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Plan 28c, E20, against a real SQL Server with two real logins: the <c>read</c> login may only read, the <c>run</c> login owns the database. What a connection does for the server (browsing, a test) works with the read login alone, and what a pass does (a write) is the run login's.
/// The proof that the roles are not mixed is made by breaking the other login: a connection whose run password is wrong still browses, and one whose read password is wrong still opens for a pass.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CredentialRolesIntegrationTests(TestApiFactory factory) : IClassFixture<TestApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private const string LoginPassword = "E20_Test_Pw1!x";

    private readonly HttpClient _client = factory.CreateClient();
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..10];
    private string Database => $"E20Roles_{_suffix}";
    private string ReadLogin => $"e20r_{_suffix}";
    private string RunLogin => $"e20w_{_suffix}";

    private sealed record Report(bool Succeeded, string? Error);

    public async Task InitializeAsync()
    {
        await using var server = new SqlConnection(TestEngines.MsSql.ConnectionString);
        await server.OpenAsync();
        await ExecuteAsync(server, $"CREATE DATABASE [{Database}]; CREATE LOGIN [{ReadLogin}] WITH PASSWORD = '{LoginPassword}', CHECK_POLICY = OFF; CREATE LOGIN [{RunLogin}] WITH PASSWORD = '{LoginPassword}', CHECK_POLICY = OFF;");
        var scoped = new SqlConnectionStringBuilder(TestEngines.MsSql.ConnectionString) { InitialCatalog = Database }.ConnectionString;
        await using var db = new SqlConnection(scoped);
        await db.OpenAsync();
        await ExecuteAsync(db, $"CREATE TABLE dbo.Probe (Id INT NOT NULL PRIMARY KEY); CREATE USER [{ReadLogin}] FOR LOGIN [{ReadLogin}]; ALTER ROLE db_datareader ADD MEMBER [{ReadLogin}]; CREATE USER [{RunLogin}] FOR LOGIN [{RunLogin}]; ALTER ROLE db_owner ADD MEMBER [{RunLogin}];");
    }

    public async Task DisposeAsync()
    {
        await using var server = new SqlConnection(TestEngines.MsSql.ConnectionString);
        await server.OpenAsync();
        await MsSqlScratch.DropDatabaseAsync(server, Database);
        SqlConnection.ClearAllPools();
        await ExecuteAsync(server, $"DROP LOGIN [{ReadLogin}]; DROP LOGIN [{RunLogin}];");
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> SaveAsync(string readPassword, string? runPassword, string? runUser = null)
    {
        var name = $"roles-{Guid.NewGuid():N}";
        var response = await _client.PutAsJsonAsync($"/api/connections/{name}", new ConnectionInput
        {
            Name = name, DriverType = DriverIds.MsSql, Host = "localhost", Port = TestEngines.MsSql.Port, Database = Database,
            Properties = new Dictionary<string, string> { ["TrustServerCertificate"] = "true" },
            AuthMode = AuthMode.SqlAuth, UserId = ReadLogin, Password = readPassword,
            RunLogin = runPassword is null ? null : new RunLoginInput { UserId = runUser ?? RunLogin, Password = runPassword },
        }, JsonOptions);
        response.EnsureSuccessStatusCode();
        return name;
    }

    private async Task<string> WhoAsync(string name, CredentialRole role)
    {
        var (connection, _) = await factory.Services.GetRequiredService<DriverConnectionFactory>().OpenAsync(name, role, CancellationToken.None);
        await using (connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT SUSER_SNAME()";
            return (string)(await command.ExecuteScalarAsync())!;
        }
    }

    [Fact]
    public async Task Each_role_connects_as_its_own_login_and_only_the_run_login_may_write()
    {
        var name = await SaveAsync(LoginPassword, LoginPassword);

        Assert.Equal(ReadLogin, await WhoAsync(name, CredentialRole.Read));
        Assert.Equal(RunLogin, await WhoAsync(name, CredentialRole.Run));
        Assert.Equal(ReadLogin, await WhoAsync(name, CredentialRole.Read));

        var (asRead, _) = await factory.Services.GetRequiredService<DriverConnectionFactory>().OpenAsync(name, CredentialRole.Read, CancellationToken.None);
        await using (asRead) await Assert.ThrowsAsync<SqlException>(() => ExecuteScalarAsync(asRead, "CREATE TABLE dbo.NotAllowed (Id INT)"));
        var (asRun, _) = await factory.Services.GetRequiredService<DriverConnectionFactory>().OpenAsync(name, CredentialRole.Run, CancellationToken.None);
        await using (asRun) await ExecuteScalarAsync(asRun, "CREATE TABLE dbo.Allowed (Id INT)");
    }

    [Fact]
    public async Task A_connection_with_one_login_connects_as_that_login_in_both_roles()
    {
        var name = await SaveAsync(LoginPassword, runPassword: null);

        Assert.Equal(ReadLogin, await WhoAsync(name, CredentialRole.Read));
        Assert.Equal(ReadLogin, await WhoAsync(name, CredentialRole.Run));
    }

    [Fact]
    public async Task Browsing_and_the_test_button_work_with_the_read_login_alone_even_when_the_run_login_is_broken()
    {
        var name = await SaveAsync(LoginPassword, runPassword: "not-the-password");

        var tables = await _client.GetFromJsonAsync<List<JsonElement>>($"/api/connections/{name}/metadata/databases/{Database}/tables", JsonOptions);
        Assert.Contains(tables!, t => t.GetProperty("table").GetString() == "Probe");

        var read = await (await _client.PostAsync($"/api/connections/{name}/test", null)).Content.ReadFromJsonAsync<Report>(JsonOptions);
        Assert.True(read!.Succeeded, read.Error);
        var run = await (await _client.PostAsync($"/api/connections/{name}/test?role=run", null)).Content.ReadFromJsonAsync<Report>(JsonOptions);
        Assert.False(run!.Succeeded);
        Assert.DoesNotContain("not-the-password", run.Error ?? "");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await _client.PostAsync($"/api/connections/{name}/test?role=admin", null)).StatusCode);
    }

    [Fact]
    public async Task A_pass_opens_with_the_run_login_even_when_the_read_login_is_broken()
    {
        var name = await SaveAsync("not-the-password", LoginPassword);

        Assert.Equal(RunLogin, await WhoAsync(name, CredentialRole.Run));
        await Assert.ThrowsAsync<SqlException>(() => WhoAsync(name, CredentialRole.Read));
        var tables = await _client.GetAsync($"/api/connections/{name}/metadata/databases/{Database}/tables");
        Assert.False(tables.IsSuccessStatusCode);
    }

    [Fact]
    public async Task The_credential_source_lists_both_references_and_no_value()
    {
        var name = await SaveAsync(LoginPassword, LoginPassword);

        var text = await _client.GetStringAsync($"/api/connections/{name}/credential-source");

        Assert.Contains($"dbdatakit:sync:connection:{name}:run", text);
        Assert.DoesNotContain(LoginPassword, text);
        var config = await _client.GetStringAsync($"/api/connections/{name}");
        Assert.DoesNotContain(LoginPassword, config);
    }

    private static async Task ExecuteScalarAsync(System.Data.Common.DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
