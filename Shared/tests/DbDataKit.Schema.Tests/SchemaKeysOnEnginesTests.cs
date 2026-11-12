using DbDataKit.Metadata;

namespace DbDataKit.Schema.Tests;

/// <summary>
/// The keys of a whole schema (<see cref="ICatalogReader.ReadSchemaKeysAsync"/>) on SQL Server and PostgreSQL: the answer equals what each table's own shape says, a composite key and a composite foreign key keep their column order, and a table of another
/// schema with the same name is not mixed in. Skipped without the engines (<c>testenv/testenv.sh up mssql postgres</c>).
/// </summary>
public class SchemaKeysOnEnginesTests
{
    private static async Task Create(Engines engines)
    {
        await engines.ExecuteAsync("CREATE SCHEMA sk1");
        await engines.ExecuteAsync("CREATE SCHEMA sk2");
        await engines.ExecuteAsync("CREATE TABLE sk1.parent (a INT NOT NULL, b INT NOT NULL, note VARCHAR(20), CONSTRAINT pk_parent PRIMARY KEY (b, a))");
        await engines.ExecuteAsync("CREATE TABLE sk1.child (id INT NOT NULL PRIMARY KEY, pa INT NOT NULL, pb INT NOT NULL, CONSTRAINT fk_child_parent FOREIGN KEY (pb, pa) REFERENCES sk1.parent (b, a))");
        await engines.ExecuteAsync("CREATE TABLE sk1.loose (x INT)");
        await engines.ExecuteAsync("CREATE VIEW sk1.v_parent AS SELECT a, b FROM sk1.parent");
        await engines.ExecuteAsync("CREATE TABLE sk2.child (id INT NOT NULL PRIMARY KEY, other INT)");
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task The_keys_of_a_schema_equal_those_of_each_table(string engine)
    {
        await using var engines = await Engines.OpenAsync(engine);
        await Create(engines);
        var reader = engines.Reader();

        var keys = await reader.ReadSchemaKeysAsync(null, "sk1");

        Assert.True(keys.PrimaryKeysRead && keys.ForeignKeysRead);
        Assert.Equal(["child", "parent"], keys.PrimaryKeys.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["b", "a"], keys.PrimaryKeys["parent"].Columns);
        Assert.Equal("pk_parent", keys.PrimaryKeys["parent"].Name);
        var fk = Assert.Single(keys.ForeignKeys["child"]);
        Assert.Equal("fk_child_parent", fk.Name);
        Assert.Equal(["pb", "pa"], fk.Columns);
        Assert.Equal(["b", "a"], fk.ReferencedColumns);
        Assert.Equal(("sk1", "parent"), (fk.Referenced.Schema, fk.Referenced.Name));
        Assert.Equal(["child"], keys.ForeignKeys.Keys);
        Assert.False(keys.PrimaryKeys.ContainsKey("v_parent"));

        foreach (var name in new[] { "parent", "child", "loose" })
        {
            var shape = await reader.GetShapeAsync(new TableRef(null, "sk1", name));
            Assert.Equal(shape.PrimaryKey?.Columns, keys.PrimaryKeys.TryGetValue(name, out var pk) ? pk.Columns : null);
            Assert.Equal(shape.ForeignKeys.Select(f => (f.Name, f.Referenced.Schema, f.Referenced.Name, string.Join(',', f.Columns), string.Join(',', f.ReferencedColumns), f.OnDelete)),
                keys.ForeignKeys.TryGetValue(name, out var fks) ? fks.Select(f => (f.Name, f.Referenced.Schema, f.Referenced.Name, string.Join(',', f.Columns), string.Join(',', f.ReferencedColumns), f.OnDelete)) : []);
        }

        var other = await reader.ReadSchemaKeysAsync(null, "sk2");
        Assert.Equal(["id"], other.PrimaryKeys["child"].Columns);
        Assert.Empty(other.ForeignKeys);
    }
}
