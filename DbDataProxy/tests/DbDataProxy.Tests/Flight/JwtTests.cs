using DbDataProxy.Client;
using DbDataProxy.Tests.Support;
using DbDataProxy.Server.Hosting;
using DuckDB.NET.Data;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DbDataProxy.Tests.Flight;

public sealed class JwtTests : IAsyncLifetime
{
    private const string Issuer = "https://issuer.test";
    private const string Audience = "api://dbproxy";
    private static readonly byte[] Key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    private WebApplication? _app;
    private string _address = "";
    private string _dir = "";

    public async ValueTask InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dbproxy-jwt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        string db = Path.Combine(_dir, "t.duckdb");
        using (var c = new DuckDBConnection($"Data Source={db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE t AS SELECT 42 AS answer";
            cmd.ExecuteNonQuery();
        }

        var settings = new Dictionary<string, string?>
        {
            ["Proxy:Listen:Address"] = "127.0.0.1",
            ["Proxy:Listen:Port"] = "0",
            ["Proxy:Tls:Mode"] = "None",
            ["Proxy:DataDirectory"] = _dir,
            ["Proxy:Compression:Mode"] = "None",
            ["Proxy:DataSources:analytics:Provider"] = "duckdb",
            ["Proxy:DataSources:analytics:ConnectionString"] = $"Data Source={db}",
            ["Proxy:DataSources:secret:Provider"] = "duckdb",
            ["Proxy:DataSources:secret:ConnectionString"] = $"Data Source={db}",
            ["Proxy:Auth:Jwt:SigningKey"] = Convert.ToBase64String(Key),
            ["Proxy:Auth:Jwt:Issuer"] = Issuer,
            ["Proxy:Auth:Jwt:Audience"] = Audience,
            ["Proxy:Auth:Jwt:Mappings:0:Claim"] = "groups",
            ["Proxy:Auth:Jwt:Mappings:0:Value"] = "analysts",
            ["Proxy:Auth:Jwt:Mappings:0:DataSources:0"] = "analytics",
            ["Proxy:Auth:Jwt:Mappings:0:DefaultDataSource"] = "analytics",
        };
        _app = ProxyHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await _app.StartAsync();
        _address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()
            .Replace("0.0.0.0", "127.0.0.1");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Token(
        string audience = Audience, string issuer = Issuer, TimeSpan? lifetime = null, byte[]? key = null,
        string[]? groups = null, string user = "ada@example.com")
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = now.AddMinutes(-10),
            Expires = now + (lifetime ?? TimeSpan.FromMinutes(5)),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "subject-1",
                ["preferred_username"] = user,
                ["groups"] = groups ?? ["analysts"],
            },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key ?? Key), SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private async Task<long> AnswerAsync(string bearer, string? dataSource = null)
    {
        using var client = new ProxyFlightClient(_address) { Authorization = "Bearer " + bearer };
        var result = await client.QueryAsync("SELECT answer FROM t", new QueryOptions { DataSource = dataSource });
        await foreach (var batch in result)
            return ((Apache.Arrow.Int32Array)batch.Column(0)).GetValue(0)!.Value;
        throw new InvalidOperationException("No rows.");
    }

    [Fact]
    public async Task Valid_token_with_a_mapped_group_can_query_its_data_source()
    {
        Assert.Equal(42, await AnswerAsync(Token()));
    }

    [Fact]
    public async Task Token_cannot_reach_data_sources_its_claims_do_not_grant()
    {
        var ex = await Rpc.ExpectAsync(() => AnswerAsync(Token(), dataSource: "secret"));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Theory]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("expired")]
    [InlineData("key")]
    [InlineData("group")]
    public async Task Invalid_tokens_are_refused(string defect)
    {
        string token = defect switch
        {
            "audience" => Token(audience: "api://someone-else"),
            "issuer" => Token(issuer: "https://evil.test"),
            "expired" => Token(lifetime: TimeSpan.FromMinutes(-30)),
            "key" => Token(key: System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            _ => Token(groups: ["interns"]),
        };
        var ex = await Rpc.ExpectAsync(() => AnswerAsync(token));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task Unsigned_tokens_are_refused()
    {
        string header = Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        long exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        string payload = Base64Url($"{{\"iss\":\"{Issuer}\",\"aud\":\"{Audience}\",\"exp\":{exp},\"groups\":[\"analysts\"]}}");
        var ex = await Rpc.ExpectAsync(() => AnswerAsync($"{header}.{payload}."));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    private static string Base64Url(string json) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
