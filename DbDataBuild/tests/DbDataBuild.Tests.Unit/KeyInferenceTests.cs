using DbDataBuild.Cli;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>Keys defined in pure SQL (plan 25 K12): the call `surrogate_key(...) AS name` is the definition, stored in the model's `keys:`.</summary>
public class KeyInferenceTests
{
    [Fact]
    public void A_call_selected_as_a_name_defines_a_key_from_its_argument_columns()
    {
        var (keys, problems) = KeyInference.Infer("SELECT surrogate_key(c.source_system, \"c\".\"customer_id\") AS customer_key, c.name FROM staging.customers c");

        Assert.Empty(problems);
        var key = Assert.Single(keys);
        Assert.Equal("customer_key", key.Name);
        Assert.Equal(["source_system", "customer_id"], key.From);
    }

    [Fact]
    public void Text_in_strings_and_comments_and_other_functions_are_not_calls()
    {
        var sql = "SELECT 'surrogate_key(a) AS x' AS s, -- surrogate_key(b) AS y\n my.surrogate_key(c) AS z, /* surrogate_key(d) AS w */ not_surrogate_key(e) AS v FROM t";
        Assert.Empty(KeyInference.Find(sql));
    }

    [Theory]
    [InlineData("SELECT surrogate_key(a.x) FROM t a", "must be selected `AS <name>`")]
    [InlineData("SELECT surrogate_key(upper(a.x)) AS k FROM t a", "is an expression")]
    [InlineData("SELECT surrogate_key(a.x, b.x) AS k FROM t a, u b", "same column name")]
    [InlineData("SELECT surrogate_key() AS k FROM t", "at least one argument")]
    [InlineData("SELECT surrogate_key(a.x) AS \"Bad Name\" FROM t a", "not a valid key name")]
    public void A_call_that_cannot_be_read_as_a_key_is_a_problem_naming_the_line(string sql, string part)
    {
        var (keys, problems) = KeyInference.Infer("\n" + sql);
        Assert.Empty(keys);
        var (line, text) = Assert.Single(problems);
        Assert.Equal(2, line);
        Assert.Contains(part, text);
    }

    [Fact]
    public void A_stored_key_makes_the_call_a_call_of_the_keys_own_function_and_only_its_name_changes()
    {
        var keys = new[] { new KeyDefinition("customer_key", ["source_system", "customer_id"]) };
        var sql = "SELECT surrogate_key(c.source_system, c.customer_id) AS customer_key, surrogate_key(c.x) AS other FROM c";

        Assert.Equal("SELECT customer_key(c.source_system, c.customer_id) AS customer_key, surrogate_key(c.x) AS other FROM c", KeyInference.Rewrite(sql, keys));
    }

    [Fact]
    public void Keys_are_added_to_a_file_that_has_none_and_to_one_that_has_some_and_a_stored_key_is_left_alone()
    {
        var inferred = new[] { new KeyInference.Inferred("customer_key", ["source_system", "customer_id"], 1) };

        var (added, none) = KeyInference.Store("name: marts.dim_customer\nkind: view\n", "m.yml", inferred);
        Assert.Empty(none);
        Assert.Equal("name: marts.dim_customer\nkind: view\nkeys:\n  customer_key: {from: [source_system, customer_id]}\n", added);

        var (more, _) = KeyInference.Store("name: m\nkeys:\n  other_key: {from: [a]}\ncolumns:\n  - {name: a, type: BIGINT}\n", "m.yml", inferred);
        Assert.Equal("name: m\nkeys:\n  other_key: {from: [a]}\n  customer_key: {from: [source_system, customer_id]}\ncolumns:\n  - {name: a, type: BIGINT}\n", more);

        Assert.Null(KeyInference.Store(added!, "m.yml", inferred).NewText);
    }

    [Fact]
    public void A_stored_key_with_other_arguments_is_a_problem_never_rewritten()
    {
        var (text, problems) = KeyInference.Store("name: m\nkeys:\n  customer_key: {from: [customer_id]}\n", "m.yml", [new KeyInference.Inferred("customer_key", ["source_system", "customer_id"], 3)]);

        Assert.Null(text);
        var (line, problem) = Assert.Single(problems);
        Assert.Equal(3, line);
        Assert.Contains("changes every value", problem);
    }

    private const string Staging = "name: staging.customers\nkind: mapped\ngrain: [customer_id]\ncolumns:\n  - {name: source_system, type: \"VARCHAR(10)\", nullable: false}\n  - {name: customer_id, type: BIGINT, nullable: false}\n";
    private const string Definition = "name: marts.dim_customer\nkind: view\ncolumns:\n  - {name: source_system, type: \"VARCHAR(10)\", nullable: false}\n  - {name: customer_id, type: BIGINT, nullable: false}\n  - {name: customer_key, type: BIGINT}\n";
    private const string Query = "SELECT c.source_system, c.customer_id, surrogate_key(c.source_system, c.customer_id) AS customer_key FROM staging.customers c\n";

    private static string Project()
    {
        var dir = NewProjectDir();
        Directory.CreateDirectory(Path.Combine(dir, "models/staging"));
        Directory.CreateDirectory(Path.Combine(dir, "models/marts"));
        TestFiles.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), "defaults: {connections: [sqlserver]}\n");
        TestFiles.WriteAllText(Path.Combine(dir, "models/staging/customers.yml"), Staging);
        TestFiles.WriteAllText(Path.Combine(dir, "models/marts/dim_customer.yml"), Definition);
        TestFiles.WriteAllText(Path.Combine(dir, "models/marts/dim_customer.sql"), Query);
        return dir;
    }

    private static (int Exit, string Out, string Err) Update(string dir, params string[] more)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(["project", "model", "update", "--project", dir, .. more], o, e, new StringReader(""), false);
        return (exit, o.ToString(), e.ToString());
    }

    [Fact]
    public void Update_check_names_the_key_the_definition_lacks_and_writes_nothing()
    {
        var dir = Project();
        var (exit, _, err) = Update(dir, "--check");

        Assert.Equal(CliApp.ExitFindings, exit);
        Assert.Contains("customer_key", err);
        Assert.DoesNotContain("keys:", File.ReadAllText(Path.Combine(dir, "models/marts/dim_customer.yml")));
    }

    [Fact]
    public void Update_write_stores_the_key_and_the_query_then_binds_as_a_call_of_the_key()
    {
        var dir = Project();
        var (exit, output, err) = Update(dir, "--write");

        Assert.Equal((CliApp.ExitOk, ""), (exit, err));
        Assert.Contains("wrote models/marts/dim_customer.yml (keys: customer_key)", output);
        Assert.EndsWith("keys:\n  customer_key: {from: [source_system, customer_id]}\n", File.ReadAllText(Path.Combine(dir, "models/marts/dim_customer.yml")));
        Assert.Equal(Query, File.ReadAllText(Path.Combine(dir, "models/marts/dim_customer.sql")));      // define never writes a query

        var ctx = ProjectContext.Load(dir);
        var model = ctx.Project.BuiltModels.Single(m => m.Definition.Name == "marts.dim_customer");
        Assert.Contains("customer_key(c.source_system, c.customer_id) AS customer_key", model.ReadQuery(dir, ctx.Config));
        Assert.Equal(CliApp.ExitOk, Update(dir, "--check").Exit);                                       // and a second run has nothing to change
    }
}
