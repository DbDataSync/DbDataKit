using DbData.Config.Testing;
using System.Text.Json.Nodes;
using DbData.Config.Yaml;

namespace DbData.Config.Tests;

public class SchemaTests
{
    [Fact]
    public void Json_schema_and_binder_agree_for_every_key()
    {
        SchemaAgreement.Assert<ProxyConfig>();
        SchemaAgreement.Assert<LooseConfig>();
    }

    [Fact]
    public void Schema_carries_constraints_secrets_deprecation_and_required()
    {
        var s = JsonSchemaGenerator.Generate<ProxyConfig>("https://example/proxy.json");
        Assert.Equal("https://example/proxy.json", s["$id"]!.GetValue<string>());
        var p = (JsonObject)s["properties"]!;
        Assert.Equal(1, long.Parse(p["timeout"]!["minimum"]!.ToJsonString()));
        Assert.Equal(3600, long.Parse(p["timeout"]!["maximum"]!.ToJsonString()));
        Assert.Equal(30, long.Parse(p["timeout"]!["default"]!.ToJsonString()));
        Assert.Equal("Seconds.", p["timeout"]!["description"]!.GetValue<string>());
        Assert.True(p["password"]!["x-secret"]!.GetValue<bool>());
        Assert.True(p["oldTimeout"]!["deprecated"]!.GetValue<bool>());
        Assert.Equal(2, long.Parse(p["schemaVersion"]!["maximum"]!.ToJsonString()));
        Assert.Equal(["x86", "x64"], ((JsonArray)p["bitness"]!["enum"]!).Select(x => x!.GetValue<string>()));
        var ds = p["dataSources"]!["additionalProperties"]!;
        Assert.Equal("provider", ds["required"]![0]!.GetValue<string>());
        Assert.Equal("^(?:[a-z]+)$", ds["properties"]!["provider"]!["pattern"]!.GetValue<string>());
        Assert.NotNull(ds["properties"]!["connect_timeout"]);
    }

    [Fact]
    public void Generated_json_is_valid_json_text()
    {
        var text = JsonSchemaGenerator.ToJson(typeof(ProxyConfig));
        Assert.NotNull(JsonNode.Parse(text));
    }

    [Fact]
    public void Preserve_policy_allows_additional_properties_in_the_schema()
    {
        Assert.True(JsonSchemaGenerator.Generate<LooseConfig>()["additionalProperties"]!.GetValue<bool>());
    }
}
