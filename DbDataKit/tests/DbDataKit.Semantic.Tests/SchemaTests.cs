using System.Text.Json.Nodes;
using DbDataKit.Semantic;
using Xunit;

namespace DbDataKit.Semantic.Tests;

/// <summary>The contract schemas must reject malformed documents, not only accept the engine's own output.</summary>
public class SchemaTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "conformance");
    private static readonly Validator V = new(
        Catalog.Load(Path.Combine(Root, "catalog", "catalog.yaml")));

    private static JsonObject Result(JsonObject step, JsonObject? descriptor = null) => new()
    {
        ["kind"] = "result", ["status"] = "partial", ["engine_level"] = 2,
        ["result_sets"] = new JsonArray(new JsonObject
        {
            ["id"] = "d1", ["grain"] = new JsonArray(), ["rows"] = new JsonArray(), ["descriptor"] = descriptor ?? Descriptor(),
        }),
        ["remaining"] = new JsonArray(step),
    };

    private static JsonObject Descriptor(string type = "number", string role = "measure") => new()
    {
        ["descriptor_version"] = "1.0", ["name"] = "d1",
        ["schema"] = new JsonObject
        {
            ["fields"] = new JsonArray(new JsonObject { ["name"] = "x", ["type"] = type, ["x_kind"] = role }),
        },
    };

    private static JsonObject Step(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void WellFormedResultIsValid() =>
        Assert.Empty(V.SchemaErrors(Result(Step("""{"step": 1, "op": "totals", "subtotal_by": ["customers.region"]}""")), "result", "core"));

    [Theory]
    [InlineData("""{"step": 1, "op": "spread", "input": "d2"}""")]                                  // no 'to' or 'spread'
    [InlineData("""{"step": 1, "op": "spread", "input": "d2", "to": ["a.b"], "spread": {"m.x": "smear"}}""")]
    [InlineData("""{"step": 1, "op": "union", "inputs": ["d1"]}""")]                                // needs two inputs
    [InlineData("""{"step": 1, "op": "totals"}""")]                                                  // no rollup
    [InlineData("""{"step": 1, "op": "regrain"}""")]                                                 // retired name
    public void MalformedStepsAreRejected(string step) =>
        Assert.NotEmpty(V.SchemaErrors(Result(Step(step)), "result", "core"));

    [Theory]
    [InlineData("string", "measure")]     // a measure must be numeric
    [InlineData("decimal", "attribute")]  // not a core type
    public void MalformedDescriptorsAreRejected(string type, string role) =>
        Assert.NotEmpty(V.SchemaErrors(Result(Step("""{"step": 1, "op": "finalize"}"""), Descriptor(type, role)), "result", "core"));
}
