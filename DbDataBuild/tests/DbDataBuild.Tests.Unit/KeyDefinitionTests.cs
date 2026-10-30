using DbDataBuild.Core;
using DbDataBuild.Models;

namespace DbDataBuild.Tests.Unit;

public class KeyDefinitionTests
{
    private const string Head = "name: marts.dim_customer\nkind: full\ncolumns:\n  - {name: customer_key, type: BIGINT}\n";

    private static (ModelDefinition? Model, List<Diagnostic> Diags) Load(string keys)
    {
        var diags = new List<Diagnostic>();
        var model = ModelDefinitionLoader.Load(Head + keys, "models/marts/dim_customer.yml", "marts.dim_customer", diags);
        return (model, diags);
    }

    [Fact]
    public void A_key_is_read_with_its_arguments_in_order()
    {
        var (model, diags) = Load("keys:\n  customer_key: {from: [source_system, customer_id]}\n");
        Assert.Empty(diags);
        var key = Assert.Single(model!.Keys);
        Assert.Equal("customer_key", key.Name);
        Assert.Equal(["source_system", "customer_id"], key.From);
        Assert.Equal(KeyMethods.Hash, key.Method);
        Assert.False(key.UnknownMember);
    }

    [Fact]
    public void A_history_key_names_its_version_key_and_valid_from()
    {
        var (model, diags) = Load("keys:\n  product_key: {from: [product_id], history: valid_from, version_key: product_version_key, unknown_member: true}\n");
        Assert.Empty(diags);
        var key = Assert.Single(model!.Keys);
        Assert.Equal("valid_from", key.History);
        Assert.Equal("product_version_key", key.VersionKey);
        Assert.True(key.UnknownMember);
    }

    [Theory]
    [InlineData("keys:\n  k: {from: [a], colour: red}\n", "colour")]
    [InlineData("keys:\n  k: {}\n", "from")]
    [InlineData("keys:\n  k: {from: []}\n", "from")]
    [InlineData("keys:\n  Bad-Name: {from: [a]}\n", "not a valid key name")]
    [InlineData("keys:\n  k: {from: [a, a]}\n", "twice")]
    [InlineData("keys:\n  k: {from: [a], method: key_map}\n", "K10")]
    [InlineData("keys:\n  k: {from: [a], method: random}\n", "random")]
    [InlineData("keys:\n  k: {from: [a], unknown_member: maybe}\n", "maybe")]
    [InlineData("keys:\n  k: {from: [a], history: valid_from}\n", "go together")]
    [InlineData("keys:\n  k: {from: [a], version_key: kv}\n", "go together")]
    [InlineData("keys:\n  k: {from: [a, v], history: v, version_key: kv}\n", "also the history argument")]
    [InlineData("keys:\n  k: {from: [a], history: v, version_key: k}\n", "more than once")]
    [InlineData("keys: [a]\n", "must be a mapping")]
    public void A_bad_key_is_refused_with_a_message_that_says_why(string keys, string expected)
    {
        var (model, diags) = Load(keys);
        Assert.Null(model);
        Assert.Contains(diags, d => (d.Found + " " + d.Fix).Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void A_model_with_no_keys_has_none()
    {
        var (model, diags) = Load("");
        Assert.Empty(diags);
        Assert.Empty(model!.Keys);
    }
}
