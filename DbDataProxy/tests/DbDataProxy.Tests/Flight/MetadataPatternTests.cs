using System.Diagnostics;
using DbDataProxy.Data;
using DbDataProxy.Tests.Support;

namespace DbDataProxy.Tests.Flight;

/// <summary>Table-name patterns are matched by the shared linear LIKE matcher: backslash escapes, no regular expressions, no backtracking.</summary>
public class MetadataPatternTests(ProxyFixture proxy) : IClassFixture<ProxyFixture>
{
    private async Task<List<string?>> TableNamesAsync(string pattern, string? schemaPattern = null)
    {
        using var client = proxy.Connect();
        await client.AuthenticateAsync("alice", ProxyFixture.AlicePassword);
        var names = new List<string?>();
        await foreach (var batch in await client.Raw.GetTablesAsync(schemaPattern: schemaPattern, tablePattern: pattern))
            for (int i = 0; i < batch.Length; i++) names.Add(ArrowText.Get(batch.Column("table_name"), i));
        return names;
    }

    [Fact]
    public async Task Underscore_and_percent_are_wildcards_and_backslash_makes_them_literal()
    {
        Assert.Equal(["prices", "scaled"], (await TableNamesAsync("%c%")).Order().Where(n => n is "prices" or "scaled"));
        Assert.Contains("prices", await TableNamesAsync("pric_s"));
        Assert.DoesNotContain("prices", await TableNamesAsync("pric\\_s"));   // a literal underscore matches no table here
        Assert.Contains("prices", await TableNamesAsync("PRICES"));          // case-insensitive, as before
    }

    [Fact]
    public async Task A_hostile_pattern_is_answered_at_once()
    {
        // Nested wildcards before a character that never matches make a backtracking matcher run for ages.
        string hostile = string.Concat(Enumerable.Repeat("%a", 40)) + "b";
        var sw = Stopwatch.StartNew();
        Assert.Empty(await TableNamesAsync(hostile));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }
}
