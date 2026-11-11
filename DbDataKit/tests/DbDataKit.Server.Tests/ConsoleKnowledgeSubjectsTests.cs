using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Knowledge;
using DbDataKit.Lineage;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Plan 27a KN02, the console's use of the lineage adapter: what exists to be written about is read from the joined lineage graph of the servers and recorded with where it came from; what the graph showed before and no longer shows is gone, but only when every
/// server answered; what an administrator said by hand is never changed by it; an item about a model that is dropped is listed as orphaned.
/// </summary>
public sealed class ConsoleKnowledgeSubjectsTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-subjects-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;
    private HttpClient http = null!;
    private string admin = "";
    private string viewer = "";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        admin = SignIn("Ada", UserRole.Admin);
        viewer = SignIn("Vic", UserRole.Viewer);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private string SignIn(string name, UserRole role) =>
        app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser(name, null, role).Id).Id;

    private ConsoleKnowledge Knowledge => app.Services.GetRequiredService<ConsoleKnowledge>();

    private static LineageNode Member(string catalog, string member, string kind) =>
        new($"suite://b1/semantic/{catalog}/{member}", NodeKind.SemanticMember, $"{catalog}: {member}", false, new Dictionary<string, string> { ["catalog"] = catalog, ["member"] = member, ["member_kind"] = kind });

    private static LineageNode Package(string kind, string name) =>
        new($"suite://r1/package/{kind}/{name}", NodeKind.Deployment, name, false, new Dictionary<string, string> { [DeploymentProperties.Type] = DeploymentTypes.Package, [DeploymentProperties.Kind] = kind, [DeploymentProperties.Name] = name });

    private static LineageGraph Graph(params LineageNode[] nodes) => new(nodes, []);

    private static LineageGraph Both() => Graph(Member("Sales", "revenue", "measure"), Package("integration", "nightly-load"));

    [Fact]
    public void The_subjects_the_graph_shows_are_recorded_as_existing_and_say_where_they_came_from()
    {
        var refresh = Knowledge.RefreshFromLineage(Both(), complete: true);

        Assert.Equal((3, 0, true), (refresh.Found, refresh.NowGone, refresh.Complete));
        var entries = Knowledge.Subjects();
        Assert.All(entries, e => Assert.Equal(("lineage", "exists"), (e.Source, e.Status)));
        Assert.Contains(entries, e => e is { Type: "semantic-model", Name: "Sales", NarrowingKind: "measure", NarrowingName: "revenue" });
        Assert.Contains(entries, e => e is { Type: "integration", Name: "nightly-load" });
        Assert.Equal(SubjectStatus.Exists, Knowledge.Catalog.Status(new KnowledgeSubject("semantic-model", ["Sales"], "measure", "revenue")));
    }

    [Fact]
    public void A_subject_the_graph_showed_before_and_a_complete_answer_no_longer_shows_is_gone_and_the_item_about_it_is_orphaned()
    {
        Knowledge.RefreshFromLineage(Both(), complete: true);

        var refresh = Knowledge.RefreshFromLineage(Graph(Package("integration", "nightly-load")), complete: true);

        Assert.Equal(1, refresh.NowGone);   // the model Sales: its measure is not kept as a thing that was dropped, the object is
        Assert.Equal(SubjectStatus.Gone, Knowledge.Catalog.Status(new KnowledgeSubject("semantic-model", ["Sales"])));
        Assert.Equal(SubjectStatus.Exists, Knowledge.Catalog.Status(new KnowledgeSubject("integration", ["nightly-load"])));
        var item = KnowledgeItemLoader.Load("---\nknowledge: 1\ntitle: Revenue\nrole: reference\nowner: data\nabout:\n  - semantic-model: Sales\n    measure: revenue\n---\ntext", "r.md", "r.md").Item!;
        Assert.True(SubjectResolver.IsOrphaned(item, Knowledge.Catalog));
    }

    [Fact]
    public void An_answer_that_is_not_complete_marks_nothing_gone_and_what_an_administrator_said_is_never_changed()
    {
        Knowledge.RefreshFromLineage(Both(), complete: true);
        Knowledge.SetSubject("build-project", "sales-warehouse", "exists");
        Knowledge.SetSubject("integration", "nightly-load", "gone");                      // an administrator's word wins over the graph

        var refresh = Knowledge.RefreshFromLineage(Graph(), complete: false);

        Assert.Equal(0, refresh.NowGone);
        Assert.Equal(SubjectStatus.Exists, Knowledge.Catalog.Status(new KnowledgeSubject("semantic-model", ["Sales"])));   // a server that did not answer is not a model that was dropped
        Assert.Equal(SubjectStatus.Exists, Knowledge.Catalog.Status(new KnowledgeSubject("build-project", ["sales-warehouse"])));
        Assert.Contains(Knowledge.Subjects(), e => e is { Type: "integration", Name: "nightly-load", Status: "gone", Source: "manual" });
        Knowledge.RefreshFromLineage(Both(), complete: true);
        Assert.Contains(Knowledge.Subjects(), e => e is { Type: "integration", Name: "nightly-load", Status: "gone", Source: "manual" });
    }

    [Fact]
    public async Task The_refresh_is_an_administrators_and_with_no_server_that_answered_it_marks_nothing_gone()
    {
        Knowledge.RefreshFromLineage(Both(), complete: true);

        async Task<(HttpStatusCode Status, JsonNode? Body)> Refresh(string session)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/knowledge/subjects/refresh");
            request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
            var response = await http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, text.Length > 0 && text[0] == '{' ? JsonNode.Parse(text) : null);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await Refresh(viewer)).Status);
        var (status, body) = await Refresh(admin);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False((bool)body!["complete"]!);                        // no server has a lineage to answer with: this says nothing about what exists
        Assert.Equal(0, (int)body["nowGone"]!);
        Assert.Equal(SubjectStatus.Exists, Knowledge.Catalog.Status(new KnowledgeSubject("semantic-model", ["Sales"])));
    }
}
