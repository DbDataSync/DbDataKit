using System.Data.Odbc;
using DbData.Dialect;

namespace DbData.Connections.Tests;

public class ParameterAdapterTests
{
    [Fact]
    public void At_names_become_question_marks_with_the_order_recorded()
    {
        var c = ParameterStyleAdapter.ToPositional("SELECT * FROM t WHERE a = @a AND b = @b OR c = @a");
        Assert.Equal("SELECT * FROM t WHERE a = ? AND b = ? OR c = ?", c.Sql);
        Assert.Equal(["a", "b", "a"], c.ParameterOrder);
    }

    [Fact]
    public void Markers_in_strings_comments_and_quoted_identifiers_are_left_alone()
    {
        var c = ParameterStyleAdapter.ToPositional("SELECT '@x', [@y], \"@z\" /* @c */ FROM t -- @d\nWHERE k = @k");
        Assert.Equal("SELECT '@x', [@y], \"@z\" /* @c */ FROM t -- @d\nWHERE k = ?", c.Sql);
        Assert.Equal(["k"], c.ParameterOrder);
    }

    [Fact]
    public void Colon_and_numbered_sources_work()
    {
        var oracle = ParameterStyleAdapter.ToPositional("SELECT :a, :b, :a FROM dual", DialectCatalog.Builtin.Get(EngineId.Oracle));
        Assert.Equal("SELECT ?, ?, ? FROM dual", oracle.Sql);
        Assert.Equal(["a", "b", "a"], oracle.ParameterOrder);
        var pg = ParameterStyleAdapter.ToPositional("SELECT $2, $1, $2", DialectCatalog.Builtin.Get(EngineId.Postgres));
        Assert.Equal("SELECT ?, ?, ?", pg.Sql);
        Assert.Equal(3, pg.ParameterOrder.Count);
    }

    [Fact]
    public void Postgres_casts_are_not_parameters()
    {
        var c = ParameterStyleAdapter.ToPositional("SELECT a::int, :x FROM t", DialectCatalog.Builtin.Get(EngineId.Oracle));
        Assert.Equal(["x"], c.ParameterOrder.Where(n => n == "x").ToList());
    }

    [Fact]
    public void Target_must_be_positional()
    {
        Assert.Throws<ArgumentException>(() => ParameterStyleAdapter.ToPositional("SELECT @a", DialectCatalog.Builtin.Get(EngineId.SqlServer), DialectCatalog.Builtin.Get(EngineId.SqlServer)));
    }

    [Fact]
    public void Bind_adds_one_parameter_per_marker_in_order_including_repeats()
    {
        var c = ParameterStyleAdapter.ToPositional("SELECT @b, @a, @b");
        using var cmd = new OdbcCommand();
        c.Bind(cmd, new Dictionary<string, object?> { ["@a"] = 1, ["B"] = "two", ["unused"] = 9 });
        Assert.Equal("SELECT ?, ?, ?", cmd.CommandText);
        Assert.Equal(3, cmd.Parameters.Count);
        Assert.Equal("two", cmd.Parameters[0].Value);
        Assert.Equal(1, cmd.Parameters[1].Value);
        Assert.Equal("two", cmd.Parameters[2].Value);
    }

    [Fact]
    public void Null_binds_as_dbnull_and_a_missing_value_names_the_parameter()
    {
        var c = ParameterStyleAdapter.ToPositional("SELECT @a, @b");
        using var cmd = new OdbcCommand();
        c.Bind(cmd, new Dictionary<string, object?> { ["a"] = null, ["b"] = 1 });
        Assert.Equal(DBNull.Value, cmd.Parameters[0].Value);
        var ex = Assert.Throws<ArgumentException>(() => c.Bind(cmd, new Dictionary<string, object?> { ["a"] = 1 }));
        Assert.Contains("'b'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_with_no_markers_is_unchanged()
    {
        var c = ParameterStyleAdapter.ToPositional("SELECT 1");
        Assert.Equal("SELECT 1", c.Sql);
        Assert.Empty(c.ParameterOrder);
    }
}
