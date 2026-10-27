using DbDataKit.Scripting;
using NuGet.Versioning;

namespace DbDataKit.Scripting.Tests;

public class DirectivesTests
{
    [Fact]
    public void Exact_directives_are_found_and_blanked_with_the_line_numbers_kept()
    {
        const string script = "#r \"nuget: Npgsql, 10.0.3\"\n#r \"System.Xml\"\nvar x = 1;\n#r \"nuget: Humanizer.Core, 2.14.1\"\nreturn x;\n";

        var (packages, source) = ScriptDirectives.Extract(script);

        Assert.Equal(["Npgsql 10.0.3", "Humanizer.Core 2.14.1"], packages.Select(p => $"{p.Id} {p.Range.MinVersion}"));
        Assert.Equal([1, 4], packages.Select(p => p.Line));
        Assert.DoesNotContain("nuget:", source);
        Assert.Contains("#r \"System.Xml\"", source);                                       // Roslyn's own directive is left alone
        Assert.Equal(script.Count(c => c == '\n'), source.Count(c => c == '\n'));
        Assert.All(packages, p => Assert.True(p.Range.Satisfies(p.Range.MinVersion!) && !p.Range.Satisfies(new NuGetVersion(99, 0, 0))));    // exact: [1.2.3], not "1.2.3 or newer"
    }

    [Fact]
    public void A_directive_in_a_comment_or_a_string_is_not_a_directive()
    {
        var (packages, source) = ScriptDirectives.Extract("// #r \"nuget: Evil, 1.0.0\"\nvar s = \"#r \\\"nuget: Evil, 1.0.0\\\"\";\n/* #r \"nuget: Evil, 1.0.0\" */\n");

        Assert.Empty(packages);
        Assert.Contains("Evil", source);
    }

    [Theory]
    [InlineData("#r \"nuget: Npgsql\"", "no version")]
    [InlineData("#r \"nuget: Npgsql, 10.*\"", "exact version")]
    [InlineData("#r \"nuget: Npgsql, [10.0,11.0)\"", "exact version")]
    [InlineData("#r \"nuget: Npgsql, $(Version)\"", "exact version")]
    [InlineData("#r \"nuget: Bad Id, 1.0.0\"", "not valid")]
    [InlineData("#r \"nuget: ../evil, 1.0.0\"", "not valid")]
    public void A_stored_script_names_exact_versions_and_a_refusal_says_which_line(string directive, string said)
    {
        var error = Assert.Throws<ScriptDirectiveException>(() => ScriptDirectives.Extract("var a = 1;\n" + directive + "\n"));

        Assert.Contains(said, error.Message);
        Assert.Equal(2, error.Line);
        Assert.Contains("script line 2", error.Message);
    }

    [Fact]
    public void Interactive_use_may_ask_for_the_newest_a_range_or_a_wildcard()
    {
        var (packages, _) = ScriptDirectives.Extract("#r \"nuget: A\"\n#r \"nuget: B, [1.0,2.0)\"\n#r \"nuget: C, 1.2.*\"\n", exactOnly: false);

        Assert.Equal(3, packages.Count);
        Assert.True(packages[0].Range.Satisfies(new NuGetVersion(5, 0, 0)));
        Assert.False(packages[1].Range.Satisfies(new NuGetVersion(2, 0, 0)));
        Assert.True(packages[2].Range.IsFloating);
        Assert.Throws<ScriptDirectiveException>(() => ScriptDirectives.Extract("#r \"nuget: C, not-a-version\"\n", exactOnly: false));
    }
}
