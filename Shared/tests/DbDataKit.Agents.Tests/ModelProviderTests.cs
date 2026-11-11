using DbDataKit.Agents;
using DbDataKit.Connections;

namespace DbDataKit.Agents.Tests;

/// <summary>Plan 27a AG05: a model provider read from a connection of kind <c>model-provider</c> (IX01), the strict reader of <c>model-slots/1</c>, and the binding rules (per channel, ordered, an unbound slot named).</summary>
public sealed class ModelProviderTests
{
    private const string Good = "schema: connection/2\nkind: model-provider\nname: claude\nsign_in: { method: api_key, key_secret: env:ANTHROPIC_API_KEY }\nmodel_provider:\n  provider: anthropic\n  models:\n    - { id: claude-sonnet-4-5, window: 200000, max_output: 8192, price: { input: 3, output: 15 } }\n";

    [Fact]
    public void A_complete_provider_loads_with_defaults_from_its_provider()
    {
        var p = ModelProviderConnection.Load(Good, "connections/claude.yml").Definition!;

        Assert.Equal(("claude", "anthropic", "https://api.anthropic.com", true, "chat", "api-key"), (p.Name, p.Kind, p.Endpoint, p.Cloud, p.Purpose, p.AuthStyle));
        Assert.Equal("env:ANTHROPIC_API_KEY", p.ApiKeySecret);
        Assert.Equal(ModelWire.Anthropic, p.Wire);
        Assert.Equal(new ModelPrice(3, 15), p.Model("claude-sonnet-4-5")!.Price);
        Assert.Equal((200000, 8192), (p.Models[0].WindowTokens, p.Models[0].MaxOutputTokens));
        var local = ModelProviderConnection.Load("schema: connection/2\nkind: model-provider\nname: local\nmodel_provider:\n  provider: ollama\n  models:\n    - { id: \"llama3.1:8b\" }\n", "l.yml").Definition!;
        Assert.Equal(("http://localhost:11434/v1", false, "none", ModelWire.OpenAi), (local.Endpoint, local.Cloud, local.AuthStyle, local.Wire));
        Assert.Null(local.Models[0].Price);
        Assert.Equal((32000, 4096), (local.Models[0].WindowTokens, local.Models[0].MaxOutputTokens));   // a window left out is the default
    }

    [Fact]
    public void A_compatible_server_with_a_bearer_key_a_foundry_deployment_and_an_embeddings_provider_read()
    {
        var compatible = ModelProviderConnection.Load("schema: connection/2\nkind: model-provider\nname: vllm\nendpoint: http://gpu1:8000/v1/\nsign_in: { method: api_key, key_secret: env:K }\nmodel_provider: { provider: openai-compatible, locality: local, models: [ { id: m } ] }\n", "v.yml").Definition!;
        Assert.Equal(("http://gpu1:8000/v1", false, "bearer"), (compatible.Endpoint, compatible.Cloud, compatible.AuthStyle));
        var foundry = ModelProviderConnection.Load("schema: connection/2\nkind: model-provider\nname: f\nendpoint: https://r.openai.azure.com/openai/v1\nsign_in: { method: api_key, key_secret: env:K }\nmodel_provider: { provider: foundry, models: [ { id: gpt } ] }\n", "f.yml").Definition!;
        Assert.Equal("api-key", foundry.AuthStyle);
        var embeddings = ModelProviderConnection.Load(Good.Replace("name: claude", "name: embed").Replace("provider: anthropic", "provider: ollama\n  purpose: embeddings").Replace("sign_in: { method: api_key, key_secret: env:ANTHROPIC_API_KEY }\n", ""), "e.yml").Definition!;
        Assert.Equal("embeddings", embeddings.Purpose);
    }

    [Fact]
    public void A_key_written_as_itself_is_refused_and_the_message_never_repeats_it()
    {
        var result = ModelProviderConnection.Load(Good.Replace("env:ANTHROPIC_API_KEY", "sk-ant-api03-secret"), "connections/claude.yml");

        Assert.False(result.Ok);
        var d = Assert.Single(result.Diagnostics, x => x.Code == ConnectionDefinitionLoader.BadSecretRef);
        Assert.Contains("secret reference", d.Message);
        Assert.DoesNotContain("sk-ant", string.Join(' ', result.Diagnostics.Select(x => x.Message + x.Fix)));
        Assert.Equal(("connections/claude.yml", 4), (d.Path, d.Line));
    }

    [Theory]
    [InlineData("provider: anthropic", "provider: mystery", "provider")]
    [InlineData("endpoint", "endpoint", "")]
    [InlineData("schema: connection/2", "schema: connection/3", "connection")]
    [InlineData("{ id: claude-sonnet-4-5, window: 200000,", "{ id: claude-sonnet-4-5, window: 5,", "window")]
    [InlineData("kind: model-provider", "kind: http", "belongs to the kind")]
    public void Each_bad_part_is_refused_with_file_and_line(string from, string to, string expected)
    {
        var yaml = from == "endpoint" ? Good.Replace("provider: anthropic\n", "provider: openai-compatible\n  locality: local\n") : Good.Replace(from, to);

        var result = ModelProviderConnection.Load(yaml, "connections/claude.yml");

        Assert.False(result.Ok);
        Assert.All(result.Diagnostics, d => Assert.Equal("connections/claude.yml", d.Path));
        if (expected.Length > 0) Assert.Contains(result.Diagnostics, d => d.Message.Contains(expected));
        else Assert.Contains(result.Diagnostics, d => d.Message.Contains("endpoint"));      // a compatible server has no default address
    }

    [Fact]
    public void An_address_with_a_key_in_it_and_a_missing_key_for_a_cloud_provider_are_refused()
    {
        Assert.Contains(ModelProviderConnection.Load(Good.Replace("name: claude\n", "name: claude\nendpoint: https://u:p@api.example.com\n"), "p.yml").Diagnostics, d => d.Message.Contains("no user name"));
        Assert.Contains(ModelProviderConnection.Load(Good.Replace("sign_in: { method: api_key, key_secret: env:ANTHROPIC_API_KEY }\n", ""), "p.yml").Diagnostics, d => d.Message.Contains("needs `sign_in"));
    }

    [Fact]
    public void A_connection_of_another_kind_is_not_a_provider()
    {
        var result = ModelProviderConnection.Load("schema: connection/2\nkind: http\nname: crm\nendpoint: https://api.example.com/v1\n", "connections/crm.yml");
        Assert.False(result.Ok);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("not `model-provider`") && d.Path == "connections/crm.yml");
    }

    [Fact]
    public void Slots_bind_per_channel_in_order_and_a_channel_without_its_own_has_the_one_for_every_channel()
    {
        var yaml = "model-slots: 1\nslots:\n  default:\n    \"*\":\n      - { provider: claude, model: claude-sonnet-4-5 }\n      - { provider: local, model: llama }\n    test:\n      - { provider: local, model: llama }\n";

        var bindings = SlotBindingsLoader.Load(yaml, "models/_slots.yml").Bindings!;

        Assert.Equal(["claude", "local"], bindings.For("default", null).Select(c => c.Provider));
        Assert.Equal(["claude", "local"], bindings.For("default", "prod").Select(c => c.Provider));
        Assert.Equal(["local"], bindings.For("default", "test").Select(c => c.Provider));
        Assert.Empty(bindings.For("other", null));
        Assert.Equal(["other"], bindings.Unbound(["default", "other"], null));      // a promotion that needs it is refused naming it
    }

    [Fact]
    public void The_embeddings_slot_takes_a_provider_for_embeddings_and_refuses_a_chat_one_and_the_other_way_round()
    {
        var chat = ModelProviderConnection.Load(Good, "p.yml").Definition!;
        var embeddings = ModelProviderConnection.Load(Good.Replace("name: claude", "name: embed").Replace("provider: anthropic", "provider: ollama\n  purpose: embeddings").Replace("sign_in: { method: api_key, key_secret: env:ANTHROPIC_API_KEY }\n", ""), "e.yml").Definition!;
        var providers = new Dictionary<string, ModelProviderDefinition> { ["claude"] = chat, ["embed"] = embeddings };
        var yaml = "model-slots: 1\nslots:\n  embeddings:\n    \"*\":\n      - { provider: embed, model: claude-sonnet-4-5 }\n    test:\n      - { provider: claude, model: claude-sonnet-4-5 }\n";

        var problems = SlotBindingsLoader.Check(SlotBindingsLoader.Load(yaml, "s.yml").Bindings!, providers, "s.yml");

        Assert.Equal("embeddings", SlotBindingsLoader.PurposeOf(SlotBindingsLoader.EmbeddingsSlot));
        Assert.Equal("chat", SlotBindingsLoader.PurposeOf("default"));
        var problem = Assert.Single(problems);
        Assert.Contains("for chat, not embeddings", problem.Message);
        Assert.Equal(7, problem.Line);
    }

    [Fact]
    public void A_choice_of_a_provider_or_model_that_is_not_there_or_not_for_chat_is_refused_naming_the_line()
    {
        var provider = ModelProviderConnection.Load(Good, "p.yml").Definition!;
        var embeddings = ModelProviderConnection.Load(Good.Replace("name: claude", "name: embed").Replace("provider: anthropic", "provider: ollama\n  purpose: embeddings").Replace("sign_in: { method: api_key, key_secret: env:ANTHROPIC_API_KEY }\n", ""), "e.yml").Definition!;
        var providers = new Dictionary<string, ModelProviderDefinition> { ["claude"] = provider, ["embed"] = embeddings };
        var yaml = "model-slots: 1\nslots:\n  default:\n    \"*\":\n      - { provider: nobody, model: m }\n      - { provider: claude, model: claude-9 }\n      - { provider: embed, model: claude-sonnet-4-5 }\n      - { provider: claude, model: claude-sonnet-4-5 }\n";

        var problems = SlotBindingsLoader.Check(SlotBindingsLoader.Load(yaml, "s.yml").Bindings!, providers, "s.yml");

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, d => d.Message.Contains("`nobody`, which does not exist") && d.Line == 5);
        Assert.Contains(problems, d => d.Message.Contains("`claude-9`") && d.Fix!.Contains("claude-sonnet-4-5"));
        Assert.Contains(problems, d => d.Message.Contains("for embeddings, not chat"));
    }

    [Fact]
    public void A_newer_format_is_refused_naming_the_file_and_both_numbers_and_an_empty_slots_file_binds_nothing()
    {
        var result = SlotBindingsLoader.Load("model-slots: 3\nslots: {}\n", "models/_slots.yml");
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("models/_slots.yml") && d.Message.Contains("format 3") && d.Message.Contains("format 1"));
        Assert.Empty(SlotBindingsLoader.Load("", "s.yml").Bindings!.Slots);
    }
}
