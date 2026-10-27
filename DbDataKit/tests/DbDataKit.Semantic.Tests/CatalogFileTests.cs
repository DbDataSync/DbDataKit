namespace DbDataKit.Semantic.Tests;

/// <summary>A catalog that cannot be read fails naming the file and the line (J04): the suite's strict YAML reader, not a parser's own message.</summary>
public sealed class CatalogFileTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dbdatakit-catalog-");

    public void Dispose() => _dir.Delete(true);

    private string Write(string yaml)
    {
        var path = Path.Combine(_dir.FullName, "sales.catalog.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private static string Sample => File.ReadAllText(Path.Combine(ConformanceTests.Root, "catalog", "catalog.yaml"));

    [Fact]
    public void ASyntaxErrorNamesTheFileAndTheLine()
    {
        var path = Write("name: sales\nsources:\n  a: [unclosed\n");
        var e = Assert.Throws<CatalogException>(() => CatalogLoader.Load(path));
        var error = Assert.Single(e.Errors);
        Assert.Equal("catalog_yaml_invalid", error.Code);
        Assert.Contains($"{path}:", error.Message);
        Assert.Matches(@"sales\.catalog\.yaml:\d+:\d+: error", error.Message);
    }

    [Fact]
    public void ADuplicateKeyNamesTheSecondOne()
    {
        var path = Write("name: sales\nname: again\n");
        var e = Assert.Throws<CatalogException>(() => CatalogLoader.Load(path));
        Assert.Contains("sales.catalog.yaml:2:", Assert.Single(e.Errors).Message);
    }

    [Fact]
    public void AnAnchorIsRefusedRatherThanReadSilently()
    {
        var path = Write("name: &n sales\n");
        var e = Assert.Throws<CatalogException>(() => CatalogLoader.Load(path));
        Assert.Contains("sales.catalog.yaml:1:", e.Errors[0].Message);
    }

    [Fact]
    public void AnEmptyFileSaysSo()
    {
        var e = Assert.Throws<CatalogException>(() => CatalogLoader.Load(Write("")));
        Assert.Contains("is empty", Assert.Single(e.Errors).Message);
    }

    [Fact]
    public void AFailedCheckPointsAtTheLineOfWhatItFound()
    {
        // an unknown aggregate on a measure: the schema check fails, and the message starts with the line where that measure begins
        var text = Sample;
        var lines = text.Split('\n').ToList();
        int at = lines.FindIndex(l => l.TrimStart().StartsWith("agg: sum", StringComparison.Ordinal));
        Assert.True(at > 0, "the sample catalog has a measure with agg: sum");
        lines[at] = lines[at].Replace("agg: sum", "agg: median");
        var path = Write(string.Join('\n', lines));
        var e = Assert.Throws<CatalogException>(() => CatalogLoader.Load(path));
        Assert.All(e.Errors, x => Assert.Matches(@"sales\.catalog\.yaml:\d+:\d+: ", x.Message));
        int indent = lines[at].Length - lines[at].TrimStart().Length;
        int measure = Enumerable.Range(0, at).Last(i => lines[i].Trim().Length > 0 && lines[i].Length - lines[i].TrimStart().Length < indent);
        Assert.Contains(e.Errors, x => x.Message.Contains($"sales.catalog.yaml:{measure + 1}:"));
    }

    [Fact]
    public void ATextWithoutAFileKeepsTheOldMessages()
    {
        var e = Assert.Throws<CatalogException>(() => CatalogLoader.LoadText("name: [unclosed\n"));
        Assert.StartsWith("catalog:", Assert.Single(e.Errors).Message);
    }
}
