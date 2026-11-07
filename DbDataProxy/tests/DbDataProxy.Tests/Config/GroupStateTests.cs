using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Identity;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Server.Secrets;
using DbDataProxy.Server.Serving;
using DbDataProxy.Server.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;

namespace DbDataProxy.Tests.Config;

/// <summary>
/// Plan 20 O02: instances of a group share their state on SQL Server or PostgreSQL. Two instances running at once against one state serve each other's sessions (a pass-through session's database credentials with them,
/// encrypted with the group's key) and write one audit; an instance given another key refuses to start, naming the secret. A second instance against a SQLite state is refused.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class GroupStateTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbproxy-group-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<WebApplication> _apps = [];

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
        {
            try { await app.StopAsync(); } catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
            await app.DisposeAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        SqlConnection.ClearAllPools();
        TestFolder.Delete(_root);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>An instance in a data directory of its own (or a shared one), on the state the arguments name.</summary>
    private WebApplication Instance(string dataDirectory, params string[] state)
    {
        Directory.CreateDirectory(ProxyConfigFile.DirectoryIn(dataDirectory));
        var serverYaml = Path.Combine(ProxyConfigFile.DirectoryIn(dataDirectory), "server.yaml");
        if (!File.Exists(serverYaml)) File.WriteAllText(serverYaml, "schemaVersion: 2\ntls: { mode: selfSigned }\n");
        var app = ProxyHost.Build([$"--Proxy:DataDirectory={dataDirectory}", "--Proxy:Listen:Address=127.0.0.1", $"--Proxy:Listen:Port={FreePort()}", .. state]);
        _apps.Add(app);
        return app;
    }

    [Fact]
    public async Task A_second_instance_against_a_sqlite_state_is_refused_and_starts_once_the_first_has_stopped()
    {
        var shared = Path.Combine(_root, "one");
        var first = Instance(shared);
        await first.StartAsync();

        var second = Instance(shared);
        var refused = await Assert.ThrowsAsync<SecondInstanceException>(() => second.StartAsync());
        Assert.Contains("SQLite", refused.Message);
        Assert.Contains("SQL Server or PostgreSQL", refused.Message);

        await first.StopAsync();
        var third = Instance(shared);
        await third.StartAsync();   // the first one's row went when it stopped
    }

    [Theory]
    [InlineData("mssql", "DBKIT_TEST_MSSQL")]
    [InlineData("postgres", "DBKIT_TEST_POSTGRES")]
    public async Task Two_instances_on_a_server_state_share_sessions_and_audit_and_one_with_another_key_refuses_to_start(string engine, string variable)
    {
        var server = Environment.GetEnvironmentVariable(variable) ?? Environment.GetEnvironmentVariable(engine == "mssql" ? "DBDATA_TEST_SQLSERVER" : "DBDATA_TEST_POSTGRES");
        if (server is null) Assert.Skip($"set {variable} to run on {engine}");

        var name = $"dbproxy_group_{Guid.NewGuid():N}";
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
            string[] state = [$"--Proxy:State:Engine={engine}", $"--Proxy:State:Connection={connection}"];

            // the first instance makes the group's key; the second is given a copy of it, as an operator would
            var (dirA, dirB) = (Path.Combine(_root, "a"), Path.Combine(_root, "b"));
            var a = Instance(dirA, state);
            await a.StartAsync();
            var storeA = SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = dirA });
            using (var key = await storeA.TryResolveAsync(SecretRef.Parse("secret:" + GroupSessionKey.Secret)))
                await SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = dirB }).SetAsync(SecretName.Parse(GroupSessionKey.Secret), key!);
            var b = Instance(dirB, state);
            await b.StartAsync();   // a second instance on a server engine is the point of one

            // a pass-through session made on one instance is served by the other, credentials and all
            var sessionsA = a.Services.GetRequiredService<DbDataProxy.Server.Auth.SessionStore>();
            var sessionsB = b.Services.GetRequiredService<DbDataProxy.Server.Auth.SessionStore>();
            var token = sessionsA.Issue(new ProxyPrincipal("ada", "passthrough", ["erp"], "erp", new DbCredentials("ada_db", "s3cret-pass")));
            var onB = sessionsB.TryGet(token);
            Assert.NotNull(onB);
            Assert.Equal(("ada", "passthrough", "erp"), (onB.Name, onB.Method, onB.DefaultDataSource));
            Assert.Equal(new DbCredentials("ada_db", "s3cret-pass"), onB.Credentials);
            Assert.Contains(sessionsB.List(), s => s.User == "ada");

            // the database never holds the password in the clear, nor the token
            var database = a.Services.GetRequiredService<StateDatabase>();
            using (var conn = database.OpenConnection())
            using (var cmd = database.Command("serving", conn, "SELECT token_hash, principal FROM {table:client_session};"))
            using (var reader = cmd.ExecuteReader())
            {
                Assert.True(reader.Read());
                Assert.DoesNotContain("s3cret-pass", reader.GetString(1), StringComparison.Ordinal);
                Assert.NotEqual(token, reader.GetString(0));
            }

            // ending it on one ends it on both
            sessionsB.Revoke(token);
            Assert.Null(sessionsA.TryGet(token));

            // one audit: what one instance records, the other reads
            var at = DateTimeOffset.UtcNow;
            new StateAuditLog(database).Append(new AuditEntry(at, "console-1", "Console", "grace", "Grace Hopper", true, "data-source.write", "erp", "ok", "t1"));
            Assert.Contains(new StateAuditLog(b.Services.GetRequiredService<StateDatabase>()).Read(), e => e.Command == "data-source.write" && e.UserId == "grace");

            // a third instance with a key of its own (none copied) is refused, naming the secret, and keeps no key it made
            // (as it is built, since a group's certificate needs the key, or as it starts)
            var refused = await Assert.ThrowsAnyAsync<SecretConfigurationException>(async () => await Instance(Path.Combine(_root, "c"), state).StartAsync());
            Assert.Contains(GroupSessionKey.Secret, refused.Message);
            using (var left = await SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = Path.Combine(_root, "c") }).TryResolveAsync(SecretRef.Parse("secret:" + GroupSessionKey.Secret)))
                Assert.Null(left);
        }
        finally
        {
            foreach (var app in _apps)
            {
                try { await app.StopAsync(); } catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
            }
            SqlConnection.ClearAllPools();
            Npgsql.NpgsqlConnection.ClearAllPools();
            Admin(engine == "mssql" ? $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];" : $"DROP DATABASE IF EXISTS {name} WITH (FORCE);");
        }
    }

    /// <summary>A scratch database on <paramref name="engine"/>'s server, the state arguments that name it, and its removal; null (the test skipped) when the engine is not set up.</summary>
    private static (string[] State, Action Drop) ScratchState(string engine, string variable)
    {
        var server = Environment.GetEnvironmentVariable(variable) ?? Environment.GetEnvironmentVariable(engine == "mssql" ? "DBDATA_TEST_SQLSERVER" : "DBDATA_TEST_POSTGRES");
        if (server is null) Assert.Skip($"set {variable} to run on {engine}");
        var name = $"dbproxy_group_{Guid.NewGuid():N}";
        void Admin(string sql)
        {
            using System.Data.Common.DbConnection admin = engine == "mssql" ? new SqlConnection(server) : new Npgsql.NpgsqlConnection(server);
            admin.Open();
            using var cmd = admin.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        Admin(engine == "mssql" ? $"CREATE DATABASE [{name}];" : $"CREATE DATABASE {name};");
        var connection = engine == "mssql" ? new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString : new Npgsql.NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
        return ([$"--Proxy:State:Engine={engine}", $"--Proxy:State:Connection={connection}"], () =>
        {
            SqlConnection.ClearAllPools();
            Npgsql.NpgsqlConnection.ClearAllPools();
            Admin(engine == "mssql" ? $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];" : $"DROP DATABASE IF EXISTS {name} WITH (FORCE);");
        });
    }

    private const string HomeUrl = "https://home.example.invalid";

    /// <summary>An instance that serves the identity home and the management API too, on ports of its own, at the group's one public address.</summary>
    private async Task<(WebApplication App, int Management, int Identity)> ServingInstance(string dataDirectory, string[] state, params string[] more)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (management, identity) = (FreePort(), FreePort());
            var app = Instance(dataDirectory, [.. state,
                "--Proxy:Management:Enabled=true", "--Proxy:Management:Address=127.0.0.1", $"--Proxy:Management:Port={management}",
                "--Proxy:Identity:Enabled=true", "--Proxy:Identity:Address=127.0.0.1", $"--Proxy:Identity:Port={identity}", $"--Proxy:Identity:PublicUrl={HomeUrl}", .. more]);
            try
            {
                await app.StartAsync();
                return (app, management, identity);
            }
            catch (IOException ex) when (attempt < 5 && ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            {
                _apps.Remove(app);
                await app.DisposeAsync();
            }
        }
    }

    private static HttpClient Browser(int port) =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = new Uri($"https://127.0.0.1:{port}") };

    private static string? Cookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith(name + "=", StringComparison.Ordinal) && c.Split('=', 2)[1].Length > 0) : null;

    private static async Task<HttpResponseMessage> Send(HttpClient browser, HttpMethod method, string path, string? cookie = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return await browser.SendAsync(request);
    }

    [Theory]
    [InlineData("mssql", "DBKIT_TEST_MSSQL")]
    [InlineData("postgres", "DBKIT_TEST_POSTGRES")]
    public async Task Two_instances_on_a_server_state_serve_one_identity_home_whichever_one_a_browser_or_console_reaches(string engine, string variable)
    {
        // plan 20 O02b: nothing a sign-in or a management call holds between two requests is one instance's
        var (state, drop) = ScratchState(engine, variable);
        try
        {
            // a stand-in Entra tenant, as IdentityModuleTests has it
            using var tenantKey = RSA.Create(2048);
            var tenantPort = FreePort();
            var authority = $"http://127.0.0.1:{tenantPort}/t/v2.0";
            var nonce = "";
            var tenantBuilder = WebApplication.CreateSlimBuilder();
            tenantBuilder.WebHost.UseUrls($"http://127.0.0.1:{tenantPort}");
            await using var tenant = tenantBuilder.Build();
            tenant.MapGet("/t/v2.0/.well-known/openid-configuration", () => Results.Json(new { issuer = authority, authorization_endpoint = authority + "/authorize", token_endpoint = authority + "/token", jwks_uri = authority + "/keys" }));
            tenant.MapGet("/t/v2.0/keys", () =>
            {
                var p = tenantKey.ExportParameters(false);
                return Results.Json(new { keys = new[] { new { kty = "RSA", kid = "k1", n = Base64Url.EncodeToString(p.Modulus!), e = Base64Url.EncodeToString(p.Exponent!) } } });
            });
            tenant.MapPost("/t/v2.0/token", () =>
            {
                static string B(object o) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(o));
                var input = B(new { alg = "RS256", kid = "k1" }) + "." + B(new Dictionary<string, object>
                {
                    ["iss"] = authority, ["aud"] = "home-app", ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(), ["sub"] = "oid-ada", ["name"] = "Ada Lovelace", ["nonce"] = nonce,
                });
                return Results.Json(new { id_token = input + "." + Base64Url.EncodeToString(tenantKey.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) });
            });
            await tenant.StartAsync();
            string[] entra = [$"--Proxy:Identity:Entra:Authority={authority}", "--Proxy:Identity:Entra:ClientId=home-app", "--Proxy:Identity:Entra:ClientSecret=tenant-secret"];

            // the first instance makes the group's keys and is paired with a console; the second is given copies of the keys and the pairing, as an operator would
            var (dirA, dirB) = (Path.Combine(_root, "a"), Path.Combine(_root, "b"));
            var (a, managementA, identityA) = await ServingInstance(dirA, state, entra);
            var console = new ConsoleIdentity("console-1", ConsoleSigningKey.Generate());
            // the group's pairing codes are in its state (plan 20 O04)
            var pairing = new PairingFiles(ProxyConfigFile.PathIn(dirA), Path.Combine(dirA, "pairing-codes.json"), a.Services.GetRequiredService<StateDatabase>());
            var paired = await PairingClient.PairAsync(new Uri($"https://127.0.0.1:{managementA}"), pairing.Codes().Create(ServerRole.Viewer | ServerRole.Operator | ServerRole.Admin).Code, console);
            var storeA = SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = dirA });
            var storeB = SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = dirB });
            foreach (var secret in new[] { GroupSessionKey.Secret, ProxyIdentityModule.SigningKeySecret })
                using (var key = await storeA.TryResolveAsync(SecretRef.Parse("secret:" + secret)))
                    await storeB.SetAsync(SecretName.Parse(secret), key!);
            // the same instance id and paired console are the group's, in its state (plan 20 O04): nothing to copy
            var (_, managementB, identityB) = await ServingInstance(dirB, state, entra);
            using var atA = Browser(identityA);
            using var atB = Browser(identityB);

            // an Entra sign-in started at A, which the tenant sends back to B
            var start = await atA.GetAsync(UpstreamSignIn.EntraPath);
            Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
            var query = System.Web.HttpUtility.ParseQueryString(start.Headers.Location!.Query);
            nonce = query["nonce"]!;
            var back = await atB.GetAsync($"{UpstreamSignIn.EntraCallbackPath}?state={Uri.EscapeDataString(query["state"]!)}&code=from-tenant");
            Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
            var session = Cookie(back, ProxyIdentityModule.Auth.SessionCookie);
            Assert.NotNull(session);
            var database = a.Services.GetRequiredService<StateDatabase>();
            var ada = Assert.Single(new IdentityUserStore(database).List());

            // a console's authorize request at A with that session gives a code, which B redeems; the id token is signed with the group's key, which A publishes
            var client = new OidcClients(database).Register("Operations console", ["https://console.example.invalid/callback"], confidential: false).Client.ClientId;
            var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(48));
            var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var authorized = await Send(atA, HttpMethod.Get, $"/identity/authorize?response_type=code&client_id={client}&redirect_uri={Uri.EscapeDataString("https://console.example.invalid/callback")}&scope=openid%20profile&state=s1&nonce=n1&code_challenge={challenge}&code_challenge_method=S256", session);
            Assert.Equal(HttpStatusCode.Redirect, authorized.StatusCode);
            var code = System.Web.HttpUtility.ParseQueryString(authorized.Headers.Location!.Query)["code"]!;
            var token = await atB.PostAsync("/identity/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = "https://console.example.invalid/callback", ["client_id"] = client, ["code_verifier"] = verifier,
            }));
            Assert.Equal(HttpStatusCode.OK, token.StatusCode);
            var idToken = JsonDocument.Parse(await token.Content.ReadAsStringAsync()).RootElement.GetProperty("id_token").GetString()!.Split('.');
            var kid = JsonDocument.Parse(Base64Url.DecodeFromChars(idToken[0])).RootElement.GetProperty("kid").GetString();
            Assert.Contains(kid!, await atA.GetStringAsync("/identity/jwks"));
            Assert.Equal(ada.Id, JsonDocument.Parse(Base64Url.DecodeFromChars(idToken[1])).RootElement.GetProperty("sub").GetString());

            // a passkey sign-in started at B, finished at A, and the captured response refused at either
            using var passkey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var credentialId = RandomNumberGenerator.GetBytes(16);
            var q = passkey.ExportParameters(false);
            new IdentityUserStore(database).AddCredential(ada.Id, CredentialMethods.Passkey, Base64Url.EncodeToString(credentialId), Convert.ToBase64String([0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20, .. q.Q.X!, 0x22, 0x58, 0x20, .. q.Q.Y!]), label: "Passkey");
            var begun = await atB.PostAsync("/api/auth/passkey/begin", null);
            Assert.Equal(HttpStatusCode.OK, begun.StatusCode);
            var passkeyCookie = Cookie(begun, ProxyIdentityModule.Auth.PasskeyAssertionCookie)!;
            var clientData = JsonSerializer.SerializeToUtf8Bytes(new { type = "webauthn.get", challenge = JsonDocument.Parse(await begun.Content.ReadAsStringAsync()).RootElement.GetProperty("challenge").GetString(), origin = HomeUrl, crossOrigin = false });
            byte[] authenticatorData = [.. SHA256.HashData(Encoding.UTF8.GetBytes(new Uri(HomeUrl).Host)), 0x05, 0, 0, 0, 0];
            var signature = passkey.SignData([.. authenticatorData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var assertion = JsonSerializer.Serialize(new
            {
                id = Base64Url.EncodeToString(credentialId), rawId = Base64Url.EncodeToString(credentialId), type = "public-key",
                response = new { authenticatorData = Base64Url.EncodeToString(authenticatorData), clientDataJSON = Base64Url.EncodeToString(clientData), signature = Base64Url.EncodeToString(signature) },
                clientExtensionResults = new { },
            });
            var completed = await Send(atA, HttpMethod.Post, "/api/auth/passkey/complete", passkeyCookie, new StringContent(assertion, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
            Assert.NotNull(Cookie(completed, ProxyIdentityModule.Auth.SessionCookie));
            foreach (var browser in new[] { atA, atB })
                Assert.Null(Cookie(await Send(browser, HttpMethod.Post, "/api/auth/passkey/complete", passkeyCookie, new StringContent(assertion, Encoding.UTF8, "application/json")), ProxyIdentityModule.Auth.SessionCookie));

            // a management token the console sent to A is refused by B (the identity admin API is one of its calls)
            var managementToken = console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", ServerRole.Admin));
            async Task<HttpStatusCode> Call(int port)
            {
                using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}/api/v1/identity/people");
                request.Headers.Add(ManagementAuthentication.TokenHeader, managementToken);
                return (await http.SendAsync(request)).StatusCode;
            }
            Assert.Equal(HttpStatusCode.OK, await Call(managementA));
            Assert.Equal(HttpStatusCode.Unauthorized, await Call(managementB));
        }
        finally
        {
            foreach (var app in _apps)
            {
                try { await app.StopAsync(); } catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
            }
            drop();
        }
    }

    [Theory]
    [InlineData("mssql", "DBKIT_TEST_MSSQL")]
    [InlineData("postgres", "DBKIT_TEST_POSTGRES")]
    public async Task A_model_published_through_one_instance_is_served_by_every_one_and_by_one_started_after_the_publisher_stopped(string engine, string variable)
    {
        // plan 20 O03: the published definitions are the group's; each instance follows them and keeps the last version it could load
        var (state, drop) = ScratchState(engine, variable);
        try
        {
            string[] poll = [.. state, "--Proxy:DefinitionPollSeconds=1"];
            var catalog = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "catalog.yaml"));
            // the data source the model reads, set up on every instance as an operator would (each instance's configuration is its own)
            var data = Path.Combine(_root, "sales.duckdb");
            using (var duck = new DuckDB.NET.Data.DuckDBConnection("Data Source=" + data))
            {
                duck.Open();
                foreach (var table in DbDataKit.Semantic.CatalogLoader.LoadText(catalog).Doc.Sources.Where(s => DbDataKit.Serving.Semantic.SemanticModel.BaseSource(s.Key) == "sqlserver_sales")
                    .SelectMany(s => s.Value.Joins.Values.Select(j => j.Table).Append(s.Value.Fact.Table)).Distinct())
                {
                    using var create = duck.CreateCommand();
                    create.CommandText = $"CREATE TABLE IF NOT EXISTS {table} (x INTEGER)";
                    create.ExecuteNonQuery();
                }
            }
            var dirs = new[] { "a", "b", "c" }.Select(d => Path.Combine(_root, d)).ToArray();
            foreach (var dir in dirs)
            {
                Directory.CreateDirectory(Path.Combine(ProxyConfigFile.DirectoryIn(dir), "connections"));
                File.WriteAllText(Path.Combine(ProxyConfigFile.DirectoryIn(dir), "connections", "sales-ro.yaml"), Conn.Yaml("sales-ro", "duckdb", "Data Source=" + data));
            }

            var (a, managementA, _) = await ServingInstance(dirs[0], poll);
            var storeA = SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = dirs[0] });
            foreach (var other in dirs[1..])
                foreach (var secret in new[] { GroupSessionKey.Secret, ProxyIdentityModule.SigningKeySecret })
                    using (var key = await storeA.TryResolveAsync(SecretRef.Parse("secret:" + secret)))
                        await SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = other }).SetAsync(SecretName.Parse(secret), key!);
            var (b, _, _) = await ServingInstance(dirs[1], poll);
            var console = new ConsoleIdentity("console-1", ConsoleSigningKey.Generate());
            var paired = await PairingClient.PairAsync(new Uri($"https://127.0.0.1:{managementA}"),
                new PairingFiles(ProxyConfigFile.PathIn(dirs[0]), Path.Combine(dirs[0], "pairing-codes.json"), a.Services.GetRequiredService<StateDatabase>()).Codes().Create(ServerRole.Viewer | ServerRole.Operator | ServerRole.Admin).Code, console);
            async Task<HttpResponseMessage> Call(HttpMethod method, string path, string? body = null)
            {
                using var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint));
                using var request = new HttpRequestMessage(method, $"https://127.0.0.1:{managementA}{path}");
                request.Headers.Add(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", ServerRole.Admin)));
                if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                return await http.SendAsync(request);
            }
            static string Publish(string catalog, int? expected) =>
                JsonSerializer.Serialize(new { catalog, sources = new Dictionary<string, string> { ["sqlserver_sales"] = "sales-ro" }, expected_version = expected });
            static async Task Eventually(Func<bool> served, string what)
            {
                for (var i = 0; i < 100 && !served(); i++) await Task.Delay(100);
                Assert.True(served(), what);
            }
            var modelsA = a.Services.GetRequiredService<DbDataKit.Serving.Semantic.SemanticModels>();
            var modelsB = b.Services.GetRequiredService<DbDataKit.Serving.Semantic.SemanticModels>();

            // published through A: A serves it at once, B when it next looks
            var first = await Call(HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", Publish(catalog, null));
            Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
            Assert.NotNull(modelsA.Find("sales_catalog"));
            await Eventually(() => modelsB.Find("sales_catalog") is not null, "the second instance serves the model published through the first");
            var v1 = modelsB.Find("sales_catalog")!;

            // a version neither can load (written by a publisher that skipped the checks): the version each served stays, and the listing says why
            var definitions = new DefinitionStore(a.Services.GetRequiredService<StateDatabase>());
            definitions.Publish(PublishedSemanticModels.DefinitionKind, "sales_catalog", JsonSerializer.Serialize(new { catalog = "name: sales_catalog\nnot: a catalog", sources = new { }, ssas = new { } }), "someone");
            var published = b.Services.GetRequiredService<PublishedSemanticModels>();
            await Eventually(() => published.Failed("sales_catalog") is { Version: 2 }, "the second instance tried version 2");
            Assert.Same(v1, modelsB.Find("sales_catalog"));
            Assert.Equal(1, published.Served("sales_catalog"));
            await Eventually(() => a.Services.GetRequiredService<PublishedSemanticModels>().Failed("sales_catalog") is not null, "the first instance tried version 2");
            var listed = JsonNode.Parse(await (await Call(HttpMethod.Get, "/api/v1/semantic-models")).Content.ReadAsStringAsync())!["models"]!.AsArray().Single()!;
            Assert.Equal((2, 1, 2), ((int)listed["current_version"]!, (int)listed["served_version"]!, (int)listed["failed_version"]!));

            // a good version after it reaches both
            var third = await Call(HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", Publish(catalog.Replace("Governed sales", "Sales"), 2));
            Assert.True(third.IsSuccessStatusCode, await third.Content.ReadAsStringAsync());
            await Eventually(() => published.Served("sales_catalog") == 3 && published.Failed("sales_catalog") is null, "the second instance serves version 3");

            // the publisher (and the instance it published through) stopped: an instance started now serves what the group serves
            await a.StopAsync();
            var (c, _, _) = await ServingInstance(dirs[2], poll);
            Assert.Equal(3, c.Services.GetRequiredService<PublishedSemanticModels>().Served("sales_catalog"));
            Assert.NotNull(c.Services.GetRequiredService<DbDataKit.Serving.Semantic.SemanticModels>().Find("semantic:sales_catalog"));

            // and a removal reaches the instances still running
            var groupStore = new PublishedSemanticModels(new DefinitionStore(c.Services.GetRequiredService<StateDatabase>()), new DbDataKit.Serving.Semantic.SemanticModels());
            groupStore.Delete("sales_catalog", "ada");
            await Eventually(() => modelsB.Find("sales_catalog") is null && c.Services.GetRequiredService<DbDataKit.Serving.Semantic.SemanticModels>().Find("sales_catalog") is null, "a removed model is served by none");
        }
        finally
        {
            foreach (var app in _apps)
            {
                try { await app.StopAsync(); } catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
            }
            drop();
        }
    }

    [Theory]
    [InlineData("mssql", "DBKIT_TEST_MSSQL")]
    [InlineData("postgres", "DBKIT_TEST_POSTGRES")]
    public async Task A_console_pairs_once_through_any_instance_of_a_group_and_every_instance_is_that_server_with_one_certificate(string engine, string variable)
    {
        // plan 20 O04: the group's instance id, paired clients, pairing codes and self-signed certificate are in its state
        var (state, drop) = ScratchState(engine, variable);
        try
        {
            // the state in server.yaml, as an operator sets a group up, so the command line finds it too
            var connection = state[1]["--Proxy:State:Connection=".Length..];
            var dirs = new[] { "a", "b" }.Select(d => Path.Combine(_root, d)).ToArray();
            foreach (var dir in dirs)
            {
                Directory.CreateDirectory(ProxyConfigFile.DirectoryIn(dir));
                File.WriteAllText(ProxyConfigFile.PathIn(dir), $"schemaVersion: 2\ntls: {{ mode: selfSigned }}\nstate: {{ engine: {engine}, connectionString: '{connection}' }}\n");
            }
            var (a, managementA, _) = await ServingInstance(dirs[0], []);
            var storeA = SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = dirs[0] });
            foreach (var secret in new[] { GroupSessionKey.Secret, ProxyIdentityModule.SigningKeySecret })
                using (var key = await storeA.TryResolveAsync(SecretRef.Parse("secret:" + secret)))
                    await SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = dirs[1] }).SetAsync(SecretName.Parse(secret), key!);
            var (b, managementB, _) = await ServingInstance(dirs[1], []);

            // one certificate for the group, though each instance would have made its own
            var thumbprint = a.Services.GetRequiredService<DbDataProxy.Server.Tls.TlsCertificateHolder>().Current!.Thumbprint;
            Assert.Equal(thumbprint, b.Services.GetRequiredService<DbDataProxy.Server.Tls.TlsCertificateHolder>().Current!.Thumbprint);

            // the operator makes a code on the command line of the first instance's machine; the console redeems it through the second
            var output = new StringWriter();
            Assert.Equal(0, await DbDataProxy.Server.Hosting.Cli.RunAsync(["server", "pair", "--max-role", "viewer,operator,admin", "--repo", dirs[0]], new DbDataKit.Cli.CliContext(output, new StringWriter(), TextReader.Null)));
            var code = System.Text.RegularExpressions.Regex.Match(output.ToString(), @"Pairing code: (\S+)").Groups[1].Value;
            Assert.False(File.Exists(Path.Combine(dirs[0], "pairing-codes.json")));   // the code waits in the state, not in one instance's folder
            var console = new ConsoleIdentity("console-1", ConsoleSigningKey.Generate());
            var paired = await PairingClient.PairAsync(new Uri($"https://127.0.0.1:{managementB}"), code, console);

            // every instance is that one server: the same id, the pinned certificate, and the console's token accepted
            foreach (var port in new[] { managementA, managementB })
            {
                using var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint));
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}/api/v1/about");
                request.Headers.Add(ManagementAuthentication.TokenHeader, console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", ServerRole.Viewer)));
                var about = await http.SendAsync(request);
                Assert.True(about.IsSuccessStatusCode, $"{port}: {about.StatusCode}");
                Assert.Equal(paired.InstanceId, (string)JsonNode.Parse(await about.Content.ReadAsStringAsync())!["instance_id"]!);
            }
            var clients = new StringWriter();
            Assert.Equal(0, await DbDataProxy.Server.Hosting.Cli.RunAsync(["server", "clients", "--repo", dirs[1]], new DbDataKit.Cli.CliContext(clients, new StringWriter(), TextReader.Null)));
            Assert.Contains("console-1", clients.ToString());

            // the code is spent at every instance
            await Assert.ThrowsAsync<PairingFailedException>(() => PairingClient.PairAsync(new Uri($"https://127.0.0.1:{managementA}"), code, new ConsoleIdentity("console-2", ConsoleSigningKey.Generate())));
        }
        finally
        {
            foreach (var app in _apps)
            {
                try { await app.StopAsync(); } catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
            }
            drop();
        }
    }

    [Theory]
    [InlineData("mssql", "DBKIT_TEST_MSSQL")]
    [InlineData("postgres", "DBKIT_TEST_POSTGRES")]
    public async Task A_semantic_package_a_build_server_deploys_once_through_one_instance_is_served_by_every_instance(string engine, string variable)
    {
        // plan 20 O04: the login, the data source and the model a publishing client sends through one instance are the group's
        var (state, drop) = ScratchState(engine, variable);
        try
        {
            string[] poll = [.. state, "--Proxy:DefinitionPollSeconds=1"];
            var database = state[1]["--Proxy:State:Connection=".Length..];
            var catalog = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "catalog.yaml"));
            // the database behind the model: the tables its SQL source names, in the scratch database (the publish checks each can be read)
            using (System.Data.Common.DbConnection db = engine == "mssql" ? new SqlConnection(database) : new Npgsql.NpgsqlConnection(database))
            {
                db.Open();
                foreach (var table in DbDataKit.Semantic.CatalogLoader.LoadText(catalog).Doc.Sources.Where(s => DbDataKit.Serving.Semantic.SemanticModel.BaseSource(s.Key) == "sqlserver_sales")
                    .SelectMany(s => s.Value.Joins.Values.Select(j => j.Table).Append(s.Value.Fact.Table)).Distinct())
                {
                    using var create = db.CreateCommand();
                    create.CommandText = $"CREATE TABLE {table} (x INTEGER)";
                    create.ExecuteNonQuery();
                }
            }

            var dirs = new[] { "a", "b", "c" }.Select(d => Path.Combine(_root, d)).ToArray();
            var (a, managementA, _) = await ServingInstance(dirs[0], poll);
            var storeA = SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = dirs[0] });
            foreach (var other in dirs[1..])
                foreach (var secret in new[] { GroupSessionKey.Secret, ProxyIdentityModule.SigningKeySecret })
                    using (var key = await storeA.TryResolveAsync(SecretRef.Parse("secret:" + secret)))
                        await SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = other }).SetAsync(SecretName.Parse(secret), key!);
            var (b, _, _) = await ServingInstance(dirs[1], poll);

            // the Build server pairs with the group as a publishing client, through the first instance, and deploys as it does: the login, the data source, the model
            var build = new ConsoleIdentity("build-1", ConsoleSigningKey.Generate());
            var groupState = a.Services.GetRequiredService<StateDatabase>();
            var paired = await PairingClient.PairAsync(new Uri($"https://127.0.0.1:{managementA}"),
                new PairingFiles(ProxyConfigFile.PathIn(dirs[0]), Group: groupState).Codes().Create(ServerRoles.Close(ServerRole.Publisher)).Code, build, ClientKind.Server, build.Id);
            async Task<HttpResponseMessage> Call(HttpMethod method, string path, object? body = null)
            {
                using var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint));
                using var request = new HttpRequestMessage(method, $"https://127.0.0.1:{managementA}{path}");
                request.Headers.Add(ManagementAuthentication.TokenHeader, build.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", ServerRole.Publisher)));
                if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                var response = await http.SendAsync(request);
                Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
                return response;
            }
            // the test engine's TLS certificate is a throwaway one, so a PostgreSQL source asks for encryption without verifying it, as its administrator would write it (PostgresEnv)
            var login = JsonNode.Parse(await (await Call(HttpMethod.Put, "/api/v1/secrets/sales-login", new { value = engine == "postgres" ? database + ";SSL Mode=Require" : database })).Content.ReadAsStringAsync())!;
            var reference = (string)login["reference"]!;
            var source = $"schema: connection/1\nname: sales-ro\nengine: {(engine == "mssql" ? "sqlserver" : "postgres")}\naccess: readonly\nconnection_string_secret: '{reference}'\n";
            var written = JsonNode.Parse(await (await Call(HttpMethod.Put, "/api/v1/data-sources/sales-ro", new { text = source, expected_version = (string?)null })).Content.ReadAsStringAsync())!;
            Assert.True((bool)written["group"]!);
            await Call(HttpMethod.Put, "/api/v1/semantic-models/sales_catalog", new { catalog, sources = new Dictionary<string, string> { ["sqlserver_sales"] = "sales-ro" }, expected_version = (int?)null });

            // none of it is one instance's: no login in an instance's store, no file in an instance's configuration
            using (var kept = await storeA.TryResolveAsync(SecretRef.Parse(reference))) Assert.Null(kept);
            Assert.False(File.Exists(Path.Combine(ProxyConfigFile.DirectoryIn(dirs[0]), "connections", "sales-ro.yaml")));

            // every instance serves it: the model, and the data source it reads with the login the client sent once
            async Task Serves(WebApplication instance, string which)
            {
                for (var i = 0; i < 100 && (instance.Services.GetRequiredService<PublishedSemanticModels>().Served("sales_catalog") is null
                    || !instance.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ProxyOptions>>().CurrentValue.DataSources.ContainsKey("sales-ro")); i++) await Task.Delay(100);
                Assert.Equal(1, instance.Services.GetRequiredService<PublishedSemanticModels>().Served("sales_catalog"));
                var resolved = instance.Services.GetRequiredService<DbDataProxy.Server.Data.DataSourceCatalog>().Resolve(new DbDataProxy.Server.Auth.ProxyPrincipal("management", "management", ["*"]), "sales-ro");
                await using var result = await resolved.Executor.ExecuteQueryAsync(new DbDataProxy.Data.QueryRequest("SELECT 1 FROM fact_sales WHERE 1 = 0", 1, 15), CancellationToken.None);
                await foreach (var _ in result.ReadBatchesAsync(CancellationToken.None)) break;
                Assert.Equal("sales-ro", instance.Services.GetRequiredService<DbDataKit.Serving.Semantic.SemanticModels>().Find("sales_catalog")!.ServingSource("sqlserver_sales"));
                _ = which;
            }
            await Serves(a, "the instance it went through");
            await Serves(b, "the other instance");

            // with the instance it went through stopped, one started now serves it all, the publisher not running either
            await a.StopAsync();
            var (c, _, _) = await ServingInstance(dirs[2], poll);
            await Serves(c, "an instance started after");

            // the client takes the data source away: no instance serves it any more
            var retire = new PublishedDataSources(new DefinitionStore(c.Services.GetRequiredService<StateDatabase>()), c.Services.GetRequiredService<StateDatabase>(), Path.Combine(_root, "unused"), () => { });
            retire.Retire("sales-ro", "ada");
            for (var i = 0; i < 100 && b.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ProxyOptions>>().CurrentValue.DataSources.ContainsKey("sales-ro"); i++) await Task.Delay(100);
            Assert.False(b.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ProxyOptions>>().CurrentValue.DataSources.ContainsKey("sales-ro"));
            Assert.False(File.Exists(Path.Combine(PublishedDataSources.FolderIn(dirs[1]), "sales-ro.yaml")));
        }
        finally
        {
            foreach (var app in _apps)
            {
                try { await app.StopAsync(); } catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
            }
            drop();
        }
    }

    [Theory]
    [InlineData("mssql", "DBKIT_TEST_MSSQL")]
    [InlineData("postgres", "DBKIT_TEST_POSTGRES")]
    public async Task A_rule_a_store_server_publishes_is_enforced_by_every_instance_of_the_group(string engine, string variable)
    {
        // plan 20 O10: the master data models a DbDataStore server publishes, with their rules, are the group's; each instance loads the version published and keeps the last one it could
        var (state, drop) = ScratchState(engine, variable);
        try
        {
            string[] poll = [.. state, "--Proxy:DefinitionPollSeconds=1"];
            var database = state[1]["--Proxy:State:Connection=".Length..];
            var storageEngine = engine == "mssql" ? "sqlserver" : "postgres";
            static string Territories(string? rule) => $$"""
                schemaVersion: 1
                name: territories
                storage: {connection: mdm}
                entities:
                  territory:
                    key: [code]
                    attributes:
                      code: {type: VARCHAR(10), required: true{{rule}}}
                      name: {type: VARCHAR(100), required: true}
                """;
            // the model's tables, as the store's server creates them (the structure is the store's to change)
            var v1 = DbDataKit.DataModel.DataModelLoader.Load(Territories(null)).Model!;
            async Task<System.Data.Common.DbConnection> Open()
            {
                System.Data.Common.DbConnection db = engine == "mssql" ? new SqlConnection(database) : new Npgsql.NpgsqlConnection(database);
                await db.OpenAsync();
                return db;
            }
            await using (var db = await Open())
                await DbDataKit.DataModel.Storage.ModelStorage.CreateAsync(db, DbDataKit.DataModel.Storage.ModelStorage.Plan(v1, storageEngine, v1.Name));

            var dirs = new[] { "a", "b", "c" }.Select(d => Path.Combine(_root, d)).ToArray();
            var (a, managementA, _) = await ServingInstance(dirs[0], poll);
            var storeA = SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = dirs[0] });
            foreach (var other in dirs[1..])
                foreach (var secret in new[] { GroupSessionKey.Secret, ProxyIdentityModule.SigningKeySecret })
                    using (var key = await storeA.TryResolveAsync(SecretRef.Parse("secret:" + secret)))
                        await SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = other }).SetAsync(SecretName.Parse(secret), key!);
            var (b, _, _) = await ServingInstance(dirs[1], poll);

            // the store's server, paired with the group as a publishing client, publishes as it does (StorePublisher): the write login, the data source, the model
            var store = new ConsoleIdentity("store-1", ConsoleSigningKey.Generate());
            var paired = await PairingClient.PairAsync(new Uri($"https://127.0.0.1:{managementA}"),
                new PairingFiles(ProxyConfigFile.PathIn(dirs[0]), Group: a.Services.GetRequiredService<StateDatabase>()).Codes().Create(ServerRoles.Close(ServerRole.Publisher)).Code, store, ClientKind.Server, store.Id);
            async Task<JsonNode> Call(HttpMethod method, string path, object? body = null, HttpStatusCode expect = HttpStatusCode.OK)
            {
                using var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint));
                using var request = new HttpRequestMessage(method, $"https://127.0.0.1:{managementA}{path}");
                request.Headers.Add(ManagementAuthentication.TokenHeader, store.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", ServerRole.Publisher)));
                if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                var response = await http.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == expect, $"{method} {path}: {response.StatusCode} {text}");
                return JsonNode.Parse(text)!;
            }
            var reference = (string)(await Call(HttpMethod.Put, "/api/v1/secrets/mdm-write", new { value = engine == "postgres" ? database + ";SSL Mode=Require" : database }))["reference"]!;
            await Call(HttpMethod.Put, "/api/v1/data-sources/mdm", new { text = $"schema: connection/1\nname: mdm\nengine: {storageEngine}\nconnection_string_secret: '{reference}'\n", expected_version = (string?)null });
            Assert.Equal(1, (int)(await Call(HttpMethod.Put, "/api/v1/entity-models/territories", new Dictionary<string, object?> { ["model"] = Territories(null), ["data_source"] = "mdm", ["expected_version"] = null }))["version"]!);

            // a model on a data source the client did not make is refused
            await Call(HttpMethod.Put, "/api/v1/entity-models/other", new Dictionary<string, object?> { ["model"] = Territories(null).Replace("name: territories", "name: other"), ["data_source"] = "elsewhere", ["expected_version"] = null }, HttpStatusCode.BadRequest);

            // the new rule, published once through the first instance
            var withRule = Territories(", pattern: '^[A-Z]{2}$'");
            Assert.Equal(2, (int)(await Call(HttpMethod.Put, "/api/v1/entity-models/territories", new Dictionary<string, object?> { ["model"] = withRule, ["data_source"] = "mdm", ["expected_version"] = 1 }))["version"]!);

            // every instance enforces it: a write through the model it serves breaks the rule and is refused, naming it; one that keeps it is written
            async Task Enforces(WebApplication instance, string code, int version = 2)
            {
                var models = instance.Services.GetRequiredService<PublishedEntityModels>();
                for (var i = 0; i < 100 && models.Find("territories")?.Version != version; i++) await Task.Delay(100);
                var served = models.Find("territories")!;
                Assert.Equal((version, "mdm"), (served.Version, served.DataSource));
                await using var db = await Open();
                var writer = new DbDataKit.Store.ModelWriter(served.Model, db, storageEngine, schema: served.Model.Name);
                var refused = await Assert.ThrowsAsync<DbDataKit.Store.WriteRefusedException>(() =>
                    writer.InsertAsync("territory", new Dictionary<string, object?> { ["code"] = "north", ["name"] = "North" }, new DbDataKit.Store.WriteActor("ada")));
                Assert.Contains(refused.Violations, v => v.Rule == "pattern" && v.Attribute == "code");
                await writer.InsertAsync("territory", new Dictionary<string, object?> { ["code"] = code, ["name"] = "Somewhere" }, new DbDataKit.Store.WriteActor("ada"));
            }
            await Enforces(a, "NO");
            await Enforces(b, "SO");

            // a version neither instance can load leaves the one each served, and the listing says so
            new DefinitionStore(a.Services.GetRequiredService<StateDatabase>()).Publish(PublishedEntityModels.DefinitionKind, "territories",
                JsonSerializer.Serialize(new PublishedEntityModels.Content("schemaVersion: 1\nname: territories\nentities: nope\n", "mdm", store.Id)), "someone");
            var modelsB = b.Services.GetRequiredService<PublishedEntityModels>();
            for (var i = 0; i < 100 && modelsB.Failed("territories") is null; i++) await Task.Delay(100);
            Assert.Equal((3, 2), (modelsB.Failed("territories")!.Value.Version, modelsB.Find("territories")!.Version));
            var listed = (await Call(HttpMethod.Get, "/api/v1/entity-models"))["models"]!.AsArray().Single()!;
            Assert.Equal((3, 2), ((int)listed["current_version"]!, (int)listed["served_version"]!));

            // put right, and an instance started with the store's server and the first instance stopped serves it
            await Call(HttpMethod.Put, "/api/v1/entity-models/territories", new Dictionary<string, object?> { ["model"] = withRule + "  # again\n", ["data_source"] = "mdm", ["expected_version"] = 3 });
            await a.StopAsync();
            var (c, _, _) = await ServingInstance(dirs[2], poll);
            Assert.Equal(4, c.Services.GetRequiredService<PublishedEntityModels>().Find("territories")!.Version);
            await Enforces(c, "WE", version: 4);
        }
        finally
        {
            foreach (var app in _apps)
            {
                try { await app.StopAsync(); } catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
            }
            drop();
        }
    }

    /// <summary>Makes a model's tables on the scratch database, as <c>dbdatastore model publish</c> does before it sends the model.</summary>
    private static async Task CreateTablesAsync(string engine, string database, string modelText)
    {
        var model = DbDataKit.DataModel.DataModelLoader.Load(modelText).Model!;
        await using System.Data.Common.DbConnection db = engine == "mssql" ? new SqlConnection(database) : new Npgsql.NpgsqlConnection(database);
        await db.OpenAsync();
        await DbDataKit.DataModel.Storage.ModelStorage.CreateAsync(db, DbDataKit.DataModel.Storage.ModelStorage.Plan(model, engine == "mssql" ? "sqlserver" : "postgres", model.Name));
    }

    /// <summary>
    /// What a store's server publishes to the group (O10), as StorePublisher sends it: the login as a secret, the data source <c>mdm</c>, and the entity model on it, through the management API of the instance at
    /// <paramref name="management"/> as a paired publishing client. Returns the group's server id, which the identity home's roles name.
    /// </summary>
    private static async Task<string> PublishEntityModelAsync(string engine, string database, int management, string dataDirectory, StateDatabase groupState, string modelText)
    {
        var publisher = new ConsoleIdentity("store-1", ConsoleSigningKey.Generate());
        var paired = await PairingClient.PairAsync(new Uri($"https://127.0.0.1:{management}"),
            new PairingFiles(ProxyConfigFile.PathIn(dataDirectory), Group: groupState).Codes().Create(ServerRoles.Close(ServerRole.Publisher)).Code, publisher, ClientKind.Server, publisher.Id);
        async Task<JsonNode> Manage(HttpMethod method, string path, object body)
        {
            using var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint));
            using var request = new HttpRequestMessage(method, $"https://127.0.0.1:{management}{path}") { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            request.Headers.Add(ManagementAuthentication.TokenHeader, publisher.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", ServerRole.Publisher)));
            var response = await http.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        }
        var storageEngine = engine == "mssql" ? "sqlserver" : "postgres";
        var name = DbDataKit.DataModel.DataModelLoader.Load(modelText).Model!.Name;
        var reference = (string)(await Manage(HttpMethod.Put, "/api/v1/secrets/mdm-write", new { value = engine == "postgres" ? database + ";SSL Mode=Require" : database }))["reference"]!;
        await Manage(HttpMethod.Put, "/api/v1/data-sources/mdm", new { text = $"schema: connection/1\nname: mdm\nengine: {storageEngine}\nconnection_string_secret: '{reference}'\n", expected_version = (string?)null });
        await Manage(HttpMethod.Put, $"/api/v1/entity-models/{name}", new Dictionary<string, object?> { ["model"] = modelText, ["data_source"] = "mdm", ["expected_version"] = null });
        return paired.InstanceId;
    }

    [Theory]
    [InlineData("mssql", "DBKIT_TEST_MSSQL")]
    [InlineData("postgres", "DBKIT_TEST_POSTGRES")]
    public async Task The_store_module_reads_and_writes_master_data_through_any_instance_with_no_store_server_running(string engine, string variable)
    {
        // plan 20 O08a: the serving layer's store module, called with identity home tokens, reads and writes through the model the store's server published; that server is not running here at all
        var (state, drop) = ScratchState(engine, variable);
        try
        {
            var database = state[1]["--Proxy:State:Connection=".Length..];
            const string Model = """
                schemaVersion: 1
                name: territories
                storage: {connection: mdm}
                entities:
                  territory:
                    key: [code]
                    history: effective_dated
                    attributes:
                      code: {type: VARCHAR(10), required: true, pattern: '^[A-Z]{2}$'}
                      name: {type: VARCHAR(100), required: true}
                """;
            await CreateTablesAsync(engine, database, Model);

            var dirs = new[] { "a", "b" }.Select(d => Path.Combine(_root, d)).ToArray();
            var (storeA, storeB) = (FreePort(), FreePort());
            string[] With(int port) => [.. state, "--Proxy:DefinitionPollSeconds=1", "--Proxy:Modules:0=flightsql", "--Proxy:Modules:1=store", "--Proxy:Store:Address=127.0.0.1", $"--Proxy:Store:Port={port}"];
            var (a, managementA, _) = await ServingInstance(dirs[0], With(storeA));
            var keys = SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = dirs[0] });
            foreach (var secret in new[] { GroupSessionKey.Secret, ProxyIdentityModule.SigningKeySecret })
                using (var key = await keys.TryResolveAsync(SecretRef.Parse("secret:" + secret)))
                    await SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = dirs[1] }).SetAsync(SecretName.Parse(secret), key!);
            var (b, _, _) = await ServingInstance(dirs[1], With(storeB));
            var groupState = a.Services.GetRequiredService<StateDatabase>();

            var serverId = await PublishEntityModelAsync(engine, database, managementA, dirs[0], groupState, Model);
            for (var i = 0; i < 100 && (b.Services.GetRequiredService<PublishedEntityModels>().Find("territories") is null
                || !b.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ProxyOptions>>().CurrentValue.DataSources.ContainsKey("mdm")); i++) await Task.Delay(100);

            // the identity home's people: an author, a viewer, and a service account (an agent); their roles are on the group's server id
            var users = new IdentityUserStore(groupState);
            var tokens = new AccessTokens(groupState, users);
            var directory = new HomeDirectory(groupState);
            string Person(string name, ServerRole roles)
            {
                var user = users.CreateUser(name, $"{name.ToLowerInvariant()}@example.invalid", UserRole.Viewer, emailVerified: true);
                directory.Assign(AssignmentSubject.User(user.Id), serverId, roles);
                return tokens.Create(user.Id, "store", "test").Secret;
            }
            var author = Person("Grace", ServerRole.Viewer | ServerRole.Author);
            var viewer = Person("Linus", ServerRole.Viewer);
            var agentAccount = tokens.CreateServiceAccount("nightly-load", null, "test");
            directory.Assign(AssignmentSubject.User(agentAccount.UserId), serverId, ServerRole.Viewer | ServerRole.Author);
            var agent = tokens.Create(agentAccount.UserId, "load", "test").Secret;

            async Task<(HttpStatusCode Status, JsonNode? Body)> Store(int port, HttpMethod method, string path, string? token, object? body = null)
            {
                using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
                using var request = new HttpRequestMessage(method, $"https://127.0.0.1:{port}/api/v1/models/territories{path}");
                if (token is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                var response = await http.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
            }
            object Insert(string code, string name) => new { changes = new[] { new { action = "insert", entity = "territory", values = new { code, name }, effective_from = "2026-01-01T00:00:00Z" } } };

            // written through one instance, read through the other, now and as of a date
            var written = await Store(storeA, HttpMethod.Post, "/changes", author, Insert("NO", "North"));
            Assert.True(written.Status == HttpStatusCode.OK, written.Body?.ToJsonString());
            var row = await Store(storeB, HttpMethod.Get, "/entities/territory/row?key.code=NO", viewer);
            Assert.Equal(HttpStatusCode.OK, row.Status);
            Assert.Equal("North", (string)row.Body!["values"]!["name"]!);
            var version = (long)row.Body["version"]!;
            var renamed = await Store(storeB, HttpMethod.Post, "/changes", author,
                new { changes = new[] { new { action = "update", entity = "territory", key = new { code = "NO" }, expected_version = version, values = new { name = "Northern" }, effective_from = "2026-06-01T00:00:00Z" } } });
            Assert.True(renamed.Status == HttpStatusCode.OK, renamed.Body?.ToJsonString());
            Assert.Equal("North", (string)(await Store(storeA, HttpMethod.Get, "/entities/territory/row?key.code=NO&as_of=2026-03-01T00:00:00Z", viewer)).Body!["values"]!["name"]!);
            Assert.Equal("Northern", (string)(await Store(storeA, HttpMethod.Get, "/entities/territory/row?key.code=NO", viewer)).Body!["values"]!["name"]!);
            Assert.Equal(2, (await Store(storeA, HttpMethod.Get, "/entities/territory/row?key.code=NO&history=true", viewer)).Body!["versions"]!.AsArray().Count);
            Assert.Single((await Store(storeB, HttpMethod.Get, "/entities/territory/rows", viewer)).Body!["rows"]!.AsArray());

            // a rule violation names the rule, and nothing is written
            var broken = await Store(storeB, HttpMethod.Post, "/changes", author, Insert("south", "South"));
            Assert.Equal((HttpStatusCode)422, broken.Status);
            Assert.Contains(broken.Body!["violations"]!.AsArray(), v => (string)v!["rule"]! == "pattern" && (string)v["attribute"]! == "code");
            Assert.Equal(HttpStatusCode.NotFound, (await Store(storeA, HttpMethod.Get, "/entities/territory/row?key.code=south", viewer)).Status);

            // who may: no token, a viewer's write
            Assert.Equal(HttpStatusCode.Unauthorized, (await Store(storeA, HttpMethod.Get, "/entities/territory/rows", null)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Store(storeA, HttpMethod.Post, "/changes", viewer, Insert("WE", "West"))).Status);

            // an agent's write waits for a person (the model does not let agents write directly), in the group's state: proposed through one instance, approved through the other
            var proposed = await Store(storeA, HttpMethod.Post, "/changes", agent, Insert("WE", "West"));
            Assert.Equal(HttpStatusCode.Accepted, proposed.Status);
            var id = (string)proposed.Body!["id"]!;
            Assert.True((bool)proposed.Body["by_agent"]!);
            Assert.Equal(HttpStatusCode.NotFound, (await Store(storeA, HttpMethod.Get, "/entities/territory/row?key.code=WE", viewer)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Store(storeB, HttpMethod.Post, $"/requests/{id}/approve", agent)).Status);
            Assert.Single((await Store(storeB, HttpMethod.Get, "/requests?state=pending", viewer)).Body!["requests"]!.AsArray());
            var approved = await Store(storeB, HttpMethod.Post, $"/requests/{id}/approve", author);
            Assert.True(approved.Status == HttpStatusCode.OK, approved.Body?.ToJsonString());
            Assert.Equal("approved", (string)approved.Body!["state"]!);
            Assert.Equal("West", (string)(await Store(storeA, HttpMethod.Get, "/entities/territory/row?key.code=WE", viewer)).Body!["values"]!["name"]!);
            Assert.Equal(HttpStatusCode.Conflict, (await Store(storeA, HttpMethod.Post, $"/requests/{id}/reject", author)).Status);
        }
        finally
        {
            foreach (var app in _apps)
            {
                try { await app.StopAsync(); } catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
            }
            drop();
        }
    }

    [Theory]
    [InlineData("mssql", "DBKIT_TEST_MSSQL")]
    [InlineData("postgres", "DBKIT_TEST_POSTGRES")]
    public async Task An_app_reads_and_writes_the_groups_published_model_through_the_apps_host_with_no_store_module_or_store_server(string engine, string variable)
    {
        // plan 20 O08c: the apps host answers an app's data calls from the model published to the group, in its own process; no store module listens and no DbDataStore server runs
        var (state, drop) = ScratchState(engine, variable);
        try
        {
            var database = state[1]["--Proxy:State:Connection=".Length..];
            const string Model = """
                schemaVersion: 1
                name: territories
                storage: {connection: mdm}
                entities:
                  territory:
                    key: [code]
                    attributes:
                      code: {type: VARCHAR(10), required: true, pattern: '^[A-Z]{2}$'}
                      name: {type: VARCHAR(100), required: true}
                  region:
                    key: [code]
                    attributes:
                      code: {type: VARCHAR(10), required: true}
                  district:
                    key: [code]
                    attributes:
                      code: {type: VARCHAR(10), required: true}
                """;
            await CreateTablesAsync(engine, database, Model);

            var dir = Path.Combine(_root, "apps");
            var appsPort = FreePort();
            var (app, management, _) = await ServingInstance(dir, [.. state, "--Proxy:DefinitionPollSeconds=1", "--Proxy:Modules:0=flightsql", "--Proxy:Modules:1=apps",
                "--Proxy:Apps:Address=127.0.0.1", $"--Proxy:Apps:Port={appsPort}", "--Proxy:Apps:Home:ClientId=apps-host"]);
            var groupState = app.Services.GetRequiredService<StateDatabase>();
            await PublishEntityModelAsync(engine, database, management, dir, groupState, Model);
            for (var i = 0; i < 100 && (app.Services.GetRequiredService<PublishedEntityModels>().Find("territories") is null
                || !app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ProxyOptions>>().CurrentValue.DataSources.ContainsKey("mdm")); i++) await Task.Delay(100);

            // an app whose manifest may write territories and read regions, saved as two instances: one on the model's default copy, one bound to a copy the group does not serve
            var instances = new DbDataKit.Apps.AppInstanceStore(new DefinitionStore(groupState));
            instances.PublishApp("schemaVersion: 1\nname: notes\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\nmodels:\n  - model: territories\n    entities: {territory: [read, write], region: [read]}\n", "ada");
            instances.Save(new DbDataKit.Apps.AppInstance("acme", "notes", Bindings: new Dictionary<string, string> { ["territories"] = "default" }), "ada");
            instances.Save(new DbDataKit.Apps.AppInstance("globex", "notes", Bindings: new Dictionary<string, string> { ["territories"] = "globex-data" }), "ada");

            // the tokens the shell's broker hands an app, for a person the home made an author or a viewer on the instance
            var broker = new DbDataKit.Apps.AppTokenBroker(new ConsoleTokenIssuer(ProxyAppsModule.TokenIssuer, await ProxyAppsModule.SigningKeyAsync(new ProxyOptions { DataDirectory = dir }, default)));
            string Token(string instance, string role) => broker.Issue(new DbDataKit.Apps.AppSessions().Create(new HomeIdentity("user-" + role, role == "author" ? "Grace" : "Linus", null, [],
                new Dictionary<string, IReadOnlyList<string>> { ["app:" + instance] = [role] })), instance).Token;
            var (author, viewer) = (Token("acme", "author"), Token("acme", "viewer"));

            async Task<(HttpStatusCode Status, JsonNode? Body)> Data(HttpMethod method, string path, string token, object? body = null, string instance = "acme")
            {
                using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
                using var request = new HttpRequestMessage(method, $"https://127.0.0.1:{appsPort}/apps/{instance}/_data/{path}");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                var response = await http.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                return (response.StatusCode, response.Content.Headers.ContentType?.MediaType == "application/json" && text.Length > 0 ? JsonNode.Parse(text) : JsonValue.Create(text));
            }
            const string Territories = "api/v1/models/territories";
            object Insert(string entity, string code, string? name = null) => new { changes = new[] { new { action = "insert", entity, values = name is null ? (object)new { code } : new { code, name } } } };

            // the author's app writes, and reads it back as one row and a page; the row is in the model's tables, written as the person
            var written = await Data(HttpMethod.Post, Territories + "/changes", author, Insert("territory", "NO", "North"));
            Assert.True(written.Status == HttpStatusCode.OK, written.Body?.ToJsonString());
            Assert.Equal("North", (string)(await Data(HttpMethod.Get, Territories + "/entities/territory/row?key.code=NO", viewer)).Body!["values"]!["name"]!);
            Assert.Single((await Data(HttpMethod.Get, Territories + "/entities/territory/rows", viewer)).Body!["rows"]!.AsArray());
            await using (System.Data.Common.DbConnection db = engine == "mssql" ? new SqlConnection(database) : new Npgsql.NpgsqlConnection(database))
            {
                await db.OpenAsync();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT name FROM territories.territory WHERE code = 'NO'";
                Assert.Equal("North", (string?)await cmd.ExecuteScalarAsync());
            }

            // the model's rules hold for an app as for anyone: a broken one names the rule
            var broken = await Data(HttpMethod.Post, Territories + "/changes", author, Insert("territory", "south", "South"));
            Assert.Equal((HttpStatusCode)422, broken.Status);
            Assert.Contains(broken.Body!["violations"]!.AsArray(), v => (string)v!["rule"]! == "pattern");

            // the person's role and the manifest's scope both decide: a viewer cannot write; regions are read but not written; districts are not the app's at all
            Assert.Equal(HttpStatusCode.Forbidden, (await Data(HttpMethod.Post, Territories + "/changes", viewer, Insert("territory", "WE", "West"))).Status);
            Assert.Equal(HttpStatusCode.OK, (await Data(HttpMethod.Get, Territories + "/entities/region/rows", author)).Status);
            var regionWrite = await Data(HttpMethod.Post, Territories + "/changes", author, Insert("region", "EU"));
            Assert.Equal(HttpStatusCode.Forbidden, regionWrite.Status);
            Assert.Contains("region", (string)regionWrite.Body!["error"]!);
            Assert.Equal(HttpStatusCode.Forbidden, (await Data(HttpMethod.Get, Territories + "/entities/district/rows", author)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Data(HttpMethod.Get, "api/v1/models/elsewhere/entities/x/rows", author)).Status);   // the scope is asked before the group, so nothing says what else is published

            // an instance bound to a copy the group does not serve, and the path apps used with a paired store, are refused with the reason
            var bound = await Data(HttpMethod.Get, Territories + "/entities/territory/rows", Token("globex", "author"), instance: "globex");
            Assert.Equal(HttpStatusCode.NotFound, bound.Status);
            Assert.Contains("globex-data", (string)bound.Body!["error"]!);
            Assert.Equal(HttpStatusCode.Gone, (await Data(HttpMethod.Get, "store-1/" + Territories + "/entities/territory/rows", author)).Status);

            // the apps host pairs with no store any more, and says so
            var output = new StringWriter();
            var error = new StringWriter();
            Assert.NotEqual(0, await Cli.Router().RunAsync(["apps", "store-pair", "--repo", dir], new DbDataKit.Cli.CliContext(output, error, TextReader.Null)));
            Assert.Contains("model publish", error.ToString());
        }
        finally
        {
            foreach (var running in _apps)
            {
                try { await running.StopAsync(); } catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
            }
            drop();
        }
    }

    [Fact]
    public async Task The_models_an_instance_published_before_are_read_into_the_groups_state_once()
    {
        // what plan 19 J13 kept in semantic-models/ of the data directory: two versions, the first one current
        var dir = Path.Combine(_root, "legacy-models");
        var folder = Path.Combine(dir, PublishedSemanticModels.LegacyFolder, "sales_catalog");
        Directory.CreateDirectory(folder);
        var catalog = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "catalog.yaml"));
        File.WriteAllText(Path.Combine(folder, "v0001.yaml"), catalog);
        File.WriteAllText(Path.Combine(folder, "v0002.yaml"), catalog.Replace("Governed sales", "Sales"));
        foreach (var n in new[] { 1, 2 })
            File.WriteAllText(Path.Combine(folder, $"v{n:D4}.json"), JsonSerializer.Serialize(new PublishedVersion(n, "build-1", "Ada", DateTimeOffset.UtcNow, new() { ["sqlserver_sales"] = "sales-ro" }, []), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(Path.Combine(folder, "current"), "1");

        var app = Instance(dir);
        await app.StartAsync();
        var published = app.Services.GetRequiredService<PublishedSemanticModels>();
        Assert.Equal(1, published.Current("sales_catalog"));
        Assert.Equal(1, published.Served("sales_catalog"));
        Assert.Equal(["build-1", "build-1"], published.Versions("sales_catalog").Select(v => v.ManagedBy));
        Assert.Equal("sales-ro", app.Services.GetRequiredService<DbDataKit.Serving.Semantic.SemanticModels>().Find("sales_catalog")!.ServingSource("sqlserver_sales"));
        Assert.False(Directory.Exists(Path.Combine(dir, PublishedSemanticModels.LegacyFolder)));
        Assert.True(Directory.Exists(Path.Combine(dir, PublishedSemanticModels.LegacyFolder + ".imported")));
        Assert.Equal(0, published.ImportFolder(dir));   // once
    }

    [Fact]
    public void The_audit_file_an_instance_wrote_before_is_read_into_the_state_once()
    {
        var directory = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, StateAuditLog.LegacyFileName);
        File.WriteAllLines(file, [
            """{"at":"2026-10-01T10:00:00+00:00","consoleId":"c1","consoleName":"Console","userId":"ada","userName":"Ada","byConsole":true,"command":"data-source.write","target":"erp","outcome":"ok","tokenId":"t1"}""",
            """{"at":"2026-10-02T10:00:00+00:00","consoleId":"c1","consoleName":"Console","userId":"ada","userName":"Ada","byConsole":true,"command":"secret.set","target":"x","outcome":"ok","tokenId":"t2"}""",
        ]);
        var log = new StateAuditLog(ProxyState.Open(directory));

        Assert.Equal(2, log.ImportFile(directory));
        Assert.Equal(["data-source.write", "secret.set"], log.Read().Select(e => e.Command));
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(file + ".imported"));
        Assert.Equal(0, log.ImportFile(directory));   // once
        Assert.Equal(2, log.Read().Count);
    }
}
