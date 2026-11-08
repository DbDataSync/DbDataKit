using DbDataKit.Config.Yaml;

namespace DbDataKit.Apps.Manifest.Tests;

/// <summary>The declarative home view of a portal (plan 26, AP12): its format, and its diagnostics with file and line.</summary>
public sealed class HomeViewTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "home-view-" + Guid.NewGuid().ToString("N")[..8]);

    public HomeViewTests() => Directory.CreateDirectory(Path.Combine(folder, "views"));

    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    private static readonly IReadOnlySet<string> Mounted = new HashSet<string> { "sales", "hr" };

    private const string Good = """
        schemaVersion: 1
        blocks:
          - text: |
              # Welcome
              Pick an app, or read the **guide**.
          - links:
              - label: Handbook
                href: https://example.com/handbook
              - label: Help desk
                href: /help/
          - tiles: all
          - tiles: [hr, sales]
        """;

    [Fact]
    public void A_view_of_text_links_and_tiles_reads()
    {
        var load = HomeViewLoader.Load(Good, "views/home.yaml", Mounted);
        Assert.True(load.Ok, string.Join('\n', load.Diagnostics));
        var blocks = load.View!.Blocks;
        Assert.Equal(4, blocks.Count);
        Assert.StartsWith("# Welcome", Assert.IsType<HomeText>(blocks[0]).Markdown);
        Assert.Equal(["Handbook", "Help desk"], Assert.IsType<HomeLinks>(blocks[1]).Links.Select(l => l.Label));
        Assert.Null(Assert.IsType<HomeTiles>(blocks[2]).Apps);
        Assert.Equal(["hr", "sales"], Assert.IsType<HomeTiles>(blocks[3]).Apps);
    }

    [Theory]
    [InlineData("schemaVersion: 1\nblocks:\n  - script: alert(1)\n", HomeViewLoader.BadHomeView, 3, "is not a block")]
    [InlineData("schemaVersion: 1\nblocks:\n  - text: ok\n  - dashboard: sales-by-region\n", HomeViewLoader.BadHomeView, 4, "not in this version")]
    [InlineData("schemaVersion: 2\nblocks: []\n", HomeViewLoader.BadHomeView, 1, "schemaVersion")]
    [InlineData("schemaVersion: 1\nblocks:\n  - links:\n      - label: x\n        href: javascript:alert(1)\n", HomeViewLoader.UnsafeHomeText, 5, "not a link")]
    [InlineData("schemaVersion: 1\nblocks:\n  - links:\n      - label: x\n        href: //evil.example/x\n", HomeViewLoader.UnsafeHomeText, 5, "not a link")]
    [InlineData("schemaVersion: 1\nblocks:\n  - tiles: [sales, finance]\n", HomeViewLoader.UnknownTileApp, 3, "`finance` is not mounted")]
    [InlineData("schemaVersion: 1\nblocks:\n  - text: \"hello <script>alert(1)</script>\"\n", HomeViewLoader.UnsafeHomeText, 3, "no HTML or script")]
    [InlineData("schemaVersion: 1\nblocks:\n  - text: \"[go](javascript:alert(1))\"\n", HomeViewLoader.UnsafeHomeText, 3, "not a link")]
    [InlineData("schemaVersion: 1\nblocks:\n  - text: a\n    links: []\n", HomeViewLoader.BadHomeView, 3, "exactly one")]
    [InlineData("schemaVersion: 1\nblocks:\n  - tiles: some\n", HomeViewLoader.BadHomeView, 3, "`all`")]
    public void A_problem_is_a_diagnostic_with_the_file_the_line_and_a_code_in_the_series(string yaml, string code, int line, string says)
    {
        var load = HomeViewLoader.Load(yaml, "views/home.yaml", Mounted);
        Assert.False(load.Ok);
        var found = Assert.Single(load.Diagnostics, d => d.Code == code);
        Assert.StartsWith("DBD-APP-", found.Code);
        Assert.Equal("views/home.yaml", found.Path);
        Assert.Equal(line, found.Line);
        Assert.Contains(says, found.Message);
    }

    [Fact]
    public void The_line_of_a_problem_inside_block_text_points_at_that_line()
    {
        var load = HomeViewLoader.Load("schemaVersion: 1\nblocks:\n  - text: |\n      # Title\n      fine\n      oops <b>x</b>\n", "views/home.yaml");
        var found = Assert.Single(load.Diagnostics);
        Assert.Equal(HomeViewLoader.UnsafeHomeText, found.Code);
        Assert.Equal(6, found.Line);
    }

    [Fact]
    public void An_empty_file_is_refused_and_a_validation_finds_the_home_file_and_line()
    {
        Assert.Contains(HomeViewLoader.Load("", "views/home.yaml").Diagnostics, d => d.Message.Contains("empty"));

        File.WriteAllText(Path.Combine(folder, "app.yaml"), "schemaVersion: 1\nname: contoso\nversion: 1.0.0\nkind: portal\nportal:\n  mounts: [{app: sales, path: /sales/}]\n  home: [views/home.yaml]\n");
        File.WriteAllText(Path.Combine(folder, "views", "home.yaml"), "schemaVersion: 1\nblocks:\n  - tiles: [nowhere]\n");
        var text = AppValidator.Validate(folder).Render();
        Assert.Contains("views/home.yaml:3", text);
        Assert.Contains("DBD-APP-012", text);
        File.WriteAllText(Path.Combine(folder, "views", "home.yaml"), "schemaVersion: 1\nblocks:\n  - tiles: [sales]\n");
        Assert.True(AppValidator.Validate(folder).Ok);
    }
}
