using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// Plan 28c E20d (group-wide, owner decision 2026-11-15) with the real programs: two runner processes on one state database (SQL Server or PostgreSQL) and a console in this process. Pairing the first runner joins it (the
/// console makes the group's key and hands it over); the second instance is joined by its address and a code made on it, and ends up with the same key; the key is in no answer, audit entry or log of the console, and a login sent
/// through the console's relay to the first instance is kept in the group's state, encrypted.
/// </summary>
public sealed class ConsoleJoinsRunnerGroupTests
{
    private const string KeySecret = "dbdatakit:runner:state:group-key";

    public static TheoryData<string> Engines => ["mssql", "postgres"];

    /// <summary>A new, empty database on the engine (<c>mssql</c> or <c>postgres</c>, from <c>DBDATAKIT_TEST_SQLSERVER</c> / <c>DBDATAKIT_TEST_POSTGRES</c>), its connection string and how to drop it; skipped when the engine is not set.</summary>
    internal static async Task<(string Connection, Func<Task> Drop)> ProvisionAsync(string engine)
    {
        var name = $"ddk_join_{Guid.NewGuid():N}"[..24];
        if (engine == "mssql")
        {
            var server = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SQLSERVER");
            if (string.IsNullOrEmpty(server)) Assert.Skip("set DBDATAKIT_TEST_SQLSERVER to run on SQL Server");
            await using (var c = new SqlConnection(server)) { await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = $"CREATE DATABASE [{name}]"; await cmd.ExecuteNonQueryAsync(); }
            return (new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString, async () =>
            {
                SqlConnection.ClearAllPools();
                await using var c = new SqlConnection(server); await c.OpenAsync(); await using var cmd = c.CreateCommand();
                cmd.CommandText = $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];"; await cmd.ExecuteNonQueryAsync();
            });
        }

        var pg = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_POSTGRES");
        if (string.IsNullOrEmpty(pg)) Assert.Skip("set DBDATAKIT_TEST_POSTGRES to run on PostgreSQL");
        await using (var c = new NpgsqlConnection(pg)) { await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = $"CREATE DATABASE {name}"; await cmd.ExecuteNonQueryAsync(); }
        return (new NpgsqlConnectionStringBuilder(pg) { Database = name }.ConnectionString, async () =>
        {
            NpgsqlConnection.ClearAllPools();
            await using var c = new NpgsqlConnection(pg); await c.OpenAsync(); await using var cmd = c.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS {name} WITH (FORCE)"; await cmd.ExecuteNonQueryAsync();
        });
    }

    /// <summary>The group's key as an instance keeps it in its own store (the runner's <c>secrets/</c>, with no DPAPI on Linux).</summary>
    private static async Task<string?> KeyOnInstanceAsync(string directory)
    {
        var store = new EncryptedFileSecretStore(SecretKeys.StoreDirectory(directory), SecretKeys.Open(directory, new SecretKeyOptions(), null, null));
        using var held = await store.TryResolveAsync(SecretRef.Parse("secret:" + KeySecret));
        return held?.RevealString();
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Pairing_joins_the_first_instance_and_a_second_is_joined_by_its_address_and_a_code_with_the_same_key(string engine)
    {
        var root = Path.Combine(Path.GetTempPath(), "ddk-e2e-join-" + Guid.NewGuid().ToString("N")[..8]);
        var (connection, drop) = await ProvisionAsync(engine);
        var started = new List<RealProcess>();
        WebApplication? console = null;
        try
        {
            var environment = new Dictionary<string, string> { ["DBDATAKIT_STATE_RUNNER_ENGINE"] = engine, ["DBDATAKIT_STATE_RUNNER_CONNECTION"] = connection };
            var (dirA, dirB) = (Path.Combine(root, "a"), Path.Combine(root, "b"));
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);
            var runner = Programs.Runner;
            var codeA = Programs.CodeIn(await RealProcess.RunWithEnvironmentAsync(runner, null, environment, "server", "pair", "--repo", dirA, "--max-role", "admin"));
            var (urlA, urlB) = (new Uri($"https://127.0.0.1:{Programs.FreePort()}"), new Uri($"https://127.0.0.1:{Programs.FreePort()}"));
            var a = RealProcess.Start(runner, ["server", "run", "--repo", dirA, "--listen", urlA.AbsoluteUri.TrimEnd('/')], environment: environment);
            started.Add(a);
            await a.WaitUntilAsync(new Uri(urlA, "/health"), TimeSpan.FromSeconds(90));
            var b = RealProcess.Start(runner, ["server", "run", "--repo", dirB, "--listen", urlB.AbsoluteUri.TrimEnd('/')], environment: environment);
            started.Add(b);
            await b.WaitUntilAsync(new Uri(urlB, "/health"), TimeSpan.FromSeconds(90));

            var consoleDirectory = Path.Combine(root, "console");
            var lines = new List<string>();
            console = ConsoleHost.Build(new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0")), ConsoleHost.Certificate(consoleDirectory), line => { lock (lines) lines.Add(line); }, webRoot: Path.Combine(consoleDirectory, "none"));
            await console.StartAsync();
            using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(console.Urls.First()) };
            var personId = console.Services.GetRequiredService<IUserAccounts>().CreateUser("Ada Admin", null, UserRole.Admin).Id;
            async Task<(HttpStatusCode Status, JsonNode? Body)> As(HttpMethod method, string path, object? body = null)
            {
                using var request = new HttpRequestMessage(method, path);
                request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={console.Services.GetRequiredService<ISessionStore>().Create(personId).Id}");
                if (body is not null) request.Content = JsonContent.Create(body);
                using var response = await http.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
            }

            // pairing the first instance joins it: the console makes the group's key and hands it over
            var (paired, summary) = await As(HttpMethod.Post, "/api/instances", new { url = urlA.AbsoluteUri.TrimEnd('/'), code = codeA, name = "Runners" });
            Assert.True(paired == HttpStatusCode.OK, $"{paired} {summary}");
            Assert.Null(summary!["groupJoin"]?.GetValue<string>());
            var id = (string)summary["id"]!;
            var consoleStore = console.Services.GetRequiredService<EncryptedFileSecretStore>();
            string key;
            using (var held = await consoleStore.TryResolveAsync(ConsoleGroupKeys.NameFor(id).ToRef())) key = held!.RevealString();
            Assert.Equal(key, await KeyOnInstanceAsync(dirA));
            Assert.Null(await KeyOnInstanceAsync(dirB));

            // the second instance: no code, refused; a code made on it (the group shares its codes), joined with the same key, and the registered address stays the first
            var (noCode, noCodeBody) = await As(HttpMethod.Post, $"/api/instances/{id}/join", new { url = urlB.AbsoluteUri.TrimEnd('/') });
            Assert.Equal(HttpStatusCode.BadRequest, noCode);
            Assert.Contains("pairing code", (string)noCodeBody!["error"]!);
            var codeB = Programs.CodeIn(await RealProcess.RunWithEnvironmentAsync(runner, null, environment, "server", "pair", "--repo", dirB, "--max-role", "admin"));
            var (joined, joinedBody) = await As(HttpMethod.Post, $"/api/instances/{id}/join", new { url = urlB.AbsoluteUri.TrimEnd('/'), code = codeB });
            Assert.True(joined == HttpStatusCode.OK, $"{joined} {joinedBody}");
            Assert.False((bool)joinedBody!["already"]!);
            Assert.Equal(key, await KeyOnInstanceAsync(dirB));
            var (_, listed) = await As(HttpMethod.Get, "/api/instances");
            Assert.Equal(urlA.AbsoluteUri.TrimEnd('/'), ((string)listed!.AsArray().Single(i => (string)i!["id"]! == id)!["url"]!).TrimEnd('/'));
            // the registered instance again: it already holds the key
            var (again, againBody) = await As(HttpMethod.Post, $"/api/instances/{id}/join", new { });
            Assert.True(again == HttpStatusCode.OK, $"{again} {againBody}");
            Assert.True((bool)againBody!["already"]!);

            // a login sent through the relay to the first instance is the group's: listed by the second
            var name = "dbdatakit:sync:connection:erp:run";
            var (sent, sentBody) = await As(HttpMethod.Put, $"/relay/{id}/api/v1/secrets/{Uri.EscapeDataString(name)}", new { value = "s3nt-to-a-read-by-b" });
            Assert.True(sent == HttpStatusCode.OK, $"{sent} {sentBody}");
            Assert.Null(await new EncryptedFileSecretStore(SecretKeys.StoreDirectory(dirA), SecretKeys.Open(dirA, new SecretKeyOptions(), null, null)).TryResolveAsync(SecretRef.Parse("secret:" + name)));

            // the audit names who joined what, never the key; nor does any log line
            var audit = console.Services.GetRequiredService<DbDataKit.State.AuditLog>().Read(new DbDataKit.State.AuditQuery(VerbPrefix: ConsoleGroupKeys.JoinVerb));
            Assert.Equal(3, audit.Count(e => e.Entry.Outcome == DbDataKit.State.AuditEvent.Ok));
            Assert.Contains(audit, e => e.Entry.Outcome == DbDataKit.State.AuditEvent.Refused);
            Assert.All(audit, e => Assert.Equal(personId, e.Entry.ActorId));
            Assert.Contains(audit, e => (e.Entry.Detail ?? "").Contains(urlB.Port.ToString()));
            var said = string.Join('\n', audit.Select(e => e.Entry.Detail)) + string.Join('\n', lines) + a.Output + b.Output;
            Assert.DoesNotContain(key, said);
            Assert.DoesNotContain("s3nt-to-a-read-by-b", said);
        }
        finally
        {
            if (console is not null) { await console.StopAsync(); await console.DisposeAsync(); }
            foreach (var process in started) await process.DisposeAsync();
            await drop();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
