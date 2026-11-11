using DbDataKit.Semantic;
using Xunit;

namespace DbDataKit.Semantic.Tests;

/// <summary>The similarity function and normalization behind fuzzy value maps, pinned to published reference values.</summary>
public class FuzzyTests
{
    // Reference pairs from the Jaro-Winkler literature.
    [Theory]
    [InlineData("martha", "marhta", 0.9444, 0.9611)]
    [InlineData("dwayne", "duane", 0.8222, 0.84)]
    [InlineData("dixon", "dicksonx", 0.7667, 0.8133)]
    public void MatchesReferenceScores(string a, string b, double jaro, double jaroWinkler)
    {
        Assert.Equal(jaro, Fuzzy.Jaro(a, b), 4);
        Assert.Equal(jaroWinkler, Fuzzy.JaroWinkler(a, b), 4);
    }

    [Fact]
    public void IdenticalAndDisjointStrings()
    {
        Assert.Equal(1.0, Fuzzy.JaroWinkler("west", "west"));
        Assert.Equal(0.0, Fuzzy.JaroWinkler("abc", "xyz"));
        Assert.Equal(0.0, Fuzzy.JaroWinkler("", "west"));
    }

    [Fact]
    public void ScoreIsSymmetric() =>
        Assert.Equal(Fuzzy.JaroWinkler("wesst", "west"), Fuzzy.JaroWinkler("west", "wesst"), 12);

    [Theory]
    [InlineData("  West ", "west")]
    [InlineData("WEST-COAST", "west coast")]
    [InlineData("Sáo   Paulo", "sao paulo")]
    [InlineData("N.Y.", "ny")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, Fuzzy.Normalize(input));
}
