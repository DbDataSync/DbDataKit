using Xunit;

namespace DbDataKit.Secrets.Tests;

public class SecretRefTests
{
    [Theory]
    [InlineData("secret:dbdata:build:connection:wh:write", SecretRefKind.Secret, "dbdata:build:connection:wh:write")]
    [InlineData("env:MY_VAR", SecretRefKind.Env, "MY_VAR")]
    [InlineData("file:/run/secrets/x", SecretRefKind.File, "/run/secrets/x")]
    [InlineData("file:C:\\s\\x.txt", SecretRefKind.File, "C:\\s\\x.txt")]
    [InlineData("keyring:svc/item", SecretRefKind.Keyring, "svc/item")]
    [InlineData("dpapi:AQAAANCM", SecretRefKind.Dpapi, "AQAAANCM")]
    [InlineData("  ENV:Upper ", SecretRefKind.Env, "Upper")]
    public void Parses(string text, SecretRefKind kind, string target)
    {
        var r = SecretRef.Parse(text);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(target, r.Target);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("env:")]
    [InlineData("keyring:noslash")]
    [InlineData("keyring:/x")]
    [InlineData("keyring:x/")]
    [InlineData("azkv:vault/x")]
    public void Rejects(string text)
    {
        Assert.False(SecretRef.TryParse(text, out _, out var error));
        Assert.NotNull(error);
        Assert.Throws<FormatException>(() => SecretRef.Parse(text));
    }

    [Fact]
    public void KeyringParts_RoundTrip_AndAuditElidesBlob()
    {
        var k = SecretRef.Parse("keyring:svc/a/b");
        Assert.Equal("svc", k.Service);
        Assert.Equal("a/b", k.Item);
        Assert.Equal("keyring:svc/a/b", k.ToString());
        Assert.Equal("dpapi:<blob>", SecretRef.Parse("dpapi:AAAA").ToAuditString());
    }
}

public class SecretNameTests
{
    [Fact]
    public void RoundTrips_WithAndWithoutPart()
    {
        var n = SecretName.Parse("dbdata:build:connection:Warehouse:write");
        Assert.Equal("build", n.Product);
        Assert.Equal("warehouse", n.Name);
        Assert.Equal("write", n.Part);
        Assert.Equal("dbdata:build:connection:warehouse:write", n.ToString());
        Assert.Equal("dbdata:sync:connection:erp", SecretName.Parse("dbdata:sync:connection:erp").ToString());
        Assert.Equal(SecretRefKind.Secret, n.ToRef().Kind);
    }

    [Theory]
    [InlineData("dbdatasync:connection:erp")]
    [InlineData("dbdata:a:b")]
    [InlineData("dbdata:a:b:c:d:e")]
    [InlineData("dbdata:a:b:c d")]
    [InlineData("dbdata:a::c")]
    [InlineData("x:a:b:c")]
    public void RejectsMalformed(string text) => Assert.False(SecretName.TryParse(text, out _));
}

public class SecretValueTests
{
    [Fact]
    public void ToString_NeverReveals()
    {
        using var v = SecretValue.From("hunter2");
        Assert.Equal("***", v.ToString());
        Assert.Equal("***", $"{v}");
        Assert.Equal("hunter2", v.RevealString());
        Assert.Equal(7, v.Length);
    }

    [Fact]
    public void Dispose_ZeroesBuffer_AndBlocksReveal()
    {
        var v = SecretValue.From("hunter2");
        var span = v.Reveal();
        // Capture the underlying buffer through the span's backing array before disposal.
        var copy = span.ToArray();
        Assert.Equal("hunter2", new string(copy));
        v.Dispose();
        Assert.Throws<ObjectDisposedException>(() => v.Reveal());
        Assert.Equal(0, v.Length);
        v.Dispose(); // idempotent
    }

    [Fact]
    public void Dispose_ClearsTheActualChars()
    {
        var buffer = "abc".ToCharArray();
        var v = new SecretValue(buffer);
        buffer[0] = 'z'; // value was copied, original unaffected by us and vice versa
        Assert.Equal("abc", v.RevealString());
        var field = typeof(SecretValue).GetField("_chars", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var inner = (char[])field.GetValue(v)!;
        v.Dispose();
        Assert.All(inner, c => Assert.Equal('\0', c));
    }
}

public class CredentialKeyDetectorTests
{
    [Theory]
    [InlineData("Server=a;Database=b;Password=p")]
    [InlineData("Server=a;Pwd=p;")]
    [InlineData("Server=a;User Id=u;Pass Word=p")]
    [InlineData("Host=a;passwd=p")]
    [InlineData("AccountName=x;AccountKey=abc==")]
    [InlineData("Endpoint=sb://x;SharedAccessKey=abc")]
    [InlineData("BlobEndpoint=x;SAS=abc")]
    [InlineData("Url=x;ApiKey=k")]
    [InlineData("Url=x;Token=t")]
    [InlineData("Url=x;PAT=t")]
    [InlineData("Server=a;Password=\"p;with;semi\"")]
    [InlineData("Server=a;Password='p'")]
    [InlineData("Driver={X};Pwd={p;q}")]
    [InlineData("Server=a;Client_Secret=zz")]
    [InlineData("postgres://user:pw@host/db")]
    [InlineData("Server=a;Authentication=Active Directory Password;User=u")]
    public void FlagsConnectionStrings(string cs)
    {
        Assert.True(CredentialKeyDetector.ContainsCredential(cs));
        Assert.NotEmpty(CredentialKeyDetector.FindInConnectionString(cs));
    }

    [Theory]
    [InlineData("Server=a;Database=PasswordVault;Integrated Security=true")]
    [InlineData("Server=a;Persist Security Info=false")]
    [InlineData("Server=a;Password=")]
    [InlineData("Server=a;Password=env:DB_PWD")]
    [InlineData("Server=a;Authentication=Active Directory Integrated")]
    [InlineData("Dsn=Secrets;Uid=u")]
    [InlineData("postgres://host/db")]
    [InlineData("")]
    public void DoesNotFlag(string cs) => Assert.False(CredentialKeyDetector.ContainsCredential(cs));

    [Fact]
    public void References_CanBeDisallowed()
    {
        Assert.True(CredentialKeyDetector.ContainsCredential("Password=env:X", allowReferences: false));
    }

    [Fact]
    public void FindingNeverContainsValue()
    {
        var f = Assert.Single(CredentialKeyDetector.FindInConnectionString("Server=a;Password=sup3rs3cret"));
        Assert.Equal("Password", f.Key);
        Assert.DoesNotContain("sup3rs3cret", f.ToString());
    }

    [Fact]
    public void PropertyBags_FlagCredentialKeys_AndAllowRefsAndRefKeys()
    {
        var bag = new Dictionary<string, string?>
        {
            ["DbPassword"] = "plain",
            ["ApiKey"] = "k",
            ["ProxyToken"] = "t",
            ["PasswordSecret"] = "secret:dbdata:sync:connection:erp",
            ["PasswordRef"] = "x",
            ["Timeout"] = "30",
            ["Empty"] = "",
            ["Nested"] = "Server=a;Pwd=zzz",
            ["ClientSecret"] = "env:CS",
        };
        var keys = CredentialKeyDetector.FindInProperties(bag).Select(f => f.Key).OrderBy(k => k).ToArray();
        Assert.Equal(["ApiKey", "DbPassword", "ProxyToken", "Pwd"], keys);
    }

    [Theory]
    [InlineData("MaxTokens", false)]
    [InlineData("tokenCommand", false)]
    [InlineData("Password", true)]
    [InlineData("pwd", true)]
    [InlineData("account_key", true)]
    public void PropertyKey_Rules(string key, bool expected) =>
        Assert.Equal(expected, CredentialKeyDetector.IsCredentialPropertyKey(key));
}
