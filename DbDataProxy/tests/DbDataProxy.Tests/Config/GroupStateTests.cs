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
            string[] state = [$"--Proxy:State:Engine={engine}", $"--Proxy:State:ConnectionString={connection}"];

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
            using (var cmd = database.Command(conn, "SELECT TokenHash, Principal FROM ClientSessions;"))
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
            var c = Instance(Path.Combine(_root, "c"), state);
            var refused = await Assert.ThrowsAnyAsync<SecretConfigurationException>(() => c.StartAsync());
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
        return ([$"--Proxy:State:Engine={engine}", $"--Proxy:State:ConnectionString={connection}"], () =>
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
            var pairing = new PairingFiles(ProxyConfigFile.PathIn(dirA), Path.Combine(dirA, "pairing-codes.json"));
            var paired = await PairingClient.PairAsync(new Uri($"https://127.0.0.1:{managementA}"), pairing.Codes().Create(ServerRole.Viewer | ServerRole.Operator | ServerRole.Admin).Code, console);
            var storeA = SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = dirA });
            var storeB = SecretsRuntime.OpenStore(new ProxyOptions { DataDirectory = dirB });
            foreach (var secret in new[] { GroupSessionKey.Secret, ProxyIdentityModule.SigningKeySecret })
                using (var key = await storeA.TryResolveAsync(SecretRef.Parse("secret:" + secret)))
                    await storeB.SetAsync(SecretName.Parse(secret), key!);
            // the same instance id and paired console: the group is one server to the console
            Directory.CreateDirectory(ProxyConfigFile.DirectoryIn(dirB));
            File.Copy(ProxyConfigFile.PathIn(dirA), ProxyConfigFile.PathIn(dirB));
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
            var ada = Assert.Single(new UserStore(database).List());

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
            new UserStore(database).AddCredential(ada.Id, CredentialMethods.Passkey, Base64Url.EncodeToString(credentialId), Convert.ToBase64String([0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20, .. q.Q.X!, 0x22, 0x58, 0x20, .. q.Q.Y!]), label: "Passkey");
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
            string[] poll = [.. state, "--Proxy:State:DefinitionPollSeconds=1"];
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
                new PairingFiles(ProxyConfigFile.PathIn(dirs[0]), Path.Combine(dirs[0], "pairing-codes.json")).Codes().Create(ServerRole.Viewer | ServerRole.Operator | ServerRole.Admin).Code, console);
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
