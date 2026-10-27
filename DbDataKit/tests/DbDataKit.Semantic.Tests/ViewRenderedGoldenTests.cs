using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Semantic.Presentation;
using Xunit;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// What <see cref="ViewRenderer"/> gives for every view fixture, recorded in <c>conformance/view-rendered</c>. The browser renderer in
/// <c>DbDataKit/web/visuals</c> (plan 19, Y01 to Y03) takes exactly these documents as its input and its tests read these files, so a change to
/// the renderer's output fails here until the golden is rewritten (set <c>DBDATAKIT_UPDATE_GOLDENS=1</c> and run this class), and the web
/// tests then run against the new output.
/// </summary>
public class ViewRenderedGoldenTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "conformance", "view-fixtures");
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static IEnumerable<object[]> Files() =>
        Directory.GetFiles(Fixtures, "*.json").Order().Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Files))]
    public void Rendered_output_equals_its_golden(string file)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, file)))!.AsObject();
        var rendered = new ViewRenderer().Render(fixture["view"]!.AsObject(), fixture["result"]!.AsObject());
        var text = rendered.ToJsonString(Pretty).ReplaceLineEndings("\n") + "\n";

        if (Environment.GetEnvironmentVariable("DBDATAKIT_UPDATE_GOLDENS") == "1")
        {
            var directory = Path.Combine(SourceDirectory(), "conformance", "view-rendered");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, file), text);
            return;
        }

        var golden = Path.Combine(AppContext.BaseDirectory, "conformance", "view-rendered", file);
        Assert.True(File.Exists(golden), $"No golden for {file}: run this class with DBDATAKIT_UPDATE_GOLDENS=1 and commit conformance/view-rendered/{file}.");
        Assert.Equal(File.ReadAllText(golden).ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void Every_golden_has_a_fixture()
    {
        var goldens = Path.Combine(AppContext.BaseDirectory, "conformance", "view-rendered");
        var stale = Directory.Exists(goldens)
            ? Directory.GetFiles(goldens, "*.json").Select(Path.GetFileName).Where(n => !File.Exists(Path.Combine(Fixtures, n!))).ToList()
            : [];
        Assert.True(stale.Count == 0, "Goldens without a fixture: " + string.Join(", ", stale));
    }

    /// <summary>The test project's own folder, found from the build output, because a golden is written to the source tree.</summary>
    private static string SourceDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (directory.GetFiles("DbDataKit.Semantic.Tests.csproj").Length > 0) return directory.FullName;
        throw new InvalidOperationException("DbDataKit.Semantic.Tests.csproj not found above " + AppContext.BaseDirectory);
    }
}
