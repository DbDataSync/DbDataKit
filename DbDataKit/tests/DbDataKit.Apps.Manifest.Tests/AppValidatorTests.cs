using DbDataKit.Apps;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Apps.Manifest.Tests;

public sealed class AppValidatorTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "app-validate-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string bundle;

    public AppValidatorTests() { Directory.CreateDirectory(Path.Combine(folder, "src")); bundle = Path.Combine(folder, "dist"); Directory.CreateDirectory(bundle); }
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    private void Write(string relative, string text) { var p = Path.Combine(folder, relative); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }

    private const string Good = "schemaVersion: 1\nname: sales\nversion: 1.0.0\nentry: src/main.ts\nmodels:\n  - model: territories\n    entities:\n      territory: [read, write]\n";
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Store = new Dictionary<string, IReadOnlySet<string>> { ["territories"] = new HashSet<string> { "territory", "region" } };

    [Fact]
    public void A_good_app_is_valid_and_says_so()
    {
        Write("app.yaml", Good); Write("src/main.ts", "");
        Write("dist/index.html", "<script src=\"./a.js\"></script>");
        var result = AppValidator.Validate(folder, bundle, Store);
        Assert.True(result.Ok, result.Render());
        Assert.Equal("The app is valid.\n", result.Render().Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_folder_without_a_manifest_is_one_clear_error()
    {
        var result = AppValidator.Validate(folder);
        Assert.False(result.Ok);
        Assert.Contains("There is no `app.yaml`", Assert.Single(result.Diagnostics).Message);
    }

    [Fact]
    public void Problems_of_every_kind_are_reported_together_with_the_file_that_has_them()
    {
        Write("app.yaml", Good.Replace("src/main.ts", "src/missing.ts").Replace("territory:", "terrytory:"));
        Write("dist/index.html", "<script src=\"/assets/a.js\"></script><script src=\"https://cdn.example.com/x.js\"></script>");
        var result = AppValidator.Validate(folder, bundle, Store);
        Assert.False(result.Ok);
        var text = result.Render();
        Assert.Contains("`src/missing.ts`, which is not in the app's folder", text);
        Assert.Contains("`territories.terrytory`, which the model does not have", text);
        Assert.Contains("The model has: region, territory.", text);
        Assert.Contains("index.html:1:1: error DBD-APP-007", text);
        Assert.Contains("warning DBD-APP-010", text);
        Assert.Contains("https://cdn.example.com", text);
        Assert.EndsWith("3 error(s), 1 warning(s).\n", text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_warning_alone_does_not_fail_the_app()
    {
        Write("app.yaml", Good); Write("src/main.ts", "");
        Write("dist/main.js", "fetch('https://api.example.com/v1')");
        var result = AppValidator.Validate(folder, bundle, Store);
        Assert.True(result.Ok);
        Assert.Contains("1 warning(s)", result.Render());
    }

    [Fact]
    public void An_origin_the_manifest_asked_for_is_not_a_warning()
    {
        Write("app.yaml", Good + "egress:\n  connect: [https://api.example.com]\n"); Write("src/main.ts", "");
        Write("dist/main.js", "fetch('https://api.example.com/v1')");
        Assert.Empty(AppValidator.Validate(folder, bundle, Store).Diagnostics);
    }

    [Fact]
    public void An_unknown_model_is_named_and_the_check_is_skipped_when_no_store_was_asked()
    {
        Write("app.yaml", Good.Replace("territories", "nothing")); Write("src/main.ts", "");
        Assert.Contains("the model `nothing`, which the Store does not have", AppValidator.Validate(folder, null, Store).Render());
        Assert.True(AppValidator.Validate(folder).Ok);
    }

    [Fact]
    public void A_portal_s_home_and_logo_files_must_exist()
    {
        Write("app.yaml", "schemaVersion: 1\nname: contoso\nversion: 1.0.0\nkind: portal\nportal:\n  identity: { logo: assets/logo.svg }\n  home: [views/home.yaml]\n");
        var text = AppValidator.Validate(folder).Render();
        Assert.Contains("`views/home.yaml`", text);
        Assert.Contains("`assets/logo.svg`", text);
        Write("views/home.yaml", ""); Write("assets/logo.svg", "");
        Assert.True(AppValidator.Validate(folder).Ok);
    }
}
