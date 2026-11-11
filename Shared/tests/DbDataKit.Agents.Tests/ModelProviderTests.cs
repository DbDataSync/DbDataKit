using DbDataKit.Agents;

namespace DbDataKit.Agents.Tests;

/// <summary>Plan 27a AG05: the strict readers of <c>model-provider/1</c> and <c>model-slots/1</c>, and the binding rules (per channel, ordered, an unbound slot named).</summary>
public sealed class ModelProviderTests
{
    private const string Good = "model-provider: 1\nname: claude\nkind: anthropic\napi-key-secret: env:ANTHROPIC_API_KEY\nmodels:\n  - { id: claude-sonnet-4-5, window: 200000, max-output: 8192, price: { input: 3, output: 15 } }\n";

    [Fact]
    public void A_complete_provider_loads_with_defaults_from_its_kind()
    {
        var p = ModelProviderLoader.Load(Good, "models/claude.yml").Definition!;

        Assert.Equal(("claude", "anthropic", "https://api.anthropic.com", true, "chat", "api-key"), (p.Name, p.Kind, p.Endpoint, p.Cloud, p.Purpose, p.AuthStyle));
        Assert.Equal(ModelWire.Anthropic, p.Wire);
        Assert.Equal(new ModelPrice(3, 15), p.Model("claude-sonnet-4-5")!.Price);
        var local = ModelProviderLoader.Load("model-provider: 1\nname: local\nkind: ollama\nmodels:\n  - { id: \"llama3.1:8b\" }\n", "l.yml").Definition!;
        Assert.Equal(("http://localhost:11434/v1", false, "none", ModelWire.OpenAi), (local.Endpoint, local.Cloud, local.AuthStyle, local.Wire));
        Assert.Null(local.Models[0].Price);
    }

    [Fact]
    public void A_key_written_as_itself_is_refused_and_the_message_never_repeats_it()
    {
        var result = ModelProviderLoader.Load(Good.Replace("env:ANTHROPIC_API_KEY", "sk-ant-api03-secret"), "models/claude.yml");

        Assert.False(result.Ok);
        var d = Assert.Single(result.Diagnostics, x => x.Code == ModelProviderLoader.BadProvider);
        Assert.Contains("secret reference", d.Message);
        Assert.DoesNotContain("sk-ant", string.Join(' ', result.Diagnostics.Select(x => x.Message + x.Fix)));
        Assert.Equal(("models/claude.yml", 4), (d.Path, d.Line));
    }

    [Theory]
    [InlineData("kind: anthropic", "kind: mystery", "is not a kind")]
    [InlineData("endpoint", "endpoint", "")]
    [InlineData("name: claude", "name: Claude Main", "not a provider name")]
    [InlineData("model-provider: 1", "model-provider: 2", "format 2")]
    [InlineData("{ id: claude-sonnet-4-5, window: 200000,", "{ id: claude-sonnet-4-5, window: 5,", "whole number from 1000")]
    public void Each_bad_part_is_refused_with_file_and_line(string from, string to, string expected)
    {
        var yaml = from == "endpoint" ? Good.Replace("kind: anthropic\n", "kind: openai-compatible\nlocality: local\n") : Good.Replace(from, to);

        var result = ModelProviderLoader.Load(yaml, "models/claude.yml");

        Assert.False(result.Ok);
        Assert.All(result.Diagnostics, d => Assert.Equal("models/claude.yml", d.Path));
        if (expected.Length > 0) Assert.Contains(result.Diagnostics, d => d.Message.Contains(expected));
        else Assert.Contains(result.Diagnostics, d => d.Message.Contains("endpoint"));      // a compatible server has no default address
    }

    [Fact]
    public void An_address_with_a_key_in_it_and_a_missing_key_for_a_cloud_kind_are_refused()
    {
        Assert.Contains(ModelProviderLoader.Load(Good.Replace("kind: anthropic\n", "kind: anthropic\nendpoint: https://u:p@api.example.com\n"), "p.yml").Diagnostics, d => d.Message.Contains("no user name"));
        Assert.Contains(ModelProviderLoader.Load(Good.Replace("api-key-secret: env:ANTHROPIC_API_KEY\n", ""), "p.yml").Diagnostics, d => d.Message.Contains("needs `api-key-secret`"));
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
    public void A_choice_of_a_provider_or_model_that_is_not_there_or_not_for_chat_is_refused_naming_the_line()
    {
        var provider = ModelProviderLoader.Load(Good, "p.yml").Definition!;
        var embeddings = ModelProviderLoader.Load(Good.Replace("name: claude", "name: embed").Replace("kind: anthropic", "kind: ollama\npurpose: embeddings"), "e.yml").Definition!;
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
