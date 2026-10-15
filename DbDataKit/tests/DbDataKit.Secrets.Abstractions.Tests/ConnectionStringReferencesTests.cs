using Xunit;

namespace DbDataKit.Secrets.Tests;

public class ConnectionStringReferencesTests
{
    private sealed class MapResolver(Dictionary<string, string> map) : ISecretResolver
    {
        public ValueTask<SecretValue> ResolveAsync(SecretRef reference, CancellationToken ct = default) =>
            map.TryGetValue(reference.ToString(), out var v)
                ? ValueTask.FromResult(SecretValue.From(v))
                : throw new SecretNotFoundException(reference.ToAuditString());
    }

    private static readonly MapResolver Resolver = new(new()
    {
        ["env:PW"] = "s3cret",
        ["env:ODD"] = "a;b=c}d",
        ["env:DQ"] = "x\"y;z",
        ["secret:dbdata:proxy:datasource:erp"] = "Server=a;Password=whole",
    });

    [Fact]
    public async Task PlainStringIsReturnedUnchanged()
    {
        const string cs = "Server=a;Password=plain;Database=d";
        Assert.Equal(cs, await ConnectionStringReferences.ResolveAsync(cs, Resolver));
    }

    [Fact]
    public async Task WholeStringReferenceIsResolved()
    {
        Assert.Equal("Server=a;Password=whole",
            await ConnectionStringReferences.ResolveAsync("secret:dbdata:proxy:datasource:erp", Resolver));
    }

    [Theory]
    [InlineData("Server=a;Password=env:PW;Database=d", "Server=a;Password=s3cret;Database=d")]
    [InlineData("Server=a;PWD=env:PW", "Server=a;PWD=s3cret")]
    [InlineData("Server=a;Password = env:PW ;Database=d", "Server=a;Password = s3cret;Database=d")]
    [InlineData("Server=a;Password={env:PW};Database=d", "Server=a;Password=s3cret;Database=d")]
    public async Task PasswordPartIsReplaced(string input, string expected)
    {
        var actual = await ConnectionStringReferences.ResolveAsync(input, Resolver);
        Assert.Equal(expected.Replace(" ;", ";"), actual.Replace(" ;", ";"));
    }

    [Fact]
    public async Task ValuesNeedingQuotesAreQuotedPerDialect()
    {
        Assert.Equal("A=1;Password={a;b=c}}d};B=2",
            await ConnectionStringReferences.ResolveAsync("A=1;Password=env:ODD;B=2", Resolver));
        Assert.Equal("A=1;Password=\"x\"\"y;z\";B=2",
            await ConnectionStringReferences.ResolveAsync("A=1;Password=env:DQ;B=2", Resolver, ConnectionStringReferences.QuoteDouble));
    }

    [Fact]
    public async Task NonCredentialKeysAreNotTouched()
    {
        const string cs = "Server=env:PW;Password=env:PW";
        Assert.Equal("Server=env:PW;Password=s3cret", await ConnectionStringReferences.ResolveAsync(cs, Resolver));
    }

    [Fact]
    public async Task MissingReferenceThrowsWithoutValue()
    {
        var ex = await Assert.ThrowsAsync<SecretNotFoundException>(async () =>
            await ConnectionStringReferences.ResolveAsync("Password=env:NOPE", Resolver));
        Assert.Equal("env:NOPE", ex.Reference);
    }

    [Fact]
    public void FindListsReferencesOnly()
    {
        var found = ConnectionStringReferences.Find("Server=a;Password=env:PW;Pwd=literal");
        var one = Assert.Single(found);
        Assert.Equal("Password", one.Key);
        Assert.Equal(SecretRefKind.Env, one.Reference.Kind);
    }
}
