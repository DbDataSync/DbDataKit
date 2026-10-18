using System.Text.Json.Nodes;
using DbDataBuild.Lowering;

namespace DbDataBuild.Tests.Unit;

/// <summary>DuckDB 2.0 restructured its serialized plan; the normalizer rewrites it into the 1.x shape the lowerer reads. The shapes below are what 2.0.0-alpha44073 prints.</summary>
public class PlanNormalizerTests
{
    private static JsonNode Norm(string json) => JsonNode.Parse(PlanNormalizer.Normalize(json))!;

    [Fact]
    public void A_plan_in_the_1x_shape_is_returned_unchanged_as_the_same_string()
    {
        const string plan = "{\"plans\":[{\"type\":\"LOGICAL_GET\",\"name\":\"seq_scan\",\"function_data\":{\"table\":\"t\"}}]}";
        Assert.Same(plan, PlanNormalizer.Normalize(plan));
    }

    [Fact]
    public void Qualified_names_become_name_schema_and_catalog()
    {
        var n = Norm("{\"a\":{\"type\":\"BOUND_AGGREGATE\",\"qname\":{\"path\":[\"system\",\"main\",\"sum\"]}},\"g\":{\"type\":\"LOGICAL_GET\",\"qname\":{\"path\":[\"seq_scan\"]}}}");
        Assert.Equal(("sum", "main", "system"), ((string)n["a"]!["name"]!, (string)n["a"]!["schema_name"]!, (string)n["a"]!["catalog_name"]!));
        Assert.Equal("seq_scan", (string)n["g"]!["name"]!);
        Assert.Null(n["g"]!["schema_name"]);
    }

    [Fact]
    public void A_comparison_function_becomes_a_comparison_with_left_and_right()
    {
        var n = Norm("{\"e\":{\"expression_class\":\"BOUND_FUNCTION\",\"type\":\"COMPARE_GREATERTHAN\",\"qname\":{\"path\":[\">\"]},\"children\":[{\"type\":\"BOUND_REF\",\"index\":0},{\"type\":\"VALUE_CONSTANT\"}]}}");
        var e = n["e"]!;
        Assert.Equal("BOUND_COMPARISON", (string)e["expression_class"]!);
        Assert.Equal("BOUND_REF", (string)e["left"]!["type"]!);
        Assert.Equal("VALUE_CONSTANT", (string)e["right"]!["type"]!);
        Assert.Null(e["children"]);
    }

    [Fact]
    public void A_cast_function_becomes_a_cast_with_its_child_and_its_try_flag()
    {
        var n = Norm("{\"e\":{\"expression_class\":\"BOUND_FUNCTION\",\"type\":\"OPERATOR_CAST\",\"qname\":{\"path\":[\"__cast\"]},\"return_type\":{\"id\":\"BIGINT\"},\"function_data\":{\"try_cast\":true},\"children\":[{\"type\":\"BOUND_REF\",\"index\":3}]}}");
        var e = n["e"]!;
        Assert.Equal(("BOUND_CAST", true, "BIGINT"), ((string)e["expression_class"]!, (bool)e["try_cast"]!, (string)e["return_type"]!["id"]!));
        Assert.Equal(3, (int)e["child"]!["index"]!);
    }

    [Fact]
    public void Not_over_in_is_not_in_as_1x_called_it()
    {
        var n = Norm("{\"qname\":{\"path\":[\"x\"]},\"e\":{\"type\":\"OPERATOR_NOT\",\"children\":[{\"type\":\"COMPARE_IN\",\"children\":[{\"type\":\"BOUND_REF\",\"index\":0},{\"type\":\"VALUE_CONSTANT\"}]}]}}");
        Assert.Equal("COMPARE_NOT_IN", (string)n["e"]!["type"]!);
        Assert.Equal(2, n["e"]!["children"]!.AsArray().Count);
        // NOT over anything else is left alone
        var plain = Norm("{\"qname\":{\"path\":[\"x\"]},\"e\":{\"type\":\"OPERATOR_NOT\",\"children\":[{\"type\":\"OPERATOR_IS_NULL\"}]}}");
        Assert.Equal("OPERATOR_NOT", (string)plain["e"]!["type"]!);
    }

    [Fact]
    public void Lag_and_lead_arguments_become_the_offset_and_default_beside_the_argument()
    {
        var n = Norm("{\"qname\":{\"path\":[\"x\"]},\"w\":{\"type\":\"WINDOW_LAG\",\"children\":[{\"type\":\"BOUND_REF\",\"index\":0},{\"type\":\"VALUE_CONSTANT\",\"alias\":\"offset\"},{\"type\":\"VALUE_CONSTANT\",\"alias\":\"default\"}]}}");
        var w = n["w"]!;
        Assert.Single(w["children"]!.AsArray());
        Assert.Equal("offset", (string)w["offset_expr"]!["alias"]!);
        Assert.Equal("default", (string)w["default_expr"]!["alias"]!);
    }

    [Fact]
    public void A_column_index_whose_type_is_an_object_is_not_mistaken_for_an_operator()
    {
        var n = Norm("{\"qname\":{\"path\":[\"x\"]},\"ci\":{\"index\":0,\"type\":{\"id\":\"INVALID\"}}}");
        Assert.Equal("INVALID", (string)n["ci"]!["type"]!["id"]!);
    }
    [Fact]
    public void Between_becomes_a_between_with_input_lower_upper_and_its_inclusive_flags()
    {
        var n = Norm("{\"e\":{\"expression_class\":\"BOUND_FUNCTION\",\"type\":\"COMPARE_BETWEEN\",\"qname\":{\"path\":[\"__between\"]},\"function_data\":{\"lower_inclusive\":true,\"upper_inclusive\":false},\"children\":[{\"type\":\"BOUND_REF\",\"index\":1},{\"type\":\"VALUE_CONSTANT\",\"alias\":\"lo\"},{\"type\":\"VALUE_CONSTANT\",\"alias\":\"hi\"}]}}");
        var e = n["e"]!;
        Assert.Equal("BOUND_BETWEEN", (string)e["expression_class"]!);
        Assert.Equal((1, "lo", "hi"), ((int)e["input"]!["index"]!, (string)e["lower"]!["alias"]!, (string)e["upper"]!["alias"]!));
        Assert.Equal((true, false), ((bool)e["lower_inclusive"]!, (bool)e["upper_inclusive"]!));
        Assert.Null(e["children"]);
    }
    [Fact]
    public void Not_over_a_comparison_is_the_opposite_comparison_as_1x_folded_it()
    {
        var n = Norm("{\"qname\":{\"path\":[\"x\"]},\"e\":{\"type\":\"OPERATOR_NOT\",\"expression_class\":\"BOUND_OPERATOR\",\"children\":[{\"type\":\"COMPARE_EQUAL\",\"expression_class\":\"BOUND_FUNCTION\",\"children\":[{\"type\":\"BOUND_REF\",\"index\":0},{\"type\":\"VALUE_CONSTANT\"}]}]}}");
        var e = n["e"]!;
        Assert.Equal(("COMPARE_NOTEQUAL", "BOUND_COMPARISON"), ((string)e["type"]!, (string)e["expression_class"]!));
        Assert.Equal("BOUND_REF", (string)e["left"]!["type"]!);
        foreach (var (from, to) in new[] { ("COMPARE_LESSTHAN", "COMPARE_GREATERTHANOREQUALTO"), ("COMPARE_GREATERTHAN", "COMPARE_LESSTHANOREQUALTO"), ("COMPARE_NOT_DISTINCT_FROM", "COMPARE_DISTINCT_FROM") })
        {
            var m = Norm("{\"qname\":{\"path\":[\"x\"]},\"e\":{\"type\":\"OPERATOR_NOT\",\"children\":[{\"type\":\"" + from + "\",\"expression_class\":\"BOUND_FUNCTION\",\"children\":[{\"type\":\"BOUND_REF\"},{\"type\":\"BOUND_REF\"}]}]}}");
            Assert.Equal(to, (string)m["e"]!["type"]!);
        }
    }

    [Fact]
    public void An_offset_of_one_and_a_null_default_are_what_1x_left_out_for_lag()
    {
        const string plain = "{\"type\":\"VALUE_CONSTANT\",\"value\":{\"type\":{\"id\":\"BIGINT\"},\"is_null\":false,\"value\":1}}";
        const string nullCast = "{\"expression_class\":\"BOUND_CAST\",\"type\":\"OPERATOR_CAST\",\"child\":{\"type\":\"VALUE_CONSTANT\",\"value\":{\"is_null\":true}}}";
        var omitted = Norm("{\"qname\":{\"path\":[\"x\"]},\"w\":{\"type\":\"WINDOW_LAG\",\"children\":[{\"type\":\"BOUND_REF\"}," + plain + "," + nullCast + "]}}");
        Assert.Null(omitted["w"]!["offset_expr"]);
        Assert.Null(omitted["w"]!["default_expr"]);
        // a real offset or default is kept
        var offset = Norm("{\"qname\":{\"path\":[\"x\"]},\"w\":{\"type\":\"WINDOW_LAG\",\"children\":[{\"type\":\"BOUND_REF\"},{\"type\":\"VALUE_CONSTANT\",\"value\":{\"is_null\":false,\"value\":2}}," + nullCast + "]}}");
        Assert.NotNull(offset["w"]!["offset_expr"]);
        Assert.Null(offset["w"]!["default_expr"]);
        var defaulted = Norm("{\"qname\":{\"path\":[\"x\"]},\"w\":{\"type\":\"WINDOW_LAG\",\"children\":[{\"type\":\"BOUND_REF\"}," + plain + ",{\"type\":\"VALUE_CONSTANT\",\"value\":{\"is_null\":false,\"value\":0}}]}}");
        Assert.NotNull(defaulted["w"]!["default_expr"]);
    }
}
