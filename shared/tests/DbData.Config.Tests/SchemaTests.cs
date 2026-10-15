using System.Text.Json.Nodes;
using DbData.Config.Yaml;

namespace DbData.Config.Tests;

public class SchemaTests
{
    /// <summary>The binder/schema agreement check: every key the binder accepts is in the JSON Schema and vice versa, recursively.</summary>
    private static void Agree(TypeSchema model, JsonObject json, string where, bool top)
    {
        switch (model)
        {
            case ObjectSchema o:
            {
                var props = ((JsonObject)json["properties"]!).Select(p => p.Key).Order().ToList();
                Assert.Equal(o.AllowedKeys(top).Order(), props);
                foreach (var p in o.Properties) Agree(p.Type, (JsonObject)json["properties"]![p.Key]!, where + "." + p.Key, false);
                Assert.Equal(o.UnknownKeys == UnknownKeyPolicy.Preserve, json["additionalProperties"]!.GetValue<bool>());
                break;
            }
            case ListSchema l: Agree(l.Item, (JsonObject)json["items"]!, where + "[]", false); break;
            case MapSchema m: Agree(m.Value, (JsonObject)json["additionalProperties"]!, where + ".*", false); break;
            case UnionSchema u:
            {
                var one = (JsonArray)json["oneOf"]!;
                Assert.Equal(u.Cases.Count, one.Count);
                foreach (var (name, c) in u.Cases)
                {
                    var j = (JsonObject)one.Single(x => x!["properties"]![u.Discriminator]!["const"]!.GetValue<string>() == name)!;
                    var jp = ((JsonObject)j["properties"]!).Select(p => p.Key).Where(k => k != u.Discriminator).Order();
                    Assert.Equal(c.AllowedKeys(false).Order(), jp);
                }
                break;
            }
        }
    }

    [Fact]
    public void Json_schema_and_binder_agree_for_every_key()
    {
        Agree(SchemaModel.For(typeof(ProxyConfig)), JsonSchemaGenerator.Generate<ProxyConfig>(), "", true);
        Agree(SchemaModel.For(typeof(LooseConfig)), JsonSchemaGenerator.Generate<LooseConfig>(), "", true);
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
