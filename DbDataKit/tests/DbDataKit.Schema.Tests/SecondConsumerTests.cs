using DbDataKit.Schema.Ddl;
using DbDataKit.Schema.Planning;

namespace DbDataKit.Schema.Tests;

/// <summary>A consumer that is not Build keeps tables in shape through <c>DbDataKit.Schema</c> alone (plan 19, H05), and the library has nothing of Build's in it.</summary>
public class SecondConsumerTests
{
    private static NativeColumn Bigint(string name, bool nullable = true) => new(name, "bigint", null, nullable, new ColumnShape(name, "bigint", null, null, null, nullable, null));

    private static NativeColumn Text(string engine, string name, int length)
    {
        var (declaration, type, collation) = engine == Engines.SqlServer ? ($"nvarchar({length})", "nvarchar", "Latin1_General_100_CI_AS") : ($"varchar({length})", "character varying", "C");
        return new(name, declaration, collation, true, new ColumnShape(name, type, length, null, null, true, collation));
    }

    private static DesiredTable Table(IEnumerable<NativeColumn> columns, IEnumerable<IndexSpec>? indexes = null) => new("fixture.people", columns.ToList(), [], (indexes ?? []).ToList());

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task A_host_that_is_not_build_creates_a_table_then_changes_it_as_the_declaration_changes_and_then_has_nothing_to_do(string engine)
    {
        await using var engines = await Engines.OpenAsync(engine);
        var host = new FixtureHost(engines, ("Q-history-fixture.people.age", "not_backfilled", null));       // a column added asks what to do with the rows already there

        var created = await host.EnsureAsync(Table([Bigint("id", false), Text(engine, "name", 20)]));
        Assert.Equal(["create schema fixture", "create table fixture.people"], created.Select(s => s.Description));

        // a column and an index are safe to add once that is answered; widening a column is safe too
        var second = await host.EnsureAsync(Table([Bigint("id", false), Text(engine, "name", 40), Bigint("age")], [new IndexSpec("ix_name", false, ["name"], [])]), allow: RiskClass.Safe);
        Assert.Equal(["add column age", "alter column name", "create index ix_name"], second.Select(s => s.Description).Order());

        var again = await host.EnsureAsync(Table([Bigint("id", false), Text(engine, "name", 40), Bigint("age")], [new IndexSpec("ix_name", false, ["name"], [])]));
        Assert.Empty(again);
    }

    [Theory, MemberData(nameof(Engines.Both), MemberType = typeof(Engines))]
    public async Task What_needs_a_person_is_refused_until_the_person_has_said_so(string engine)
    {
        await using var engines = await Engines.OpenAsync(engine);
        await new FixtureHost(engines).EnsureAsync(Table([Bigint("id", false), Text(engine, "name", 20), Bigint("legacy")]));
        var withoutLegacy = Table([Bigint("id", false), Text(engine, "name", 20)]);

        // dropping a column loses its data: refused at 'safe' and at 'risky', and done when destructive changes are allowed
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => new FixtureHost(engines).EnsureAsync(withoutLegacy, RiskClass.Risky));
        Assert.Contains("drop column legacy", refused.Message);
        Assert.Contains("destructive", refused.Message);

        var host = new FixtureHost(engines);
        await host.EnsureAsync(withoutLegacy, RiskClass.Destructive);
        Assert.Equal(["drop column legacy"], host.Applied);

        // a new column asks what to do with the rows already there, and nothing is applied until it is answered
        var withAge = Table([Bigint("id", false), Text(engine, "name", 20), Bigint("age")]);
        var asking = new FixtureHost(engines);
        var open = await Assert.ThrowsAsync<InvalidOperationException>(() => asking.EnsureAsync(withAge));
        Assert.Contains("A person is needed", open.Message);
        Assert.Empty(asking.Applied);

        var answered = new FixtureHost(engines, ("Q-history-fixture.people.age", "not_backfilled", null));
        await answered.EnsureAsync(withAge);
        Assert.Equal(["add column age"], answered.Applied);
    }

    [Fact]
    public void Nothing_in_the_library_names_a_product()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/DbDataKit.Schema"));
        Assert.True(Directory.Exists(root), root);

        var project = File.ReadAllText(Path.Combine(root, "DbDataKit.Schema.csproj"));
        Assert.DoesNotContain("DbDataBuild", project);
        Assert.DoesNotContain("DbDataSync", project);
        Assert.DoesNotContain("DbDataProxy", project);
        Assert.DoesNotContain("DbDataConsole", project);

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            Assert.False(text.Contains("DbDataBuild") || text.Contains("DbDataSync") || text.Contains("DbDataProxy") || text.Contains("DbDataConsole"), $"{Path.GetFileName(file)} names a product");
        }
    }
}
