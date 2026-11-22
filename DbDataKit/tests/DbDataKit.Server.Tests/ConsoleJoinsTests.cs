using System.Net;
using DbDataKit.Cli;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// <c>setup invite</c> and the console's join route: the command makes a token row and prints the <c>pair</c> command once, refuses what it cannot make,
/// and the route answers a server that has no sign-in (a request that is not a join is a 400, never a 401).
/// </summary>
public sealed class ConsoleJoinsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-joins-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private async Task<(int Exit, string Out, string Err, IDictionary<string, object?> Data)> Run(params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var context = new CliContext(o, e, TextReader.Null);
        var exit = await ConsoleCli.RunAsync([.. args, "--repo", directory], context);
        return (exit, o.ToString(), e.ToString(), context.Data);
    }

    [Fact]
    public async Task Setup_invite_prints_the_pair_command_once_and_keeps_only_the_token_s_verifier()
    {
        Assert.Contains("has no certificate yet", (await Run("setup", "invite", "runner")).Err);   // before the console's first start
        var certificate = ConsoleHost.Certificate(directory);

        var made = await Run("setup", "invite", "runner", "--role", "operator", "--group", "night", "--minutes", "30", "--url", "https://console.example.com:7800");
        Assert.True(made.Exit == 0, made.Err);
        var command = (string)made.Data["command"]!;
        Assert.StartsWith("dbdatarunner pair --host https://console.example.com:7800 --token ", command);
        Assert.EndsWith($"--console-thumbprint {Thumbprints.Format(PairingProof.CertificateFingerprint(certificate))}", command);
        var token = command.Split(' ')[5];
        Assert.NotNull(JoinTokens.Normalize(token));

        var record = new StateJoinTokenStore(ConsoleState.Open(directory)).All().Single();
        Assert.Equal((made.Data["token_id"], "runner", "night", JoinTokens.VerifierFor(token)), ((object?)record.Id, record.Product, record.Group, record.Verifier));
        Assert.True(ServerRoles.Allows(record.Ceiling, ServerRole.Operator) && !ServerRoles.Allows(record.Ceiling, ServerRole.Admin));
        Assert.InRange(record.ExpiresAt - record.MadeAt, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));
        var audit = new AuditLog(ConsoleState.Open(directory), ConsoleState.ServiceId).Read(new AuditQuery(VerbPrefix: ConsoleJoins.InviteVerb));
        Assert.DoesNotContain(token, Assert.Single(audit).Entry.Detail);

        Assert.Contains("only a server on this machine reaches", (await Run("setup", "invite", "gateway", "--url", "https://localhost:7800")).Out);
        Assert.Equal(CliExit.Usage, (await Run("setup", "invite", "store")).Exit);
        Assert.Equal(CliExit.Usage, (await Run("setup", "invite", "runner", "--minutes", "2000")).Exit);
        Assert.Equal(CliExit.Usage, (await Run("setup", "invite", "runner", "--role", "chief")).Exit);
        Assert.Equal(CliExit.Usage, (await Run("setup", "invite", "runner", "--url", "http://console:7800")).Exit);
    }

    [Fact]
    public async Task The_join_route_answers_without_a_sign_in()
    {
        var app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        try
        {
            using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = new Uri(app.Urls.First()) };
            using var response = await http.PostAsync(JoinEndpoint.Path, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("not a join", await response.Content.ReadAsStringAsync());
            Assert.NotNull(app.Services.GetRequiredService<ConsoleJoins>());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
