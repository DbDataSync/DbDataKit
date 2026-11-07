using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Tests;

/// <summary>Plan 29b X01: the logical type of a column, read from the migration templates.</summary>
public sealed class StateTableDefinitionsTests
{
    [Fact]
    public void Columns_are_typed_in_order_across_migrations_with_the_key_and_identity_found()
    {
        var tables = StateTableDefinitions.Read(SnapshotTestModules.Types);

        Assert.Equal(["sample", "pair", "counter"], tables.Select(t => t.Name));
        var sample = tables[0];
        Assert.Equal(
            ["id:uuid", "label:text", "note:text", "created_at:time", "seen_at:time", "enabled:bool", "amount:decimal", "doc:json", "data:blob", "size:int", "extra:text"],
            sample.Columns.Select(c => $"{c.Name}:{c.Type}"));
        Assert.Equal(["id"], sample.PrimaryKey);
        Assert.Equal(["a", "b"], tables[1].PrimaryKey);
        Assert.Equal(("id", true), (tables[2].Columns[0].Name, tables[2].Columns[0].Identity));
        Assert.Equal(["id"], tables[2].PrimaryKey);
    }

    [Fact]
    public void Every_shared_module_is_understood_and_has_a_key_in_every_table()
    {
        foreach (var module in new[] { IdentityModule.Module, NotificationsModule.Module, AuditModule.Module, DefinitionsModule.Module, AppStorageModule.Module, RunsModule.Module, ProgressModule.Module, PayloadStore.Module })
        {
            var tables = StateTableDefinitions.Read(module);
            Assert.NotEmpty(tables);
            Assert.All(tables, t => Assert.NotEmpty(t.PrimaryKey));
            Assert.All(tables.SelectMany(t => t.Columns), c => Assert.Contains(c.Type, new[] { "uuid", "time", "bool", "int", "decimal", "json", "text", "blob" }));
        }
    }

    [Fact]
    public void Comments_commas_parentheses_and_a_dropped_column_do_not_confuse_it()
    {
        var module = new StateModule("t", StateScopeKind.Install,
        [
            """
            -- a comment, with a comma (and parentheses) and a {table:ignored} token
            CREATE TABLE {table:thing} (
                a {{key}} NOT NULL, -- trailing, comment
                b {{text}} NULL CHECK (b IN ('x,y', 'z')),
                c {{int}} NOT NULL,
                CONSTRAINT thing_key PRIMARY KEY (a, c)
            );
            """,
            "ALTER TABLE {table:thing} {{addcolumn}} d {{decimal}} NOT NULL;",
            "ALTER TABLE {table:thing} DROP COLUMN b;",
        ]);

        var thing = Assert.Single(StateTableDefinitions.Read(module));

        Assert.Equal(["a:text", "c:int", "d:decimal"], thing.Columns.Select(c => $"{c.Name}:{c.Type}"));
        Assert.Equal(["a", "c"], thing.PrimaryKey);
        Assert.Equal([false, false, false], thing.Columns.Select(c => c.Nullable));
    }

    [Fact]
    public void A_type_that_is_not_a_convention_token_is_refused_naming_the_column()
    {
        var raw = new StateModule("t", StateScopeKind.Install, ["CREATE TABLE {table:thing} (id {{key}} PRIMARY KEY, price NUMERIC(10,2) NOT NULL);"]);
        var unknown = new StateModule("u", StateScopeKind.Install, ["CREATE TABLE {table:thing} (id {{key}} PRIMARY KEY, price {{money}} NOT NULL);"]);

        Assert.Contains("u.thing.price", Assert.Throws<InvalidOperationException>(() => StateTableDefinitions.Read(unknown)).Message);
        Assert.Contains("t.thing.price", Assert.Throws<InvalidOperationException>(() => StateTableDefinitions.Read(raw)).Message);
    }

    [Fact]
    public void A_table_without_a_primary_key_is_refused_naming_it()
    {
        var module = new StateModule("t", StateScopeKind.Install, ["CREATE TABLE {table:loose} (a {{key}} NOT NULL, b {{int}} NOT NULL);"]);

        Assert.Contains("t.loose", Assert.Throws<InvalidOperationException>(() => StateTableDefinitions.Read(module)).Message);
    }
}
