using System.Globalization;
using DbDataKit.Config.Yaml;
using DbDataKit.Secrets;

namespace DbDataKit.Agents;

/// <summary>Which wire a provider speaks: Anthropic's Messages API, or the chat completions API that OpenAI, Microsoft Foundry, Ollama and most local servers share.</summary>
public enum ModelWire
{
    Anthropic,
    OpenAi,
}

/// <summary>One model a provider allows: its id as the provider names it, its window and largest answer, and its price per million tokens (null when not priced, such as a local model).</summary>
public sealed record ModelEntry(string Id, int WindowTokens, int MaxOutputTokens, ModelPrice? Price, int Line, int Column);

/// <summary>
/// A model provider, <c>model-provider/1</c> (plan 27a AG05, plan 27 decision 15): an endpoint, the reference of the key (never the key), the models it allows with their prices, whether it is a cloud or a local one and what it is for.
/// It is the model half of the connections of plan 27a IX01: until that slice gives the <c>connection</c> format a <c>kind</c>, a provider is a file of its own with the same conventions (an endpoint, secret references, a Test).
/// </summary>
public sealed record ModelProviderDefinition(string Name, string? Title, string Kind, string Endpoint, bool Cloud, string Purpose, string AuthStyle, string? ApiKeySecret, IReadOnlyList<ModelEntry> Models)
{
    public ModelWire Wire => Kind == "anthropic" ? ModelWire.Anthropic : ModelWire.OpenAi;

    public ModelEntry? Model(string id) => Models.FirstOrDefault(m => m.Id == id);
}

public sealed class ModelProviderResult(ModelProviderDefinition? definition, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public ModelProviderDefinition? Definition { get; } = definition;

    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;

    public bool Ok => Definition is not null && !Diagnostics.HasErrors();
}

/// <summary>
/// Strict reader of <c>model-provider/1</c> (plan 27a AG05). Every problem is a diagnostic with file, line and column. A key written as itself is refused <b>without echoing it</b>: the message says a key is a secret reference. Codes:
/// <c>DBD-AGT-030</c> a value, <c>-031</c> the format number.
/// </summary>
public static class ModelProviderLoader
{
    public const string BadProvider = "DBD-AGT-030";
    public const string FormatNumber = "DBD-AGT-031";
    public const int Version = 1;
    public const string Kind = "model-provider";

    public static readonly string[] Keys = ["model-provider", "name", "title", "kind", "endpoint", "locality", "purpose", "auth", "api-key-secret", "models"];

    /// <summary>The kinds of provider and the wire each speaks: the four of plan 27 section 3.7 and a local server that speaks OpenAI's wire.</summary>
    public static readonly string[] Kinds = ["anthropic", "openai", "foundry", "ollama", "openai-compatible"];

    public static readonly string[] Localities = ["cloud", "local"];

    public static readonly string[] Purposes = ["chat", "embeddings"];

    public static readonly string[] AuthStyles = ["bearer", "api-key", "none"];

    public static readonly string[] ModelKeys = ["id", "window", "max-output", "price"];

    public static readonly string[] PriceKeys = ["input", "output"];

    /// <summary>Where a kind is when no endpoint is written (a Foundry deployment and a compatible server have no default).</summary>
    public static string? DefaultEndpoint(string kind) => kind switch
    {
        "anthropic" => "https://api.anthropic.com",
        "openai" => "https://api.openai.com/v1",
        "ollama" => "http://localhost:11434/v1",
        _ => null,
    };

    private static readonly System.Text.RegularExpressions.Regex Name = new("^[a-z][a-z0-9-]{0,63}$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool IsName(string text) => Name.IsMatch(text);

    public static string Refusal(string file, int format) => format > Version
        ? $"the {Kind} definition '{file}' is in format {format}, and this program reads format {Version} of {Kind}. Upgrade this program; the file was written by a newer version."
        : $"the {Kind} definition '{file}' is in format {format}, which this program does not read (it reads format {Version} of {Kind}).";

    public static ModelProviderResult Load(string yaml, string file)
    {
        var diags = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml.TrimStart('﻿'), file, diags);
        if (root is null && !diags.HasErrors()) diags.Add(DefaultDiagnosticCatalog.Instance.Create(BadProvider, file, 1, 1, "The provider file is empty.", "Write `model-provider: 1`, a `name`, a `kind` and the models it allows."));
        if (root is null || diags.HasErrors()) return new(null, diags);
        var definition = new Reader(file, diags).Read(root);
        return new(diags.HasErrors() ? null : definition, diags);
    }

    private sealed class Reader(string fileName, List<ConfigDiagnostic> diags) : AgentFileReader(fileName, diags, BadProvider)
    {
        public ModelProviderDefinition? Read(YamlNode root)
        {
            if (root is not YamlMapping map) { Bad(root, "A model provider is a mapping with `model-provider: 1`, `name` and `kind`."); return null; }
            var format = map.Get("model-provider");
            if (format is null) { Add(FormatNumber, map, $"`model-provider` is missing: the file says which format of {Kind} it is in.", "Add `model-provider: 1`."); return null; }
            if (format is not YamlScalar { Value: var text } || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
            { Add(FormatNumber, format, "`model-provider` must be a positive whole number."); return null; }
            if (number != Version) { Add(FormatNumber, format, Refusal(File, number), number > Version ? $"Upgrade the tool, or write the file in format {Version}." : null); return null; }
            CheckKeys(map, Keys, "a model provider");

            var name = Scalar(map, "name", required: true, map);
            if (name is not null && !Name.IsMatch(name.Value)) { Bad(name, $"`{name.Value}` is not a provider name: lower-case letters, digits and '-', starting with a letter, at most 64.", "Example: `anthropic-main`."); name = null; }
            var kind = OneOf(map, "kind", Kinds, required: true);
            var endpoint = Endpoint(map, kind?.Value);
            var cloud = Locality(map, kind?.Value);
            var purpose = OneOf(map, "purpose", Purposes, required: false)?.Value ?? "chat";
            var auth = Auth(map, kind?.Value);
            var key = KeyReference(map);
            var models = Models(map);
            if (kind?.Value is "anthropic" or "openai" or "foundry" && key is null && auth != "none" && map.Get("api-key-secret") is null)
                Add(BadProvider, map, $"A {kind.Value} provider needs `api-key-secret`: the reference of the key (`secret:<name>` or `env:<NAME>`).", "Store the key with the console's secrets and write `api-key-secret: secret:anthropic-key`.");
            if (name is null || kind is null || endpoint is null || cloud is null) return null;
            return new ModelProviderDefinition(name.Value, Text(map, "title"), kind.Value, endpoint, cloud.Value, purpose, auth, key, models);
        }

        private YamlScalar? OneOf(YamlMapping map, string key, string[] allowed, bool required)
        {
            if (!required && map.Get(key) is null) return null;
            var node = Scalar(map, key, required, map);
            if (node is null) return null;
            if (allowed.Contains(node.Value)) return node;
            Bad(node, $"`{node.Value}` is not a {key}.", $"Use one of: {string.Join(", ", allowed)}.");
            return null;
        }

        private string? Text(YamlMapping map, string key) => map.Get(key) is YamlScalar { Value.Length: > 0 and <= 200 } s && !s.Value.Any(char.IsControl) ? s.Value : null;

        private string? Endpoint(YamlMapping map, string? kind)
        {
            var node = map.Get("endpoint");
            if (node is null)
            {
                if (kind is not null && DefaultEndpoint(kind) is { } known) return known;
                if (kind is not null) Add(SharedCodes.MissingKey, map, $"Required key `endpoint` is missing: a {kind} provider has no default address.", "Write the address, such as `endpoint: http://localhost:8080/v1`.");
                return null;
            }
            if (node is YamlScalar { Value: var value } && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query))
                return value.TrimEnd('/');
            Bad(node, "`endpoint` is an http or https address with no user name, password or query in it.", "A key is never part of the address: write `api-key-secret`.");
            return null;
        }

        private bool? Locality(YamlMapping map, string? kind)
        {
            if (map.Get("locality") is null)
            {
                if (kind is "ollama") return false;
                if (kind is "openai-compatible") { Add(SharedCodes.MissingKey, map, "Required key `locality` is missing: say whether this server is `cloud` or `local`.", "The install policy treats a cloud provider differently (data rules, fallback)."); return null; }
                return true;
            }
            return OneOf(map, "locality", Localities, required: true) is { Value: var v } ? v == "cloud" : null;
        }

        private string Auth(YamlMapping map, string? kind)
        {
            var given = OneOf(map, "auth", AuthStyles, required: false)?.Value;
            return given ?? kind switch { "anthropic" => "api-key", "foundry" => "api-key", "ollama" => "none", _ => "bearer" };
        }

        private string? KeyReference(YamlMapping map)
        {
            if (map.Get("api-key-secret") is not { } node) return null;
            if (node is YamlScalar { Value: var value } && SecretRef.TryParse(value, out _)) return value.Trim();
            // the message never repeats the value: a key written as itself must not reach a log or a screen
            Bad(node, "`api-key-secret` is a secret reference such as `secret:anthropic-key` or `env:ANTHROPIC_API_KEY`, never the key itself.", "Store the key with the console's secrets and write its reference here.");
            return null;
        }

        private List<ModelEntry> Models(YamlMapping map)
        {
            var models = new List<ModelEntry>();
            if (map.Get("models") is not { } node) { Add(SharedCodes.MissingKey, map, "Required key `models` is missing: a provider lists the models it allows.", "Write `models: [ { id: <the provider's model id>, window: 200000 } ]`."); return models; }
            if (node is not YamlSequence list || list.Items.Count == 0) { Bad(node, "`models` is a list of at least one model: `- { id: ..., window: ..., price: { input: 3, output: 15 } }`."); return models; }
            foreach (var item in list.Items)
            {
                if (item is not YamlMapping m) { Bad(item, "A model is a mapping with an `id`."); continue; }
                CheckKeys(m, ModelKeys, "a model");
                var id = Scalar(m, "id", required: true, m);
                if (id is not null && (id.Value.Length > 120 || id.Value.Any(char.IsWhiteSpace))) { Bad(id, "A model id is the provider's own name for it, with no spaces."); continue; }
                if (id is not null && models.Any(x => x.Id == id.Value)) { Bad(id, $"`{id.Value}` is listed more than once."); continue; }
                var window = Whole(m, "window", 1_000, 10_000_000) ?? 32_000;
                var maxOutput = Whole(m, "max-output", 16, 1_000_000) ?? Math.Min(4096, window);
                if (id is not null) models.Add(new ModelEntry(id.Value, window, maxOutput, Price(m), id.Line, id.Column));
            }
            return models;
        }

        private int? Whole(YamlMapping m, string key, int min, int max)
        {
            if (m.Get(key) is not { } n) return null;
            if (n is YamlScalar s && int.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max) return v;
            Bad(n, $"`{key}` is a whole number from {min} to {max}.");
            return null;
        }

        private ModelPrice? Price(YamlMapping m)
        {
            if (m.Get("price") is not { } node) return null;
            if (node is not YamlMapping p) { Bad(node, "`price` is `{ input: 3.00, output: 15.00 }`: amounts per million tokens."); return null; }
            CheckKeys(p, PriceKeys, "`price`");
            decimal? Amount(string key)
            {
                if (p.Get(key) is not { } n) { Add(SharedCodes.MissingKey, p, $"`price` needs `{key}`.", "Both are per million tokens."); return null; }
                if (n is YamlScalar s && decimal.TryParse(s.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v >= 0) return v;
                Bad(n, $"`{key}` is an amount of zero or more, such as `3.00`.");
                return null;
            }
            var input = Amount("input");
            var output = Amount("output");
            return input is { } i && output is { } o ? new ModelPrice(i, o) : null;
        }
    }
}
