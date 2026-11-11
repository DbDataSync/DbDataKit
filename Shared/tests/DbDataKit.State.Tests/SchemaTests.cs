namespace DbDataKit.State.Tests;

public sealed class SchemaTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_shared_fragments_apply_and_reopening_does_not_reapply_them(string engine)
    {
        var database = _engines.Open(engine);
        var users = new UserStore(database);
        users.CreateUser("Ada", null, UserRole.Admin);

        using (var connection = database.OpenConnection())
            Assert.Equal(StateEngines.Schema.Count, database.Dialect.GetSchemaVersion(connection));

        // opening again checks the version and applies nothing; the data is still there
        using (var connection = database.OpenConnection())
            Assert.Equal(StateEngines.Schema.Count, database.Dialect.GetSchemaVersion(connection));
        Assert.True(users.Any());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_product_adds_its_own_tables_after_the_shared_ones(string engine)
    {
        var schema = new StateSchema([IdentitySchema.Accounts, "CREATE TABLE Widgets (Id {{key}} PRIMARY KEY, Label {{text}} NULL);"]);
        var database = _engines.Open(engine, schema);

        using var connection = database.OpenConnection();
        using var insert = database.Command(connection, "INSERT INTO Widgets (Id, Label) VALUES ($id, $label);");
        insert.Bind(database, "id", "w1");
        insert.Bind(database, "label", "first");
        insert.ExecuteNonQuery();
        using var count = database.Command(connection, "SELECT COUNT(*) FROM Widgets;");
        Assert.Equal(1L, Convert.ToInt64(count.ExecuteScalar()));
    }

    [Fact]
    public void Templates_render_the_engines_words_for_each_token()
    {
        const string template = "CREATE TABLE T (Id {{identity:Id}}, A {{key}} NOT NULL, B {{text}} NULL, C {{int}} NOT NULL);";

        var sqlite = StateTemplates.Render(template, SqliteStateDialect.Instance);

        Assert.DoesNotContain("{{", sqlite);
        Assert.Contains("Id INTEGER PRIMARY KEY AUTOINCREMENT", sqlite);
        Assert.Contains("A TEXT NOT NULL", sqlite);
    }

    [Fact]
    public void Splitting_ignores_semicolons_in_comments_and_strings()
    {
        const string script = """
            -- a note; with a semicolon
            CREATE TABLE A (X TEXT DEFAULT 'a;b');
            CREATE INDEX IX ON A(X); -- trailing; comment
            """;

        var statements = StateTemplates.SplitStatements(script);

        Assert.Equal(2, statements.Count);
        Assert.Contains("'a;b'", statements[0]);
    }

    [Fact]
    public void An_unknown_engine_names_the_built_in_ones()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StateDialect.For("Oracle"));
        Assert.Contains(StateEngineIds.Sqlite, ex.Message);
        Assert.Contains(StateEngineIds.Postgres, ex.Message);
    }
}
