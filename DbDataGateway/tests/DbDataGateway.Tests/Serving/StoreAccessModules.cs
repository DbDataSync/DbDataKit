using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.Serving.Conformance;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.Secrets;
using DbDataGateway.Server.Serving;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Tests.Serving;

/// <summary>
/// What the rules table and the column rules of the Store half give each person, written out from the data (not asked of any module): the rows of <c>territory</c> a person's groups include and no group excludes, each as <c>code|region</c>, the region
/// as the strongest treatment of the person's groups leaves it (hidden: not in the row; nulled; masked as the fixed mask). The Store has no serving core to compare with (<c>IServingEntities</c> has no host), so this is the reference its modules are held to.
/// </summary>
internal sealed class StoreOracle(ModuleAccessRig rig) : IConformanceModule
{
    public static readonly ConformanceQuestion Territory = new("territory", "the rows of the territory entity");

    private static readonly (string Code, string? Region)[] Rows = [("E1", "East"), ("E2", "East"), ("E3", "East"), ("W1", "West"), ("N1", "North"), ("X1", null)];

    /// <summary>The rules table <c>rules.region_access</c> and <c>rules.closed_codes</c> as the test has changed them.</summary>
    public List<(string Who, string? Region)> RegionAccess { get; } = [("east", "East"), ("emea", "East"), ("emea", "West"), ("nullers", null)];

    public List<(string Who, string Code)> Closed { get; } = [("east", "E3")];

    /// <summary>The column rules on <c>region</c> by group: <c>mask</c>, <c>null</c> or <c>hide</c>.</summary>
    public Dictionary<string, string> Treatments { get; } = new(StringComparer.Ordinal) { ["east"] = "mask", ["nullers"] = "hide" };

    public string Name => "oracle";

    public bool Asks(ConformanceQuestion question) => true;

    public Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default)
    {
        var groups = rig.People[person].Groups;
        var treatment = new[] { "hide", "null", "mask" }.FirstOrDefault(t => groups.Any(g => Treatments.GetValueOrDefault(g) == t));
        var seen = Rows.Where(row => RegionAccess.Any(r => groups.Contains(r.Who) && r.Region == row.Region) && !Closed.Any(c => groups.Contains(c.Who) && c.Code == row.Code));
        return Task.FromResult(ModuleAnswer.Of(seen.Select(row => treatment switch
        {
            "hide" => row.Code,
            "null" => row.Code + "|NULL",
            "mask" => row.Code + "|" + (row.Region is null ? "NULL" : "********"),
            _ => row.Code + "|" + (row.Region ?? "NULL"),
        })));
    }
}

/// <summary>A module that reads rows of an entity of a published Store model through the group's Store.</summary>
internal abstract class StoreModule(ModuleAccessRig rig, string name) : RigModule(rig, name)
{
    public override bool Asks(ConformanceQuestion question) => question.Id == "territory";

    /// <summary>The rows of a Store answer (<c>{rows: [{values: {...}}]}</c>) as <c>code|region</c>: an attribute the answer leaves out is not in the text.</summary>
    protected static ModuleAnswer RowsOf(JsonNode body) =>
        ModuleAnswer.Of(body["rows"]!.AsArray().Select(r => r!["values"]!.AsObject()).Select(values => string.Join("|", new[] { "code", "region" }.Where(values.ContainsKey).Select(a => ModuleAnswer.Cell(ModuleAccessRig.JsonCell(values[a]))))));

    protected GroupStoreApi Api() => GatewayStoreModule.ApiOf(Rig.Get<IOptionsMonitor<GatewayOptions>>(), Rig.Get<StateDatabase>(), Rig.Get<SecretsRuntime>(), Rig.Get<PublishedEntityModels>());
}

/// <summary>The group's Store API itself, called as the person with no groups on the call (the home's groups are read for the person).</summary>
internal sealed class StoreApiModule(ModuleAccessRig rig) : StoreModule(rig, "store api")
{
    public override async Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default)
    {
        var p = Person(person);
        var answer = await Api().RowsAsync(new StoreCaller(p.UserId, p.Name, false, ServerRole.Viewer), "sales", "territory", new Dictionary<string, string>(), cancellationToken);
        return answer.Status == 200 ? RowsOf(answer.Body) : ModuleAnswer.Refusal($"{answer.Status}: {answer.Body.ToJsonString()}");
    }
}

/// <summary>The <c>store</c> module over its HTTPS listener.</summary>
internal sealed class StoreHttpModule(ModuleAccessRig rig) : StoreModule(rig, "store module (http)")
{
    public override async Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default)
    {
        using var http = ModuleAccessRig.Trusting();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{Rig.StorePort}/api/v1/models/sales/entities/territory/rows");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Person(person).Token);
        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.OK) return RowsOf(JsonNode.Parse(text)!);
        return ModuleAnswer.Refusal($"{(int)response.StatusCode}: {text}");
    }
}

/// <summary>An app's data call as the apps host hands it to the group (<see cref="GroupAppData"/>): the person and the roles, no groups.</summary>
internal sealed class AppsDataModule(ModuleAccessRig rig) : StoreModule(rig, "apps host (data call)")
{
    public override async Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default)
    {
        var p = Person(person);
        var call = new AppDataCall("conformance-app", p.UserId, p.Name, ServerRole.Viewer, [TokenScope.Entry("sales", "territory", write: false)], AppDataRoute.Rows, "sales", "territory", null, new Dictionary<string, string>(), []);
        var answer = await new GroupAppData(Api()).SendAsync(call, cancellationToken);
        return answer.Status == 200 ? RowsOf(JsonNode.Parse(answer.Body)!) : ModuleAnswer.Refusal($"{answer.Status}: {Encoding.UTF8.GetString(answer.Body)}");
    }
}

/// <summary>The <c>mcp</c> module's read tool <c>read_entity_rows</c>.</summary>
internal sealed class McpEntityModule(ModuleAccessRig rig) : StoreModule(rig, "mcp (read_entity_rows)")
{
    public override async Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default)
    {
        using var http = ModuleAccessRig.Trusting();
        var call = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "read_entity_rows", ["arguments"] = new JsonObject { ["model"] = "sales", ["entity"] = "territory" } } };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{Rig.McpPort}/mcp") { Content = new StringContent(call.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Person(person).Token);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!["result"]!;
        return (bool)result["isError"]! ? ModuleAnswer.Refusal((string)result["content"]![0]!["text"]!) : RowsOf(result["structuredContent"]!);
    }
}
