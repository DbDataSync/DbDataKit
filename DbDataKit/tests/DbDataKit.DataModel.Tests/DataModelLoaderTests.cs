using DbDataKit.Config.Yaml;
using DbDataKit.Dialect;

namespace DbDataKit.DataModel.Tests;

public class DataModelLoaderTests
{
    private const string Plan = """
        schemaVersion: 1
        name: sales_territories
        title: Sales territories
        storage: {connection: mdm_sql}
        approval: {changes: steward}
        entities:
          territory:
            key: [territory_code]
            attributes:
              territory_code: {type: VARCHAR(10), required: true, pattern: "^[A-Z]{2,4}-[0-9]{2}$"}
              name:           {type: VARCHAR(100), required: true, unique: true}
              region:         {type: VARCHAR(40), references: {model: crm_reference, entity: region}}
              active:         {type: BOOLEAN, default: true}
            history: effective_dated
          territory_share:
            key: [territory_code, region]
            attributes:
              territory_code: {type: VARCHAR(10), references: {entity: territory}}
              region:         {type: VARCHAR(40)}
              share:          {type: "DECIMAL(5,4)", min: 0, max: 1}
            rules:
              - {kind: sum_equals, attribute: share, group_by: [region], value: 1, tolerance: 0.0001}
        """;

    private static DataModelLoadResult Load(string yaml) => DataModelLoader.Load(yaml, "sales.model.yaml");

    private static ConfigDiagnostic Single(DataModelLoadResult result)
    {
        Assert.False(result.Ok);
        Assert.Null(result.Model);
        return Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void The_example_of_plan_15_is_a_valid_model()
    {
        var result = Load(Plan);

        Assert.True(result.Ok, string.Join("\n", result.Diagnostics));
        var model = result.Model!;
        Assert.Equal(("sales_territories", "mdm_sql", ApprovalLevel.Steward), (model.Name, model.Connection, model.Approval));
        var territory = model.Entity("territory")!;
        Assert.Equal(HistoryMode.EffectiveDated, territory.History);
        Assert.Equal(CanonicalType.String(10), territory.Attribute("territory_code")!.Canonical);
        Assert.Equal(CanonicalType.Decimal(5, 4), model.Entity("territory_share")!.Attribute("share")!.Canonical);
        Assert.Equal("crm_reference", territory.Attribute("region")!.References!.Model);
        Assert.Equal("true", territory.Attribute("active")!.Default);
        var rule = Assert.Single(model.Entity("territory_share")!.Rules!);
        Assert.Equal((RuleKind.SumEquals, "share", 1m, 0.0001m), (rule.Kind, rule.Attribute, rule.Value, rule.Tolerance));
        Assert.Equal(["region"], rule.GroupBy);
    }

    [Fact]
    public void Key_attributes_are_required_whether_or_not_the_file_says_so()
    {
        var model = Load(Plan).Model!;

        Assert.All(model.Entity("territory_share")!.Attributes.Where(a => a.Name != "share"), a => Assert.True(a.Required));
        Assert.False(model.Entity("territory_share")!.Attribute("share")!.Required);
    }

    [Fact]
    public void A_newer_version_is_refused_naming_the_file_and_a_missing_one_is_refused_too()
    {
        var newer = Single(Load("schemaVersion: 2\nname: m\nstorage: {connection: c}\nentities: {}\n"));
        Assert.Equal((SharedCodes.SchemaVersionNewer, "sales.model.yaml", 1), (newer.Code, newer.Path, newer.Line));

        var missing = Single(Load("name: m\nstorage: {connection: c}\n"));
        Assert.Equal(SharedCodes.SchemaVersionInvalid, missing.Code);
        Assert.Equal(SharedCodes.SchemaVersionInvalid, Single(Load("schemaVersion: one\nname: m\n")).Code);
    }

    [Fact]
    public void An_unknown_key_is_refused_with_what_was_probably_meant_and_where()
    {
        var problem = Single(Load(Plan.Replace("history: effective_dated", "histroy: effective_dated")));

        Assert.Equal(SharedCodes.UnknownKey, problem.Code);
        Assert.Contains("histroy", problem.Message);
        Assert.Contains("Did you mean `history`", problem.Message);
        Assert.True(problem.Line > 0);
    }

    [Theory]
    [InlineData("VARCHAR(40", "closing parenthesis")]
    [InlineData("DECIMAL", "precision and a scale")]
    [InlineData("MONEYS", "not a type")]
    [InlineData("VARCHAR(x)", "whole numbers")]
    public void A_type_the_models_do_not_know_is_refused_and_says_what_to_write(string type, string said)
    {
        var problem = Single(Load(Plan.Replace("VARCHAR(100)", type)));

        Assert.Equal(DataModelLoader.BadType, problem.Code);
        Assert.Contains(said, problem.Message);
    }

    [Fact]
    public void Canonical_type_text_is_accepted_beside_sql_names()
    {
        var model = Load(Plan.Replace("type: VARCHAR(100)", "type: \"string(unicode,100)\"")).Model!;

        Assert.Equal(CanonicalType.Parse("string(unicode,100)"), model.Entity("territory")!.Attribute("name")!.Canonical);
    }

    [Fact]
    public void A_key_that_names_an_attribute_the_entity_does_not_have_is_refused()
    {
        var problem = Single(Load(Plan.Replace("key: [territory_code]", "key: [code]")));

        Assert.Equal(DataModelLoader.BadKey, problem.Code);
        Assert.Contains("`code`", problem.Message);
    }

    [Fact]
    public void A_reference_within_the_model_needs_an_entity_that_exists_and_a_key_to_point_at()
    {
        Assert.Equal(DataModelLoader.BadReference, Single(Load(Plan.Replace("references: {entity: territory}", "references: {entity: territori}"))).Code);

        var wholeKey = Single(Load(Plan.Replace("references: {entity: territory}", "references: {entity: territory_share}")));
        Assert.Contains("name the one to point at", wholeKey.Message);

        var notUnique = Single(Load(Plan.Replace("references: {entity: territory}", "references: {entity: territory, attribute: active}")));
        Assert.Contains("neither the key nor unique", notUnique.Message);

        Assert.True(Load(Plan.Replace("references: {entity: territory}", "references: {entity: territory, attribute: name}")).Ok);
    }

    [Fact]
    public void Constraints_that_cannot_hold_are_refused_where_they_are_written()
    {
        var pattern = Single(Load(Plan.Replace("^[A-Z]{2,4}-[0-9]{2}$", "[A-")));
        Assert.Equal(DataModelLoader.BadConstraint, pattern.Code);
        Assert.Contains("regular expression", pattern.Message);

        Assert.Contains("above `max`", Single(Load(Plan.Replace("min: 0, max: 1", "min: 2, max: 1"))).Message);
        Assert.Contains("cannot be held", Single(Load(Plan.Replace("default: true", "default: maybe"))).Message);
        Assert.Contains("applies to text", Single(Load(Plan.Replace("{type: BOOLEAN, default: true}", "{type: BOOLEAN, pattern: x}"))).Message);
    }

    [Fact]
    public void Rules_are_a_closed_set_and_their_attributes_must_exist()
    {
        var kind = Single(Load(Plan.Replace("kind: sum_equals", "kind: sum_all")));
        Assert.Equal(DataModelLoader.BadRule, kind.Code);
        Assert.Contains("sum_equals", kind.Fix);

        Assert.Contains("not an attribute of `territory_share`", Single(Load(Plan.Replace("attribute: share,", "attribute: shares,"))).Message);
        Assert.Contains("needs a numeric `value`", Single(Load(Plan.Replace("value: 1, ", ""))).Message);
    }

    [Fact]
    public void Choices_are_checked_and_the_message_lists_them()
    {
        var approval = Single(Load(Plan.Replace("changes: steward", "changes: everyone")));
        Assert.Equal(SharedCodes.InvalidValue, approval.Code);
        Assert.Contains("none, steward, owner", approval.Fix);

        Assert.Contains("audit", Single(Load(Plan.Replace("history: effective_dated", "history: forever"))).Fix);
    }

    [Fact]
    public void Names_are_plain_and_never_repeated()
    {
        Assert.Equal(DataModelLoader.BadName, Single(Load(Plan.Replace("sales_territories", "sales territories"))).Code);
        Assert.Equal(DataModelLoader.BadName, Single(Load(Plan.Replace("  territory_share:", "  \"territory-share\":"))).Code);
    }

    [Fact]
    public void A_file_that_is_not_yaml_a_list_or_empty_fails_without_throwing()
    {
        Assert.False(Load("a: [b\n").Ok);
        Assert.False(Load("- a\n- b\n").Ok);
        Assert.False(Load("").Ok);
        Assert.False(Load("schemaVersion: 1\nname: m\nstorage: {connection: c}\nentities:\n  a: [1]\n").Ok);
    }

    [Fact]
    public void Roles_an_entity_can_play_for_the_semantic_layer_are_read()
    {
        var model = Load("""
            schemaVersion: 1
            name: refs
            storage: {connection: c}
            entities:
              day:
                role: calendar
                key: [d]
                attributes:
                  d: {type: DATE}
              region:
                role: dimension
                key: [code]
                attributes:
                  code: {type: VARCHAR(4)}
                  parent: {type: VARCHAR(4)}
                rules:
                  - {kind: no_cycle, attribute: parent}
            """).Model!;

        Assert.Equal(EntityRole.Calendar, model.Entity("day")!.Role);
        Assert.Equal(RuleKind.NoCycle, model.Entity("region")!.Rules![0].Kind);
    }
}
