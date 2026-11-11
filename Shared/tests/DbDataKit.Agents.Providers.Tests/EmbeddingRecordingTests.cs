using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Agents;
using DbDataKit.Agents.Providers;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Tests.Providers;

/// <summary>
/// Plan 27a KN05: the embedding adapters against recorded responses (OpenAI's embeddings API, Microsoft Foundry's, Ollama's own embed API; a compatible local server speaks OpenAI's wire), written by hand from the documented shapes like the chat recordings
/// (<c>fixtures/README.md</c>). Nothing here opens a socket. Test embeds a sample, reports the number of dimensions and tells a server that is down, a refused key and a model that is not there in words; the key is never in a message.
/// </summary>
public sealed class EmbeddingRecordingTests
{
    private const string Canary = "sk-canary-0123456789-do-not-log";

    private sealed class Recorded : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _answers = new();

        public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];

        public bool Unreachable { get; set; }

        public Recorded Answer(string body, HttpStatusCode status = HttpStatusCode.OK) { _answers.Enqueue((status, body)); return this; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (Unreachable) throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
            Sent.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellation)));
            var (status, body) = _answers.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static string Fixture(string folder, string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", folder, file));

    private static ModelProviderDefinition Provider(string kind, string endpoint, string model, bool key, string purpose = "embeddings", string name = "emb") => ModelProviderConnection.Load(
        $"schema: connection/2\nkind: model-provider\nname: {name}\nendpoint: {endpoint}\n{(key ? "sign_in: { method: api_key, key_secret: env:TEST_KEY }\n" : "")}model_provider:\n  provider: {kind}\n  purpose: {purpose}\n{(kind == "openai-compatible" ? "  locality: local\n" : "")}  models:\n    - {{ id: {model}, window: 8192 }}\n", "p.yml").Definition!;

    private static IEmbeddingGenerator<string, Embedding<float>> Generator(ModelProviderDefinition p, Recorded handler, string? key = null) => EmbeddingClients.Create(p, p.Models[0], key, new HttpClient(handler));

    [Fact]
    public async Task OpenAI_embeddings_come_back_in_the_order_of_the_inputs_with_the_right_address_key_and_model()
    {
        var p = Provider("openai", "https://api.openai.com/v1", "text-embedding-3-small", key: true);
        var handler = new Recorded().Answer(Fixture("openai", "embeddings.json"));

        var result = await Generator(p, handler, Canary).GenerateAsync(["first", "second"]);

        Assert.Equal(2, result.Count);
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f], result[0].Vector.ToArray());                        // the answer lists index 1 before index 0: the order is the inputs'
        Assert.Equal([0.5f, 0.5f, -0.5f, 0.5f], result[1].Vector.ToArray());
        Assert.Equal(12, result.Usage!.InputTokenCount);
        var (request, body) = handler.Sent.Single();
        Assert.Equal("/v1/embeddings", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer " + Canary, request.Headers.Authorization!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("text-embedding-3-small", (string?)json["model"]);
        Assert.Equal(["first", "second"], json["input"]!.AsArray().Select(n => (string?)n));
        Assert.DoesNotContain(Canary, body);
    }

    [Fact]
    public async Task Foundry_takes_the_key_in_the_api_key_header_and_a_compatible_local_server_takes_none()
    {
        var foundry = Provider("foundry", "https://acme.openai.azure.com/openai/v1", "text-embedding-3-small", key: true);
        var local = Provider("openai-compatible", "http://localhost:8080/v1", "bge-small", key: false);
        var a = new Recorded().Answer(Fixture("foundry", "embeddings-1.json"));
        var b = new Recorded().Answer(Fixture("openai", "embeddings-1.json"));

        await Generator(foundry, a, Canary).GenerateAsync(["x"]);
        await Generator(local, b).GenerateAsync(["x"]);

        Assert.Equal(Canary, a.Sent.Single().Request.Headers.GetValues("api-key").Single());
        Assert.Equal("/openai/v1/embeddings", a.Sent.Single().Request.RequestUri!.AbsolutePath);
        Assert.Null(b.Sent.Single().Request.Headers.Authorization);
        Assert.Equal("/v1/embeddings", b.Sent.Single().Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Ollama_is_asked_at_its_own_embed_address_even_when_the_connection_names_the_v1_chat_address()
    {
        var p = Provider("ollama", "http://localhost:11434/v1", "nomic-embed-text", key: false);
        var handler = new Recorded().Answer(Fixture("ollama", "embed.json"));

        var result = await Generator(p, handler).GenerateAsync(["a", "b"]);

        Assert.Equal([[0.1f, 0.2f, 0.3f, 0.4f], [0.4f, 0.3f, 0.2f, 0.1f]], result.Select(e => e.Vector.ToArray()));
        Assert.Equal(17, result.Usage!.InputTokenCount);
        var (request, body) = handler.Sent.Single();
        Assert.Equal("http://localhost:11434/api/embed", request.RequestUri!.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.Equal("nomic-embed-text", (string?)JsonNode.Parse(body)!["model"]);
        Assert.Equal(["a", "b"], JsonNode.Parse(body)!["input"]!.AsArray().Select(n => (string?)n));
    }

    [Theory]
    [InlineData("openai", "https://api.openai.com/v1", "openai", "embeddings-1.json", 6, true)]
    [InlineData("foundry", "https://acme.openai.azure.com/openai/v1", "foundry", "embeddings-1.json", 6, true)]
    [InlineData("ollama", "http://localhost:11434/v1", "ollama", "embed-1.json", 8, false)]
    public async Task Test_embeds_a_sample_and_reports_the_number_of_dimensions(string kind, string endpoint, string folder, string file, int dimensions, bool key)
    {
        var p = Provider(kind, endpoint, "an-embedding-model", key);
        var handler = new Recorded().Answer(Fixture(folder, file));

        var result = await EmbeddingClients.TestAsync(p, p.Models[0], key ? Canary : null, new HttpClient(handler));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(dimensions, result.Dimensions);
        Assert.Contains($"{dimensions} dimensions", result.Message);
        Assert.DoesNotContain(Canary, result.Message);
        Assert.Contains(EmbeddingClients.Sample, handler.Sent.Single().Body);
    }

    [Fact]
    public async Task Test_says_in_words_that_the_server_is_down_and_that_it_is_worth_trying_again()
    {
        var p = Provider("ollama", "http://localhost:11434/v1", "nomic-embed-text", key: false);

        var result = await EmbeddingClients.TestAsync(p, p.Models[0], null, new HttpClient(new Recorded { Unreachable = true }));

        Assert.False(result.Ok);
        Assert.True(result.Temporary);
        Assert.Null(result.Dimensions);
        Assert.Contains("could not be reached", result.Message);
        Assert.Contains("emb", result.Message);
    }

    [Fact]
    public async Task Test_tells_a_model_that_is_not_pulled_a_refused_key_and_a_busy_server_apart_and_never_shows_the_key()
    {
        var ollama = Provider("ollama", "http://localhost:11434/v1", "nomic-embed-text", key: false);
        var missing = await EmbeddingClients.TestAsync(ollama, ollama.Models[0], null, new HttpClient(new Recorded().Answer(Fixture("ollama", "embed-404.json"), HttpStatusCode.NotFound)));
        Assert.False(missing.Ok);
        Assert.False(missing.Temporary);
        Assert.Contains("ollama pull nomic-embed-text", missing.Message);

        var openai = Provider("openai", "https://api.openai.com/v1", "text-embedding-3-small", key: true);
        var refused = await EmbeddingClients.TestAsync(openai, openai.Models[0], Canary, new HttpClient(new Recorded().Answer(Fixture("openai", "error-401.json"), HttpStatusCode.Unauthorized)));
        Assert.False(refused.Ok);
        Assert.False(refused.Temporary);
        Assert.Contains("did not accept the key", refused.Message);
        Assert.DoesNotContain(Canary, refused.Message);

        var busy = await EmbeddingClients.TestAsync(openai, openai.Models[0], Canary, new HttpClient(new Recorded().Answer(Fixture("openai", "error-429.json"), HttpStatusCode.TooManyRequests)));
        Assert.True(busy.Temporary);

        var wrong = await EmbeddingClients.TestAsync(openai, openai.Models[0], Canary, new HttpClient(new Recorded().Answer("""{ "object": "list", "data": [] }""")));
        Assert.False(wrong.Ok);
        Assert.Contains("no embeddings", wrong.Message);
    }

    [Fact]
    public void A_chat_provider_and_an_anthropic_provider_are_refused_for_embeddings_in_words()
    {
        var chat = Provider("openai", "https://api.openai.com/v1", "gpt-4.1", key: true, purpose: "chat", name: "chatty");
        var anthropic = Provider("anthropic", "https://api.anthropic.com", "claude-sonnet-4-5", key: true, purpose: "embeddings", name: "claude");

        var a = Assert.Throws<ModelProviderException>(() => EmbeddingClients.Create(chat, chat.Models[0], Canary, new HttpClient()));
        var b = Assert.Throws<ModelProviderException>(() => EmbeddingClients.Create(anthropic, anthropic.Models[0], Canary, new HttpClient()));

        Assert.Contains("for chat, not for embeddings", a.Message);
        Assert.Contains("Anthropic has no embeddings API", b.Message);
        Assert.False(a.Temporary || b.Temporary);
    }
}
