using DbDataKit.Apps.Bundling;

namespace DbDataKit.Apps.Bundling.Tests;

/// <summary>A Bun macro (an import <c>with { type: "macro" }</c>) runs its code while Bun bundles, so a build refuses a source that has one, naming the file and line, before Bun starts.</summary>
public sealed class BunMacrosTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bun-macros-" + Guid.NewGuid().ToString("N")[..8]);
    private string Source => Path.Combine(root, "src");

    public BunMacrosTests() => Directory.CreateDirectory(Path.Combine(Source, "src"));

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Theory]
    [InlineData("import { stamp } from './stamp.ts' with { type: 'macro' };", 1, "./stamp.ts")]
    [InlineData("import { stamp } from \"./stamp.ts\" with { type: \"macro\" };", 1, "./stamp.ts")]
    [InlineData("import { stamp } from './stamp.ts' assert { type: 'macro' };", 1, "./stamp.ts")]
    [InlineData("import './stamp.ts' with {type:'macro'};", 1, "./stamp.ts")]
    [InlineData("const a = 1;\nimport {\n  stamp,\n} from './stamp.ts'\n  with { type: 'macro' };", 5, "./stamp.ts")]
    [InlineData("import { stamp } from './stamp.ts' with { \"type\": \"macro\", other: 'x' };", 1, "./stamp.ts")]
    [InlineData("const m = await import('./stamp.ts', { with: { type: 'macro' } });", 1, "")]
    public void An_import_with_the_macro_attribute_is_found_with_its_line(string text, int line, string specifier)
    {
        var found = Assert.Single(BunMacros.Find("src/main.ts", text));
        Assert.Equal(("src/main.ts", line, specifier), (found.File, found.Line, found.Package));
        Assert.StartsWith($"src/main.ts:{line}: ", found.Message);
        Assert.Contains("Bun macro", found.Message);
    }

    [Theory]
    [InlineData("import { stamp } from './stamp.ts';")]
    [InlineData("import data from './data.json' with { type: 'json' };")]
    [InlineData("// import { stamp } from './stamp.ts' with { type: 'macro' };")]
    [InlineData("/*\n * import { stamp } from './stamp.ts' with { type: 'macro' };\n */")]
    [InlineData("const macro = 'type: macro';")]
    public void Other_imports_and_comments_are_not_macros(string text) => Assert.Empty(BunMacros.Find("src/main.ts", text));

    [Fact]
    public void The_source_folder_is_read_with_hidden_folders_and_without_node_modules()
    {
        Write("src/main.ts", "import { a } from './.lib/a.ts';\n");
        Write("src/.lib/a.ts", "\nimport { stamp } from '../stamp.ts' with { type: 'macro' };\nexport const a = stamp();\n");
        Write("node_modules/pkg/index.ts", "import { stamp } from './stamp.ts' with { type: 'macro' };\n");
        var found = Assert.Single(BunMacros.Find(Source));
        Assert.Equal(("src/.lib/a.ts", 2), (found.File, found.Line));
    }

    [Fact]
    public async Task A_build_refuses_a_macro_naming_the_file_and_line_and_does_not_start_bun()
    {
        Write("src/stamp.ts", "export const stamp = () => Date.now();\n");
        Write("src/main.ts", "import { stamp } from './stamp.ts' with { type: 'macro' };\ndocument.title = String(stamp());\n");
        // a bundler that does not exist: had the build started it, the result would say it could not be started
        var bun = Path.Combine(root, "no-such-bun");

        var result = await new BunAppBundler(bun).BundleAsync(new AppBundleRequest(Source, "src/main.ts", Path.Combine(root, "out")));

        Assert.False(result.Succeeded);
        var messages = result.Diagnostics.Select(d => d.Message).ToList();
        Assert.Equal("The source imports a Bun macro; nothing was built.", messages[0]);
        Assert.StartsWith("src/main.ts:1: imports './stamp.ts' with { type: \"macro\" }, a Bun macro.", messages[1]);
        Assert.DoesNotContain(messages, m => m.Contains("could not be started", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(root, "out")));
    }
}
