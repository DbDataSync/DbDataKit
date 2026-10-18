using DbDataBuild.State;

namespace DbDataBuild.Tests.Unit;

public class HashingTests
{
    private static ColumnShape Col(string name, string type = "bigint", int? len = null, int? p = null, int? s = null, bool nullable = true, string? collation = null, string? computed = null) =>
        new(name, type, len, p, s, nullable, collation, computed);

    [Fact]
    public void Shape_hash_ignores_column_order_but_not_the_set_of_columns()
    {
        var a = Hashing.ShapeHash([Col("a"), Col("b", "varchar", 20)]);
        Assert.Equal(a, Hashing.ShapeHash([Col("b", "varchar", 20), Col("a")]));
        Assert.NotEqual(a, Hashing.ShapeHash([Col("a")]));
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    public static TheoryData<string> Changes => new()
    {
        "name", "type", "length", "precision", "scale", "nullability", "collation", "computed",
    };

    [Theory, MemberData(nameof(Changes))]
    public void Shape_hash_changes_with_every_covered_attribute(string what)
    {
        var baseline = Col("a", "decimal", null, 18, 2, true, "CI", null);
        var changed = what switch
        {
            "name" => baseline with { Name = "b" },
            "type" => baseline with { Type = "numeric" },
            "length" => baseline with { Length = 10 },
            "precision" => baseline with { Precision = 19 },
            "scale" => baseline with { Scale = 3 },
            "nullability" => baseline with { Nullable = false },
            "collation" => baseline with { Collation = "CS" },
            _ => baseline with { Computed = "a + 1" },
        };
        Assert.NotEqual(Hashing.ShapeHash([baseline]), Hashing.ShapeHash([changed]));
    }

    [Fact]
    public void Shape_hash_is_stable_for_a_known_input()
    {
        // pins the canonical text format: a change here is a change of every recorded hash
        Assert.Equal("shape/v1\na|bigint|~|~|~|null|~|~\n", Hashing.ShapeText([Col("a")]));
    }

    [Fact]
    public void Separator_characters_in_names_cannot_make_two_shapes_collide()
    {
        Assert.NotEqual(Hashing.ShapeHash([Col("a|bigint")]), Hashing.ShapeHash([Col("a", "bigint|bigint")]));
        Assert.NotEqual(Hashing.ShapeHash([Col("a\nb")]), Hashing.ShapeHash([Col("a"), Col("b")]));
        Assert.NotEqual(Hashing.ShapeHash([Col("a", collation: null)]), Hashing.ShapeHash([Col("a", collation: "~")])); // null and the literal "~" differ
    }

    [Fact]
    public void Physical_hash_ignores_enumeration_order_and_tracks_definitions()
    {
        var x = new PhysicalItem("index", "ix_a", "a asc");
        var y = new PhysicalItem("index", "ix_b", "b asc");
        Assert.Equal(Hashing.PhysicalHash([x, y]), Hashing.PhysicalHash([y, x]));
        Assert.NotEqual(Hashing.PhysicalHash([x, y]), Hashing.PhysicalHash([x, y with { Definition = "b desc" }]));
        Assert.NotEqual(Hashing.PhysicalHash([]), Hashing.ShapeHash([]));
    }

    [Fact]
    public void Script_hash_normalizes_line_endings_only()
    {
        Assert.Equal(Hashing.ScriptHash("a\nb"), Hashing.ScriptHash("a\r\nb"));
        Assert.NotEqual(Hashing.ScriptHash("a\nb"), Hashing.ScriptHash("a\nb "));
    }

    [Fact]
    public void Ordinals_are_recorded_outside_the_hash()
    {
        Assert.Equal("a,b", Hashing.OrdinalText(["a", "b"]));
    }
}
