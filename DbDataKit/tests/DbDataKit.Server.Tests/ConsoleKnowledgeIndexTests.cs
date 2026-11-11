using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Knowledge;
using DbDataKit.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Plan 27a KN28 with KN05, KN07 and KN08 on a real console: the Knowledge page's index over the console's own knowledge folder. An embedding provider (Ollama's embed API, answered here by a handler that embeds with the deterministic fake and a few synonyms, so
/// "turnover" is near "revenue") is added as a connection, Test reports the dimensions, the <c>embeddings</c> slot binds it, the index is built (words at once, vectors by the background step), a search finds by words, vectors and both, an edited item is embedded
/// again alone, another model builds beside the first which keeps answering until it is complete, and a failing model is shown in the failures. On SQLite the console's index has words only and the page says why; on SQL Server 2025 and PostgreSQL with pgvector (the shared test engines) it has vectors too.
/// </summary>
public sealed class ConsoleKnowledgeIndexTests : IAsyncLifetime
{
    private const string Provider = "schema: connection/2\nkind: model-provider\nname: emb\nmodel_provider:\n  provider: ollama\n  purpose: embeddings\n  models:\n    - { id: fake-embed, window: 8192 }\n    - { id: fake-embed-2, window: 8192 }\n    - { id: refusing, window: 8192 }\n";

    private static readonly Dictionary<string, string> Synonyms = new() { ["turnover"] = "revenue", ["income"] = "revenue" };

    /// <summary>Answers Ollama's embed API with the fake embedder (32 dimensions; 24 for the second model; the third refuses a text that mentions xylophone), and keeps what it was asked to embed.</summary>
    private sealed class Embedder : HttpMessageHandler
    {
        public List<string> Embedded { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Assert.Equal("/api/embed", request.RequestUri!.AbsolutePath);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellation))!;
            var model = (string)body["model"]!;
            var inputs = body["input"]!.AsArray().Select(n => (string)n!).ToList();
            if (model == "refusing" && inputs.Any(i => i.Contains("xylophone", StringComparison.Ordinal)))
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{ "error": "the input is too long for this model" }""", Encoding.UTF8, "application/json") };
            lock (Embedded) Embedded.AddRange(inputs);
            var fake = new FakeEmbeddingGenerator(model == "fake-embed-2" ? 24 : 32, model);
            var vectors = inputs.Select(i => fake.Vector(string.Join(' ', i.Split(' ', '\n').Select(w => Synonyms.GetValueOrDefault(w.ToLowerInvariant().Trim('.', ',', '?'), w)))));
            var json = new JsonObject { ["model"] = model, ["embeddings"] = new JsonArray([.. vectors.Select(v => (JsonNode)new JsonArray([.. v.Select(x => (JsonNode)JsonValue.Create(x)!)]))]), ["prompt_eval_count"] = inputs.Count };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-index-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Embedder embedder = new();
    private ScratchStateDatabase? scratch;
    private WebApplication app = null!;
    private HttpClient http = null!;
    private string admin = "";
    private string viewer = "";

    public static TheoryData<string> Engines => ["sqlite", "mssql", "postgres"];

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(directory, "knowledge"));
        File.WriteAllText(Path.Combine(directory, "knowledge", "revenue.md"), "---\nknowledge: 1\ntitle: Revenue\nrole: reference\nowner: finance\nabout:\n  - install: true\n---\n# Revenue\n\nRevenue means net revenue: gross sales minus returns minus discounts.\n");
        File.WriteAllText(Path.Combine(directory, "knowledge", "region.md"), "---\nknowledge: 1\ntitle: Region\nrole: reference\nowner: sales\nabout:\n  - install: true\n---\nIn 2025 the EMEA region was split into EU and MEA; older rows carry the old code.\n");
        return ValueTask.CompletedTask;
    }

    private async Task Start(string engine)
    {
        StateSettings? state = null;
        if (engine != "sqlite")
        {
            scratch = ScratchStateDatabase.For(engine);
            state = new StateSettings(engine, scratch.Connection);
        }
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0"), State: state), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"), configure: services => services.AddSingleton(new ModelHttp(embedder)));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        admin = SignIn("Ada", UserRole.Admin);
        viewer = SignIn("Vic", UserRole.Viewer);
    }

    public async ValueTask DisposeAsync()
    {
        http?.Dispose();
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        scratch?.Dispose();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private string SignIn(string name, UserRole role) =>
        app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser(name, null, role).Id).Id;

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Call(HttpMethod method, string path, string session, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 && text[0] is '{' or '[' ? JsonNode.Parse(text) : null);
    }

    private async Task<JsonNode> State()
    {
        await app.Services.GetRequiredService<ConsoleKnowledgeIndex>().WaitAsync();
        var (status, body) = await Call(HttpMethod.Get, "/api/knowledge/index", admin);
        Assert.Equal(HttpStatusCode.OK, status);
        return body!;
    }

    private async Task Bind(string model)
    {
        var (status, body) = await Call(HttpMethod.Put, "/api/knowledge/index/embedding", admin, new EmbeddingBody("emb", model));
        Assert.True(status == HttpStatusCode.OK, body?.ToJsonString());
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_index_is_built_over_the_files_searched_by_words_and_meaning_and_follows_an_edit_and_a_model_change(string engine)
    {
        await Start(engine);
        var vectors = engine != "sqlite";

        // nothing is created by looking: the index is not built, and the model is not bound
        var before = await State();
        Assert.False((bool)before["opened"]!);
        Assert.Null((string?)before["embedding"]!["provider"]);

        // an embedding provider is a connection; Test embeds a sample and reports the dimensions; a viewer may not bind
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/outside-connections/emb", admin, new YamlBody(Provider))).Status);
        var (_, tested) = await Call(HttpMethod.Post, "/api/agents/models/providers/emb/test", admin, new TestModelBody("fake-embed"));
        Assert.True((bool)tested!["ok"]!, tested.ToJsonString());
        Assert.Equal(32, (int)tested["dimensions"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, "/api/knowledge/index/embedding", viewer, new EmbeddingBody("emb", "fake-embed"))).Status);
        await Bind("fake-embed");
        Assert.Contains("\"embeddings\":", File.ReadAllText(Path.Combine(directory, "bindings.yml")));
        var bound = (await State())["embedding"]!;
        Assert.Equal(("emb", "fake-embed", "emb/fake-embed", true), ((string)bound["provider"]!, (string)bound["model"]!, (string)bound["modelId"]!, (bool)bound["usable"]!));

        // build: words at once, vectors by the background step
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/knowledge/index/build", viewer)).Status);
        var (built, build) = await Call(HttpMethod.Post, "/api/knowledge/index/build", admin);
        Assert.Equal(HttpStatusCode.OK, built);
        Assert.Equal(2, (int)build!["sync"]!["added"]!);
        var after = await State();
        Assert.True((bool)after["opened"]!);
        Assert.Equal(2, (int)after["items"]!);
        Assert.Equal(vectors, (bool)after["vectorsAvailable"]!);
        if (vectors)
        {
            Assert.Equal(("emb/fake-embed", 32), ((string)after["vectors"]!["readyModel"]!, (int)after["vectors"]!["readyDimension"]!));
            Assert.Equal((int)after["chunks"]!, (int)after["vectors"]!["embedded"]!);
            Assert.Null(after["vectorsProblem"]);
        }
        else Assert.Contains("knowledge_vectors", (string)after["vectorsProblem"]!);
        Assert.Null((string?)after["lastRun"]?["problem"]);

        // search: words find what has the words; meaning finds what has the sense; both is words where there are no vectors, and says why
        var words = (await Call(HttpMethod.Post, "/api/knowledge/search", admin, new SearchBody("net revenue returns", "words"))).Body!;
        Assert.Equal("project:revenue.md", (string)words["hits"]![0]!["itemId"]!);
        var turnover = (await Call(HttpMethod.Post, "/api/knowledge/search", viewer, new SearchBody("what is our turnover", "both"))).Body!;
        if (vectors)
        {
            Assert.Equal("both", (string)turnover["used"]!);
            Assert.Equal("project:revenue.md", (string)turnover["hits"]![0]!["itemId"]!);
            Assert.Equal("words", (string)(await Call(HttpMethod.Post, "/api/knowledge/search", admin, new SearchBody("what is our turnover", "words"))).Body!["used"]!);
            Assert.Empty((await Call(HttpMethod.Post, "/api/knowledge/search", admin, new SearchBody("what is our turnover", "words"))).Body!["hits"]!.AsArray());
        }
        else
        {
            Assert.Equal("words", (string)turnover["used"]!);
            Assert.Contains("no vector module", (string)turnover["note"]!);
            Assert.Empty(turnover["hits"]!.AsArray());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, "/api/knowledge/search", admin, new SearchBody(" "))).Status);

        if (!vectors) return;

        // an edit: the file changes, the index follows in the background and only the changed chunk is embedded again
        var embeddedBefore = embedder.Embedded.Count;
        var edited = "---\nknowledge: 1\ntitle: Revenue\nrole: reference\nowner: finance\nabout:\n  - install: true\n---\n# Revenue\n\nRevenue means net revenue: gross sales minus returns minus discounts and minus rebates.\n";
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/knowledge/items/project/revenue.md", admin, new ItemBody(edited))).Status);
        var edit = await State();
        Assert.Equal(embeddedBefore + 1, embedder.Embedded.Count);
        Assert.Equal((int)edit["chunks"]!, (int)edit["vectors"]!["embedded"]!);
        Assert.Contains("rebates", (string)(await Call(HttpMethod.Post, "/api/knowledge/search", admin, new SearchBody("rebates", "vectors"))).Body!["hits"]![0]!["excerpt"]!);

        // another model: a second index builds beside the first; the page shows both while it builds and the switch at the end
        await Bind("fake-embed-2");
        var switched = await State();
        Assert.Equal(("emb/fake-embed-2", 24), ((string)switched["vectors"]!["readyModel"]!, (int)switched["vectors"]!["readyDimension"]!));
        Assert.Null(switched["vectors"]!["buildingModel"]);
        Assert.Equal("project:revenue.md", (string)(await Call(HttpMethod.Post, "/api/knowledge/search", admin, new SearchBody("what is our turnover", "vectors"))).Body!["hits"]![0]!["itemId"]!);

        // a model that refuses a chunk: the failure is listed with the reason and the attempts, the others are embedded, and a chunk given up on does not hold the switch back; retry tries it again
        File.WriteAllText(Path.Combine(directory, "knowledge", "bad.md"), "---\nknowledge: 1\ntitle: Bad\nrole: reference\nowner: sales\nabout:\n  - install: true\n---\nThis text mentions xylophone, which the model refuses.\n");
        await Bind("refusing");
        var refusing = await State();
        Assert.Equal("emb/refusing", (string)refusing["vectors"]!["readyModel"]!);
        Assert.Equal(3, (int)refusing["items"]!);
        Assert.Equal(1, (int)refusing["vectors"]!["failed"]!);
        Assert.Equal("project:bad.md", (string)refusing["failures"]![0]!["itemId"]!);
        Assert.Contains("too long", (string)refusing["failures"]![0]!["message"]!);
        Assert.Equal(3, (int)refusing["failures"]![0]!["attempts"]!);
        Assert.Equal((int)refusing["chunks"]! - 1, (int)refusing["vectors"]!["embedded"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/knowledge/index/retry", viewer)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/knowledge/index/retry", admin)).Status);
        var again = await State();
        Assert.Equal(3, (int)again["failures"]![0]!["attempts"]!);

        // a rebuild makes the vectors again from the chunks; words are untouched
        var embeddedBeforeRebuild = embedder.Embedded.Count;
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/knowledge/index/rebuild", admin)).Status);
        var rebuilt = await State();
        Assert.Equal((int)rebuilt["chunks"]! - 1, (int)rebuilt["vectors"]!["embedded"]!);
        Assert.True(embedder.Embedded.Count > embeddedBeforeRebuild);
    }
}
