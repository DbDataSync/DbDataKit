using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Plan 27a KN10 and KN17 in the console: what a publish would check is shown first (the Knowledge page's Check tab, <c>GET /api/knowledge/check</c>) and is the same as <c>dbdatakit knowledge check</c> says on the command line: a bad item with file and line, a subject nobody knows,
/// and an instruction that would push an agent over its budget, naming the agent. The budget is read from the agent's files in the console's directory.
/// </summary>
public sealed class ConsoleKnowledgeCheckTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-kcheck-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;
    private HttpClient http = null!;
    private string viewer = "";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        Write("agents/helper/agent.yml", "agent: 1\nname: helper\naudience: back-end\nmodel: { slot: default }\ninstructions: [role.md]\nscopes: { semantic-models: [Sales] }\n");
        Write("agents/helper/role.md", "You help with the Sales model. Be brief.");
        Write("knowledge/small.md", Item("Small", "Revenue means net revenue.", "about:\n  - semantic-model: Sales"));
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        viewer = app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser("Vic", null, UserRole.Viewer).Id).Id;
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Item(string title, string body, string about) => $"---\nknowledge: 1\ntitle: {title}\nrole: instruction\n{about}\nowner: administrators\n---\n{body}\n";

    private async Task<JsonNode> Check()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/knowledge/check");
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={viewer}");
        var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    [Fact]
    public async Task A_clean_project_passes_and_says_what_each_agent_would_be_given()
    {
        var project = (await Check())["projects"]!.AsArray().Single()!;

        Assert.Equal("project", (string?)project["name"]);
        Assert.True((bool)project["ok"]!);
        var reach = project["reaches"]!.AsArray().Single()!;
        Assert.Equal("helper", (string?)reach["agent"]);
        Assert.False((bool)reach["pushesOver"]!);
        Assert.True((int)reach["adds"]! > 0);
        Assert.Equal(25_600, (int)reach["budget"]!);   // a fifth of the 128,000 window an agent with no model bound is counted against
    }

    [Fact]
    public async Task A_bad_subject_and_an_instruction_over_the_budget_are_refused_naming_file_line_and_agent()
    {
        Write("knowledge/ghost.md", Item("Ghost", "About nothing.", "about:\n  - semantic-model: Ghosts"));
        Write("knowledge/big.md", Item("Big", string.Join(' ', Enumerable.Repeat("Revenue is net of returns and discounts and is booked when the order ships.", 1800)), "about:\n  - semantic-model: Sales"));
        app.Services.GetRequiredService<ConsoleKnowledge>().SetSubject("semantic-model", "Sales", "exists");

        var project = (await Check())["projects"]!.AsArray().Single()!;

        Assert.False((bool)project["ok"]!);
        var diagnostics = project["diagnostics"]!.AsArray().Select(d => d!).ToList();
        var ghost = diagnostics.Single(d => (string?)d["code"] == "DBD-KNW-010");
        Assert.Equal(("ghost.md", 6), ((string?)ghost["file"], (int)ghost["line"]!));
        var over = diagnostics.Single(d => (string?)d["code"] == "DBD-KNW-040");
        Assert.Equal("big.md", (string?)over["file"]);
        Assert.Contains("'helper'", (string?)over["message"]);
        Assert.True((bool)project["reaches"]!.AsArray().Single()!["pushesOver"]!);
    }

    [Fact]
    public async Task The_command_line_says_the_same_as_the_screen()
    {
        Write("knowledge/big.md", Item("Big", string.Join(' ', Enumerable.Repeat("Revenue is net of returns and discounts and is booked when the order ships.", 1800)), "about:\n  - semantic-model: Sales"));
        var (output, error) = (new StringWriter(), new StringWriter());

        var exit = await ConsoleCli.RunAsync(["knowledge", "check", "--repo", directory], new CliContext(output, error, TextReader.Null));

        Assert.NotEqual(0, exit);
        Assert.Contains("DBD-KNW-040", error.ToString());
        Assert.Contains("'helper'", error.ToString());
        Assert.Contains("big.md", error.ToString());

        var (cleanOut, cleanErr) = (new StringWriter(), new StringWriter());
        File.Delete(Path.Combine(directory, "knowledge", "big.md"));
        Assert.Equal(0, await ConsoleCli.RunAsync(["knowledge", "check", "--repo", directory], new CliContext(cleanOut, cleanErr, TextReader.Null)));
        Assert.Contains("1 item(s) checked", cleanOut.ToString());
    }
}
