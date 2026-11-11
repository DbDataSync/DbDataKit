using DbDataKit.Apps;

namespace DbDataKit.Apps.Manifest.Tests;

public sealed class AppManifestTests : IDisposable
{
    private const string Good = """
        schemaVersion: 1
        name: sales-planning
        title: Sales planning
        version: 1.2.0
        entry: src/main.ts
        views: [views/home.yaml]
        origin: own
        chrome: none
        models:
          - model: territories
            entities: {territory: [read, write], region: [read]}
        semantic: [sales]
        roles: [viewer, editor]
        """;

    private readonly string folder = Path.Combine(Path.GetTempPath(), "app-manifest-" + Guid.NewGuid().ToString("N")[..8]);

    public AppManifestTests() => Directory.CreateDirectory(folder);
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    private static string Problems(string yaml) => string.Join("\n", AppManifestLoader.Load(yaml).Diagnostics.Select(d => $"{d.Line}: {d.Message}"));

    [Fact]
    public void A_complete_manifest_is_read_in_full()
    {
        var result = AppManifestLoader.Load(Good);

        Assert.True(result.Ok, Problems(Good));
        var m = result.Manifest!;
        Assert.Equal(("sales-planning", "1.2.0", "src/main.ts"), (m.Name, m.Version, m.Entry));
        Assert.Equal(AppOrigin.Own, m.Origin);
        Assert.Equal(AppChrome.None, m.Chrome);
        Assert.Equal(["views/home.yaml"], m.Views);
        Assert.Equal(DataOperations.Read | DataOperations.Write, m.Models.Single().Entities["territory"]);
        Assert.Equal(DataOperations.Read, m.Models.Single().Entities["region"]);
        Assert.Equal(["sales"], m.SemanticModels);
        Assert.Equal(["viewer", "editor"], m.Roles);
    }

    [Fact]
    public void The_defaults_are_a_sandboxed_app_under_the_shell()
    {
        var m = AppManifestLoader.Load("schemaVersion: 1\nname: a\nversion: 0.1.0\nentry: main.ts\n").Manifest!;

        Assert.Equal((AppOrigin.Sandbox, AppChrome.Shell), (m.Origin, m.Chrome));
        Assert.Empty(m.Models);
    }

    [Fact]
    public void Writing_an_entity_implies_reading_it()
    {
        var m = AppManifestLoader.Load("schemaVersion: 1\nname: a\nversion: 0.1.0\nentry: main.ts\nmodels:\n  - model: m\n    entities: {e: [write]}\n").Manifest!;

        Assert.Equal(DataOperations.Read | DataOperations.Write, m.Models.Single().Entities["e"]);
    }

    [Theory]
    [InlineData("name: Sales", "is not an app name")]
    [InlineData("name: api", "keeps for itself")]
    [InlineData("name: ../x", "is not an app name")]
    [InlineData("version: 1.2", "is not a version")]
    [InlineData("entry: /etc/passwd", "is not a path inside the app")]
    [InlineData("entry: ../x.ts", "is not a path inside the app")]
    [InlineData("entry: a\\b.ts", "is not a path inside the app")]
    [InlineData("origin: elsewhere", "is not a value of `origin`")]
    [InlineData("chrome: half", "is not a value of `chrome`")]
    [InlineData("bogus: 1", "Unknown key `bogus`")]
    public void A_bad_field_is_refused_with_a_message_that_says_why(string line, string expected)
    {
        var replaced = Good.Split('\n').Select(l => l.StartsWith(line.Split(':')[0] + ":", StringComparison.Ordinal) ? line : l);
        var yaml = line.StartsWith("bogus", StringComparison.Ordinal) ? Good + "\n" + line : string.Join("\n", replaced);

        Assert.Contains(expected, Problems(yaml));
    }

    [Fact]
    public void An_app_with_neither_an_entry_nor_views_has_nothing_to_serve()
    {
        Assert.Contains("nothing to serve", Problems("schemaVersion: 1\nname: a\nversion: 1.0.0\n"));
    }

    [Fact]
    public void A_model_use_must_name_entities_and_known_operations()
    {
        Assert.Contains("`entities` names each entity", Problems("schemaVersion: 1\nname: a\nversion: 1.0.0\nentry: m.ts\nmodels:\n  - model: m\n"));
        Assert.Contains("not an operation", Problems("schemaVersion: 1\nname: a\nversion: 1.0.0\nentry: m.ts\nmodels:\n  - model: m\n    entities: {e: [delete]}\n"));
    }

    [Fact]
    public void A_missing_or_newer_schema_version_names_the_problem_and_the_line()
    {
        Assert.Contains("`schemaVersion` is missing", Problems("name: a\nversion: 1.0.0\nentry: m.ts\n"));
        Assert.Contains("understands up to 1", Problems("schemaVersion: 2\nname: a\nversion: 1.0.0\nentry: m.ts\n"));
        Assert.StartsWith("3:", Problems("schemaVersion: 1\nname: a\nversion: nope\nentry: m.ts\n"));
    }

    [Fact]
    public void A_damaged_file_is_a_diagnostic_never_an_exception()
    {
        foreach (var damaged in new[] { "", "[", "- a\n- b", "schemaVersion: [", "schemaVersion: 1\nname: {a: b}\n", "schemaVersion: 1\nname: a\nversion: 1.0.0\nviews: 5\n" })
            Assert.False(AppManifestLoader.Load(damaged).Ok, damaged);
    }

    [Fact]
    public void Files_the_manifest_names_must_exist()
    {
        var manifest = AppManifestLoader.Load(Good).Manifest!;
        Directory.CreateDirectory(Path.Combine(folder, "src"));
        File.WriteAllText(Path.Combine(folder, "src", "main.ts"), "");

        var missing = AppChecks.Files(manifest, folder);

        Assert.Equal("The manifest names `views/home.yaml`, which is not in the app's folder.", Assert.Single(missing).Message);
    }

    [Fact]
    public void A_bundle_that_points_at_the_servers_root_is_refused_and_relative_paths_pass()
    {
        File.WriteAllText(Path.Combine(folder, "good.html"), "<script src=\"./assets/app.js\"></script><link href='assets/app.css'><script src=\"//cdn.example/x.js\"></script>");
        File.WriteAllText(Path.Combine(folder, "bad.html"), "<html>\n<script type=\"module\" src=\"/assets/app.js\"></script>");
        File.WriteAllText(Path.Combine(folder, "bad.css"), "a { background: url(/img/x.png) }");

        var found = AppChecks.AbsolutePaths(folder);

        Assert.Equal(["bad.css:1", "bad.html:2"], found.Select(d => $"{d.Path}:{d.Line}").ToArray());
    }
}
