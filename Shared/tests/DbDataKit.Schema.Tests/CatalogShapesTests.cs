using DbDataKit.Dialect;
using DbDataKit.Metadata;
using DbDataKit.Schema.Reading;

namespace DbDataKit.Schema.Tests;

/// <summary>What a catalog's words become in the shapes that are hashed (plan 19, H04), without an engine: the same reading the live tests do against SQL Server and PostgreSQL.</summary>
public class CatalogShapesTests
{
    private static ColumnInfo Col(string name, int ordinal, string nativeType, bool nullable = true, string? collation = null, bool computed = false, string? expression = null) =>
        new(name, ordinal, CanonicalType.Of(CanonicalKind.String), nativeType, nullable, expression, false, computed, collation);

    private static TableShape Table(string name, TableKind kind, IReadOnlyList<ColumnInfo> columns, params IndexInfo[] indexes) =>
        new(new TableRef(null, "s", name, kind), columns, null, [], indexes, null, ShapeSupport.Full);

    [Theory]
    [InlineData(ShapeEngine.SqlServer, "nvarchar(50)", "nvarchar", 50, null, null)]
    [InlineData(ShapeEngine.SqlServer, "nvarchar(max)", "nvarchar", -1, null, null)]
    [InlineData(ShapeEngine.SqlServer, "decimal(18,3)", "decimal", null, 18, 3)]
    [InlineData(ShapeEngine.SqlServer, "decimal(18)", "decimal", null, 18, 0)]
    [InlineData(ShapeEngine.SqlServer, "datetime2(6)", "datetime2", null, null, 6)]
    [InlineData(ShapeEngine.SqlServer, "datetime2", "datetime2", null, null, null)]
    [InlineData(ShapeEngine.SqlServer, "bigint", "bigint", null, null, null)]
    [InlineData(ShapeEngine.Postgres, "character varying(40)", "character varying", 40, null, null)]
    [InlineData(ShapeEngine.Postgres, "numeric(14,2)", "numeric", null, 14, 2)]
    [InlineData(ShapeEngine.Postgres, "timestamp without time zone", "timestamp without time zone", null, null, 6)]
    [InlineData(ShapeEngine.Postgres, "timestamp(3) without time zone", "timestamp without time zone", null, null, 3)]
    [InlineData(ShapeEngine.Postgres, "integer[]", "array", null, null, null)]
    public void A_native_type_becomes_a_name_and_the_arguments_that_type_uses(ShapeEngine engine, string nativeType, string type, int? length, int? precision, int? scale)
    {
        var shape = CatalogShapes.ToObjectShape(Table("t", TableKind.Table, [Col("c", 1, nativeType)]), "s", engine);

        var column = Assert.Single(shape.Columns);
        Assert.Equal((type, length, precision, scale), (column.Type, column.Length, column.Precision, column.Scale));
    }

    [Fact]
    public void Columns_come_in_ordinal_order_with_their_nullability_collation_and_computed_text()
    {
        var shape = CatalogShapes.ToObjectShape(Table("t", TableKind.Table, [Col("b", 2, "int", false, "C"), Col("a", 1, "int", true, null, true, "([b]+1)")]), "s", ShapeEngine.SqlServer);

        Assert.Equal(["a", "b"], shape.Columns.Select(c => c.Name));
        Assert.Equal(("([b]+1)", true), (shape.Columns[0].Computed, shape.Columns[0].Nullable));
        Assert.Equal(("C", false, null), (shape.Columns[1].Collation, shape.Columns[1].Nullable, shape.Columns[1].Computed));
    }

    [Fact]
    public void An_index_is_one_physical_item_with_its_keys_in_order_descending_marked_and_its_included_columns_and_a_constraint_is_told_apart()
    {
        var shape = CatalogShapes.ToObjectShape(Table("t", TableKind.Table, [Col("a", 1, "int")],
            new IndexInfo("ix", true, [new IndexColumn("a"), new IndexColumn("b", Descending: true)], ["c"]),
            new IndexInfo("pk_t", true, [new IndexColumn("a")], null, BacksConstraint: true, Compression: "PAGE", Partitions: 2)), "s", ShapeEngine.SqlServer);

        Assert.Equal(
            [("index", "ix", "unique=1;keys=a,b desc;include=c"), ("constraint_index", "pk_t", "unique=1;keys=a;include="), ("compression", "pk_t", "PAGE partitions=2")],
            shape.Physical.Select(p => (p.Kind, p.Name, p.Definition)));
    }

    [Fact]
    public void PostgreSQL_has_no_compression_item_and_a_view_is_a_view()
    {
        var shape = CatalogShapes.ToObjectShape(Table("v", TableKind.View, [Col("a", 1, "integer")], new IndexInfo("ix", false, [new IndexColumn("a")], null, Compression: "PAGE", Partitions: 1)), "s", ShapeEngine.Postgres);

        Assert.Equal(ObjectKind.View, shape.Kind);
        Assert.Equal(["index"], shape.Physical.Select(p => p.Kind));
    }
}
