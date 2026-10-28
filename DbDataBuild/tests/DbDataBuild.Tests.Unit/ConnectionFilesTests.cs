using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// A project's connections are `connections/&lt;name&gt;.yaml`, `connection/1` files (plan 13, E03). The loader reads them, the project file that still declares connections is refused naming it (DDB-621), a
/// file that is not right is DDB-620 naming the file and the line, every template is on the new format, and the published schema agrees with the loader.
/// </summary>
public class ConnectionFilesTests
{
    private static string Project(string config, params (string Name, string Text)[] files)
    {
        var dir = NewProjectDir();
        File.WriteAllText(Path.Combine(dir, "dbdatabuild.yml"), config);
        if (files.Length > 0) Directory.CreateDirectory(Path.Combine(dir, "connections"));
        foreach (var (name, text) in files) File.WriteAllText(Path.Combine(dir, "connections", name), text);
        return dir;
    }

    private static (ProjectConfig Config, List<Diagnostic> Diags) Load(string dir)
    {
        var diags = new List<Diagnostic>();
        return (ProjectConfigLoader.LoadFromProject(dir, diags), diags);
    }

    private const string Warehouse = "schema: connection/1\nname: warehouse\nengine: sqlserver\nextensions:\n  build: { version: 17, title: Warehouse, tags: [prod], max_parallel: 4, parameters: { store_id: E01 } }\n";
    private const string Origin = "schema: connection/1\nname: shop\nengine: flightsql\ndialect: clickhouse\naccess: readonly\n";

    [Fact]
    public void Connection_files_declare_the_connections_with_everything_the_project_file_used_to_say()
    {
        var dir = Project("defaults: {connections: [warehouse]}\ndialects:\n  clickhouse: { extends: ansi, quote: '`' }\n", ("warehouse.yaml", Warehouse), ("shop.yaml", Origin));
        try
        {
            var (config, diags) = Load(dir);
            Assert.DoesNotContain(diags, d => d.Severity == Severity.Error);
            var warehouse = config.Connections["warehouse"];
            Assert.Equal("sqlserver", warehouse.Engine);
            Assert.Equal(17, warehouse.Version);
            Assert.Equal(4, warehouse.MaxParallel);
            Assert.Equal("Warehouse", warehouse.Info.Title);
            Assert.Equal("E01", warehouse.Parameters["store_id"].ToString() is { } s && s.Contains("E01") ? "E01" : "");
            Assert.False(warehouse.ReadOnly);
            var shop = config.Connections["shop"];
            Assert.Equal("flightsql", shop.Engine);
            Assert.True(shop.ReadOnly);
            Assert.Equal("clickhouse", shop.Kind.Dialect);
            Assert.Contains("sqlserver", config.Connections.Keys);      // a connection named after an engine still needs no file
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_connection_file_may_say_how_its_login_is_secured()
    {
        var dir = Project("defaults: {connections: [warehouse]}\n", ("warehouse.yaml", Warehouse.Replace("engine: sqlserver\n", "engine: sqlserver\ntls: { encrypt: false, trust_server_certificate: true }\n")));
        try
        {
            var (config, diags) = Load(dir);
            Assert.DoesNotContain(diags, d => d.Severity == Severity.Error);
            var tls = config.Connections["warehouse"].Tls;
            Assert.NotNull(tls);
            Assert.False(tls!.Encrypt);
            Assert.True(tls.TrustServerCertificate);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_project_file_that_still_declares_connections_is_refused_naming_the_file()
    {
        var dir = Project("defaults: {connections: [sqlserver]}\nconnections:\n  sqlserver: { version: 16 }\n  shop: { engine: readonly postgres }\n");
        try
        {
            var (config, diags) = Load(dir);
            var refused = Assert.Single(diags, d => d.Code == "DDB-621");
            Assert.Equal("dbdatabuild.yml", refused.Location.File);
            Assert.Equal(2, refused.Location.Line);
            Assert.Contains("`sqlserver`", refused.Found);
            Assert.Contains("`shop`", refused.Found);
            Assert.Contains("connections/<name>.yaml", DiagnosticFormatter.Format(refused));
            Assert.Same(ProjectConfig.Default, config);      // nothing was converted and the project is not loaded

            var o = new StringWriter(); var e = new StringWriter();
            var exit = CliApp.Run(["project", "compile", "--project", dir], o, e);
            Assert.Equal(CliApp.ExitFindings, exit);
            Assert.Contains("DDB-621", o + e.ToString());
            Assert.Contains("dbdatabuild.yml", o + e.ToString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_refusal_is_there_with_a_connection_file_beside_it_too()
    {
        var dir = Project("connections:\n  warehouse: { engine: postgres }\n", ("warehouse.yaml", Warehouse));
        try { Assert.Contains(Load(dir).Diags, d => d.Code == "DDB-621"); }
        finally { Directory.Delete(dir, true); }
    }

    public static TheoryData<string, string, string> BadFiles => new()
    {
        { "name is not the file's", "other.yaml", Warehouse },
        { "an address", "warehouse.yaml", Warehouse.Replace("engine: sqlserver\n", "engine: sqlserver\naddress: { host: db.example.com }\n") },
        { "a login", "warehouse.yaml", Warehouse.Replace("engine: sqlserver\n", "engine: sqlserver\nauth: { user: u, password_secret: \"env:P\" }\n") },
        { "a tls key that is not one", "warehouse.yaml", Warehouse.Replace("engine: sqlserver\n", "engine: sqlserver\ntls: { verfiy: false }\n") },
        { "a timeout", "warehouse.yaml", Warehouse.Replace("engine: sqlserver\n", "engine: sqlserver\ntimeouts: { command_s: 10 }\n") },
        { "the old keywords in engine", "warehouse.yaml", Warehouse.Replace("engine: sqlserver", "engine: readonly sqlserver") },
        { "an unknown key", "warehouse.yaml", Warehouse.Replace("engine: sqlserver\n", "engine: sqlserver\nbogus: 1\n") },
        { "the old schema", "warehouse.yaml", Warehouse.Replace("connection/1", "connection/2") },
        { "no schema", "warehouse.yaml", Warehouse.Replace("schema: connection/1\n", "") },
        { "a plaintext credential", "warehouse.yaml", Warehouse.Replace("engine: sqlserver\n", "engine: sqlserver\nconnection_string: \"Server=x;Password=hunter2\"\n") },
        { "an engine key inside extensions.build", "warehouse.yaml", Warehouse.Replace("build: {", "build: { engine: postgres,") },
        { "extensions.build that is not a mapping", "warehouse.yaml", Warehouse.Replace("build: { version: 17, title: Warehouse, tags: [prod], max_parallel: 4, parameters: { store_id: E01 } }", "build: nope") },
        { "a .yml extension", "warehouse.yml", Warehouse },
        { "a YAML syntax error", "warehouse.yaml", "schema: connection/1\nname: [warehouse\n" },
    };

    [Theory, MemberData(nameof(BadFiles))]
    public void A_file_that_is_not_right_is_DDB_620_naming_the_file(string what, string fileName, string text)
    {
        var dir = Project("defaults: {connections: [sqlserver]}\n", (fileName, text));
        try
        {
            var (config, diags) = Load(dir);
            var found = diags.Where(d => d.Severity == Severity.Error).ToList();
            Assert.True(found.Count > 0, what);
            Assert.All(found.Where(d => d.Code is "DDB-620"), d => Assert.Equal("connections/" + fileName, d.Location.File));
            Assert.Contains(found, d => d.Code is "DDB-620" or "DDB-101");
            Assert.Same(ProjectConfig.Default, config);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_problem_inside_extensions_build_names_the_connection_file_and_its_line()
    {
        var dir = Project("defaults: {connections: [warehouse]}\n", ("warehouse.yaml", Warehouse.Replace("max_parallel: 4", "max_parallel: 400")));
        try
        {
            var (_, diags) = Load(dir);
            var bad = Assert.Single(diags, d => d.Severity == Severity.Error);
            Assert.Equal("connections/warehouse.yaml", bad.Location.File);
            Assert.Equal(5, bad.Location.Line);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Two_names_that_differ_only_in_case_are_still_refused()
    {
        var dir = Project("defaults: {connections: [warehouse]}\n", ("warehouse.yaml", Warehouse), ("Warehouse.yaml", Warehouse.Replace("name: warehouse", "name: Warehouse")));
        try { Assert.Contains(Load(dir).Diags, d => d.Severity == Severity.Error && d.Location.File.StartsWith("connections/", StringComparison.Ordinal)); }
        finally { Directory.Delete(dir, true); }
    }

    public static TheoryData<string> Templates() { var data = new TheoryData<string>(); foreach (var t in TemplateStore.All) data.Add(t.Name); return data; }

    [Theory, MemberData(nameof(Templates))]
    public void Every_template_is_on_the_new_format_and_its_files_satisfy_the_schema(string template)
    {
        var root = Path.Combine(Path.GetTempPath(), "ddb-connfiles-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Assert.Equal(0, CliApp.Run(["project", "create", template, root], TextWriter.Null, TextWriter.Null));
            Assert.DoesNotContain(File.ReadLines(Path.Combine(root, "dbdatabuild.yml")), l => l.StartsWith("connections:", StringComparison.Ordinal));
            var (config, diags) = Load(root);
            Assert.DoesNotContain(diags, d => d.Severity == Severity.Error);
            var schema = SchemaConformanceTests.LoadSchema("connection");
            var folder = Path.Combine(root, "connections");
            var files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.yaml") : [];
            foreach (var file in files)
            {
                Assert.True(SchemaConformanceTests.SchemaAccepts(schema, File.ReadAllText(file)), file);
                Assert.Contains(Path.GetFileNameWithoutExtension(file), config.Connections.Keys);
            }
            if (template == "fan_in_fan_out") Assert.Equal(4, files.Length);
            else if (template != "macro_examples") Assert.Single(files);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData("schema: connection/1\nname: a\nengine: postgres\n", true)]
    [InlineData("schema: connection/1\nname: a\nengine: flightsql\naccess: readonly\ndialect: clickhouse\nextensions:\n  build: { title: x, parameters: { store_id: E01 }, max_parallel: 8 }\n", true)]
    [InlineData("schema: connection/1\nname: a\nengine: postgres\nextensions:\n  sync: { anything: 1 }\n", true)]
    [InlineData("schema: connection/1\nname: a\n", false)]
    [InlineData("schema: connection/1\nname: \"9 bad\"\nengine: postgres\n", false)]
    [InlineData("schema: connection/1\nname: a\nengine: readonly postgres\n", false)]
    [InlineData("schema: connection/1\nname: a\nengine: postgres\naddress: { host: h }\n", false)]
    [InlineData("schema: connection/1\nname: a\nengine: postgres\naccess: sometimes\n", false)]
    [InlineData("schema: connection/1\nname: a\nengine: postgres\nextensions:\n  build: { max_parallel: 400 }\n", false)]
    [InlineData("schema: connection/1\nname: a\nengine: postgres\nextensions:\n  build: { nope: 1 }\n", false)]
    [InlineData("schema: connection/2\nname: a\nengine: postgres\n", false)]
    public void The_schema_and_the_loader_agree(string yaml, bool valid)
    {
        Assert.Equal(valid, SchemaConformanceTests.SchemaAccepts(SchemaConformanceTests.LoadSchema("connection"), yaml));
        var dir = Project("defaults: {connections: [a]}\ndialects:\n  clickhouse: { extends: ansi }\n", ("a.yaml", yaml));      // the whole path: the file reader, then the settings under extensions.build
        try
        {
            var errors = Load(dir).Diags.Where(d => d.Severity == Severity.Error).ToList();
            Assert.Equal(valid, errors.Count == 0);
        }
        finally { Directory.Delete(dir, true); }
    }
}
