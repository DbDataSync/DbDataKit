using System.Net;
using DbDataKit.Connections.Http.Tests;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Kit.Http;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>
/// Plan 27a IX10, second half: a task opens an <c>http</c> slot and gets a client signed in as the connection says. The Runner resolves every secret of its copy of the connection on its side and hands the grant over (definition text with
/// references, the values by reference); the kit makes the <see cref="OutsideHttp"/> from it inside the task. Against the real Runner host fixture and a fake OAuth 2 server on a socket.
/// </summary>
public sealed class RunnerHttpSlotTests
{
    private const string ClientSecretVariable = "DBDATAKIT_TEST_IX10_CLIENT_SECRET";
    private const string RefreshVariable = "DBDATAKIT_TEST_IX10_REFRESH";
    private const string KeyVariable = "DBDATAKIT_TEST_IX10_KEY";
    private const string PasswordVariable = "DBDATAKIT_TEST_IX10_PASSWORD";

    private static async Task<ConnectionGrant> GrantAsync(RunnerHarness h, string name, string yaml)
    {
        var (status, _) = await h.CallAsync(HttpMethod.Put, $"/api/v1/connection-copies/{name}", ServerRole.Admin, new { definition = yaml });
        Assert.Equal(HttpStatusCode.OK, status);
        return await h.Engine.Resolver!.ResolveAsync("api", name, "default", CancellationToken.None);
    }

    [Fact]
    public async Task OAuth_client_credentials_signs_in_inside_the_task_with_the_secret_resolved_on_the_runner_and_the_value_is_not_in_the_definition_or_the_grants_text()
    {
        await using var oauth = new FakeOAuthServer();
        Environment.SetEnvironmentVariable(ClientSecretVariable, FakeOAuthServer.ClientSecret);
        try
        {
            await using var h = new RunnerHarness(StateEngineIds.Sqlite);
            await h.StartAsync();
            var yaml = $"schema: connection/2\nname: crm\nkind: http\nendpoint: {oauth.BaseAddress}\nsign_in:\n  method: oauth2_client_credentials\n  token_url: {oauth.BaseAddress}/token\n  client_id: {FakeOAuthServer.ClientId}\n  client_secret_secret: 'env:{ClientSecretVariable}'\n";
            var grant = await GrantAsync(h, "crm", yaml);

            Assert.Equal(yaml, grant.HttpDefinition);
            Assert.DoesNotContain(FakeOAuthServer.ClientSecret, grant.HttpDefinition!, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeOAuthServer.ClientSecret, grant.ToString(), StringComparison.Ordinal);
            Assert.Equal(FakeOAuthServer.ClientSecret, grant.HttpSecrets[$"env:{ClientSecretVariable}"]);

            using var http = OutsideHttp.Open(grant);
            var response = await http.GetAsync("data");
            Assert.Equal(HttpStatusCode.OK, response.Status);
            Assert.Equal(1, oauth.ClientCredentialCalls);
            await http.GetAsync("data");
            Assert.Equal(1, oauth.ClientCredentialCalls);          // the token is cached inside the client

            // the runner's log never saw the value
            Assert.DoesNotContain(FakeOAuthServer.ClientSecret, string.Join('\n', h.Log));
        }
        finally { Environment.SetEnvironmentVariable(ClientSecretVariable, null); }
    }

    [Fact]
    public async Task OAuth_authorization_code_refreshes_from_the_refresh_token_the_runner_holds_and_a_revoked_one_is_a_permanent_login_failure()
    {
        await using var oauth = new FakeOAuthServer();
        oauth.RefreshTokens["refresh-held"] = true;
        Environment.SetEnvironmentVariable(ClientSecretVariable, FakeOAuthServer.ClientSecret);
        Environment.SetEnvironmentVariable(RefreshVariable, "refresh-held");
        try
        {
            await using var h = new RunnerHarness(StateEngineIds.Sqlite);
            await h.StartAsync();
            var yaml = $"schema: connection/2\nname: crm\nkind: http\nendpoint: {oauth.BaseAddress}\nsign_in:\n  method: oauth2_authorization_code\n  authorize_url: {oauth.BaseAddress}/authorize\n  token_url: {oauth.BaseAddress}/token\n  client_id: {FakeOAuthServer.ClientId}\n  client_secret_secret: 'env:{ClientSecretVariable}'\n  refresh_token_secret: 'env:{RefreshVariable}'\n";
            var grant = await GrantAsync(h, "crm", yaml);
            Assert.Equal("refresh-held", grant.HttpSecrets[$"env:{RefreshVariable}"]);

            using (var http = OutsideHttp.Open(grant))
            {
                Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("data")).Status);
                Assert.Equal(1, oauth.RefreshCalls);
            }

            oauth.RevokeEverything();
            using var again = OutsideHttp.Open(grant);
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => again.GetAsync("data"));
            Assert.DoesNotContain("refresh-held", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeOAuthServer.ClientSecret, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClientSecretVariable, null);
            Environment.SetEnvironmentVariable(RefreshVariable, null);
        }
    }

    [Fact]
    public async Task An_API_key_in_the_query_and_basic_sign_in_use_the_values_the_runner_resolved_and_a_secret_the_runner_does_not_have_names_the_slot_and_the_reference()
    {
        Environment.SetEnvironmentVariable(KeyVariable, "key-value-91ab");
        Environment.SetEnvironmentVariable(PasswordVariable, "pw-value-77cd");
        try
        {
            await using var h = new RunnerHarness(StateEngineIds.Sqlite);
            await h.StartAsync();
            var key = await GrantAsync(h, "keyed", $"schema: connection/2\nname: keyed\nkind: http\nendpoint: https://api.example/v1\nsign_in:\n  method: api_key\n  in: query\n  name: k\n  key_secret: 'env:{KeyVariable}'\n");
            var seen = new List<HttpRequestMessage>();
            using (var http = OutsideHttp.Open(key, connection: new DbDataKit.Connections.Http.HttpConnectionOptions(new Recording(seen))))
                await http.GetAsync("things");
            Assert.Equal("?k=key-value-91ab", seen[0].RequestUri!.Query);

            var basic = await GrantAsync(h, "basic", $"schema: connection/2\nname: basic\nkind: http\nendpoint: https://api.example/v1\nsign_in:\n  method: basic\n  user: svc\n  password_secret: 'env:{PasswordVariable}'\n");
            seen.Clear();
            using (var http = OutsideHttp.Open(basic, connection: new DbDataKit.Connections.Http.HttpConnectionOptions(new Recording(seen))))
                await http.GetAsync("things");
            Assert.Equal("Basic " + Convert.ToBase64String("svc:pw-value-77cd"u8.ToArray()), seen[0].Headers.Authorization!.ToString());

            Environment.SetEnvironmentVariable(PasswordVariable, null);
            var missing = await Assert.ThrowsAsync<DbDataKit.Runner.Protocol.Server.SlotResolutionException>(() => GrantAsync(h, "basic", $"schema: connection/2\nname: basic\nkind: http\nendpoint: https://api.example/v1\nsign_in:\n  method: basic\n  user: svc\n  password_secret: 'env:{PasswordVariable}'\n"));
            Assert.Contains("'api'", missing.Message, StringComparison.Ordinal);
            Assert.Contains(PasswordVariable, missing.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(KeyVariable, null);
            Environment.SetEnvironmentVariable(PasswordVariable, null);
        }
    }

    [Fact]
    public async Task A_client_certificate_stored_as_a_secret_is_loaded_inside_the_task_and_a_dry_run_grant_refuses_writes()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=task", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        const string variable = "DBDATAKIT_TEST_IX10_CERT";
        Environment.SetEnvironmentVariable(variable, Convert.ToBase64String(cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx)));
        try
        {
            await using var h = new RunnerHarness(StateEngineIds.Sqlite);
            await h.StartAsync();
            var grant = await GrantAsync(h, "mtls", $"schema: connection/2\nname: mtls\nkind: http\nendpoint: https://api.example/v1\nsign_in:\n  method: client_certificate\n  certificate_secret: 'env:{variable}'\n");
            Assert.Single(grant.HttpSecrets);
            using (OutsideHttp.Open(grant)) { }                       // the certificate loads from the handed-over secret

            var seen = new List<HttpRequestMessage>();
            var dry = new ConnectionGrant { Slot = "api", Connection = "mtls", HttpDefinition = "schema: connection/2\nname: mtls\nkind: http\nendpoint: https://api.example/v1\n", DryRun = true };
            using var http = OutsideHttp.Open(dry, connection: new DbDataKit.Connections.Http.HttpConnectionOptions(new Recording(seen)));
            var response = await http.SendJsonAsync(HttpMethod.Post, "things", new { a = 1 });
            Assert.Equal(HttpStatusCode.NoContent, response.Status);
            Assert.Empty(seen);
            Assert.Single(http.WouldSend);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    private sealed class Recording(List<HttpRequestMessage> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            seen.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
