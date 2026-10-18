using System.Diagnostics;
using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Web;
using DbDataBuild.Planning;
using DbDataBuild.State;

namespace DbDataBuild.Tests.Unit;

/// <summary>A fact that runs only where a Chrome or Chromium is on the path; elsewhere it is reported as skipped, not passed.</summary>
public sealed class ChromeFactAttribute : FactAttribute
{
    public static readonly string? Browser = Find();

    public ChromeFactAttribute() { if (Browser == null) Skip = "No Chrome or Chromium on the path."; }

    private static string? Find()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "chrome.exe" } : new[] { "google-chrome", "chromium", "chromium-browser", "chrome" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names) { var p = Path.Combine(dir, n); if (File.Exists(p)) return p; }
        return null;
    }
}

/// <summary>The page itself: each screen is loaded in a headless browser against a real server over a template project, and what it drew is read back from the DOM.</summary>
public sealed class WebPageTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ddb-page-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly WebServer server;

    public WebPageTests()
    {
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", dir, "--format", "json"], new StringWriter(), new StringWriter()));
        server = new WebServer(dir, new TuiCommand.CliHost(_ => null));
        server.StartAsync();
    }

    public void Dispose() { server.Dispose(); try { Directory.Delete(dir, true); } catch (IOException) { } }

    /// <summary>The text of the page's main area after the script has run.</summary>
    private string Screen(string route, WebServer? from = null)
    {
        from ??= server;
        var profile = Path.Combine(dir, ".chrome-" + Guid.NewGuid().ToString("N")[..6]);
        var start = new ProcessStartInfo(ChromeFactAttribute.Browser!) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "--headless=new", "--no-sandbox", "--disable-gpu", "--disable-extensions", $"--user-data-dir={profile}", "--virtual-time-budget=10000", "--dump-dom", from.Address + route }) start.ArgumentList.Add(a);
        using var chrome = Process.Start(start)!;
        var dom = chrome.StandardOutput.ReadToEndAsync();
        chrome.StandardError.ReadToEndAsync();
        Assert.True(chrome.WaitForExit(90_000), "The browser did not finish.");
        var main = Regex.Match(dom.Result, "<main id=\"view\"[^>]*>(.*?)</main>", RegexOptions.Singleline);
        Assert.True(main.Success, "The page has no main area.");
        return Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(main.Groups[1].Value, "<[^>]+>", " ")), @"\s+", " ");
    }

    [ChromeFact]
    public void Every_screen_draws_the_projects_own_data_and_none_shows_an_error()
    {
        var model = Directory.EnumerateFiles(Path.Combine(dir, "models"), "*.sql", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Path.Combine(dir, "models"), f)).First();
        var name = string.Join('.', model[..^4].Split('/', '\\'));
        var expectations = new (string Route, string Expected)[]
        {
            ("#/", "tests passed"),
            ("#/lineage", name),
            ($"#/lineage/{name}", "connected"),
            ($"#/models/{name}", "grain"),
            ($"#/models/{name}/sql", "SELECT"),
            ($"#/models/{name}/lowered", "SELECT"),
            ("#/sample", "Run models on sample data"),
            ("#/diff", "Compare two tables"),
            ("#/tests", "tests"),
            ("#/matrix", "construct"),
        };
        foreach (var (route, expected) in expectations)
        {
            var text = Screen(route);
            Assert.DoesNotContain("Could not load", text);
            Assert.DoesNotContain("[object", text);                          // a list of elements put in as text: an array that was not flattened
            Assert.Contains(expected, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [ChromeFact]
    public void The_plans_screen_lists_plans_shows_one_with_its_statements_and_flags_an_edited_one()
    {
        var folder = Path.Combine(dir, "plans", "postgres");
        Directory.CreateDirectory(folder);
        Plan Make(string id) => new(id, "postgres", "4af31c2", false, "0.1.0", [new ObjectBase("marts.customers", ObjectState.InSync, new string('a', 64), new string('a', 64))], [],
            [new PlanStep("1", StepType.Ddl, "marts.customers", "drop column legacy", "ALTER TABLE marts.customers DROP COLUMN legacy;", RiskClass.Destructive, ["col.dropped"], new string('b', 64), [])], ["a note"]);
        File.WriteAllText(Path.Combine(folder, "2026-10-01-aaaa0001.plan.yml"), PlanDocument.Serialize(Make("2026-10-01-aaaa0001")));
        File.WriteAllText(Path.Combine(folder, "2026-10-01-aaaa0001.plan.md"), "# Report of the plan\n");
        var edited = Path.Combine(folder, "2026-10-02-bbbb0002.plan.yml");
        File.WriteAllText(edited, PlanDocument.Serialize(Make("2026-10-02-bbbb0002")).Replace("DROP COLUMN legacy", "DROP COLUMN other"));

        var list = Screen("#/plans");
        Assert.Contains("2026-10-01-aaaa0001", list);
        Assert.Contains("⚠ 2026-10-02-bbbb0002", list);                          // the edited one is marked
        var one = Screen("#/plans/" + Uri.EscapeDataString("plans/postgres/2026-10-01-aaaa0001.plan.yml"));
        Assert.Contains("ALTER TABLE marts.customers DROP COLUMN legacy;", one);
        Assert.Contains("--allow-destructive marts.customers", one);
        Assert.Contains("Report of the plan", one);
        Assert.Contains("a note", one);
        Assert.DoesNotContain("null", one);                                     // an absent panel is left out, not drawn as text
        Assert.Contains("hash does not match", Screen("#/plans/" + Uri.EscapeDataString("plans/postgres/2026-10-02-bbbb0002.plan.yml")));
    }

    [ChromeFact]
    public void The_apply_panel_is_only_there_when_the_server_allows_it()
    {
        var folder = Path.Combine(dir, "plans", "postgres");
        Directory.CreateDirectory(folder);
        var plan = new Plan("2026-10-06-ffff0006", "postgres", null, false, "0.1.0", [new ObjectBase("marts.customers", ObjectState.InSync, new string('a', 64), new string('a', 64))], [],
            [new PlanStep("1", StepType.Ddl, "marts.customers", "drop column legacy", "ALTER TABLE marts.customers DROP COLUMN legacy;", RiskClass.Destructive, ["col.dropped"], new string('b', 64), [])], []);
        File.WriteAllText(Path.Combine(folder, "2026-10-06-ffff0006.plan.yml"), PlanDocument.Serialize(plan));
        var route = "#/plans/" + Uri.EscapeDataString("plans/postgres/2026-10-06-ffff0006.plan.yml");

        Assert.Contains("was not started with --allow-apply", Screen(route));
        using var allowing = new WebServer(dir, new TuiCommand.CliHost(_ => null), 0, allowApply: true);
        allowing.StartAsync();
        var text = Screen(route, allowing);
        Assert.Contains("I allow the destructive steps on marts.customers", text);
        Assert.Contains("Apply to postgres", text);
        Assert.Contains("Dry run", text);
    }

    [ChromeFact]
    public void Text_from_the_project_is_shown_as_text_never_as_markup()
    {
        var path = Path.Combine(dir, "models");
        var file = Directory.EnumerateFiles(path, "*.sql", SearchOption.AllDirectories).First();
        File.AppendAllText(file, "\n-- <img src=x onerror=\"document.title='pwned'\"> <script>document.title='pwned'</script>\n");
        var name = string.Join('.', Path.GetRelativePath(path, file)[..^4].Split('/', '\\'));
        var text = Screen($"#/models/{name}/sql");
        Assert.Contains("<img src=x", text);       // the characters are there, as characters
        Assert.Contains("<script>document.title", text);
    }
}
