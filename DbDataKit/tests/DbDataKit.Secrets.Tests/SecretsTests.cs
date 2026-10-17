using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DbDataKit.Secrets.Tests;

public class ProviderTests
{
    [Fact]
    public void VariableNameFor_MatchesTheDefaultMapping() =>
        Assert.Equal("DBDATA_SECRET_DBDATA_PROXY_DATASOURCE_ERP", EnvSecretProvider.VariableNameFor("dbdata:proxy:datasource:erp"));

    private static Func<string, string?> Env(params (string, string)[] vars)
    {
        var d = vars.ToDictionary(v => v.Item1, v => v.Item2);
        return k => d.GetValueOrDefault(k);
    }

    [Fact]
    public async Task Env_ResolvesEnvRef_AndSecretViaDefaultMapping()
    {
        var p = new EnvSecretProvider(Env(("MY_VAR", "v1"), ("DBDATA_SECRET_DBDATA_SYNC_CONNECTION_ERP", "v2")));
        Assert.Equal("v1", (await p.TryResolveAsync(SecretRef.Parse("env:MY_VAR")))!.RevealString());
        Assert.Equal("v2", (await p.TryResolveAsync(SecretRef.Parse("secret:dbdata:sync:connection:erp")))!.RevealString());
        Assert.Null(await p.TryResolveAsync(SecretRef.Parse("env:NOPE")));
        Assert.False(p.CanHandle(SecretRef.Parse("file:/x")));
    }

    [Fact]
    public void Env_WithoutPrefix_DoesNotHandleSecretRefs() =>
        Assert.False(new EnvSecretProvider(secretPrefix: null).CanHandle(SecretRef.Parse("secret:x")));

    [Fact]
    public async Task Legacy_Build_MapsConnectionReadWrite_NoFallbackBetweenThem()
    {
        var p = new LegacyEnvProvider(Env(("DBDATABUILD_WAREHOUSE_WRITE", "Server=w")));
        Assert.Equal("Server=w", (await p.TryResolveAsync(SecretRef.Parse("secret:dbdata:build:connection:warehouse:write")))!.RevealString());
        Assert.Null(await p.TryResolveAsync(SecretRef.Parse("secret:dbdata:build:connection:warehouse:read")));
    }

    [Fact]
    public async Task Legacy_Sync_MapsRefToSecretEnv()
    {
        var p = new LegacyEnvProvider(Env(("DBDATASYNC_SECRET_DBDATASYNC_CONNECTION_ERP", "pw")));
        Assert.Equal("pw", (await p.TryResolveAsync(SecretRef.Parse("secret:dbdatasync:connection:erp")))!.RevealString());
    }

    [Fact]
    public async Task Legacy_PrefixAndRulesAreConfigurable()
    {
        var p = new LegacyEnvProvider(Env(("ACME_ORDERS_READ", "x"), ("CUSTOM_ONE", "y")),
            [LegacyEnvProvider.BuildRule("ACME"), t => t == "secret-one" ? "CUSTOM_ONE" : null]);
        Assert.Equal("x", (await p.TryResolveAsync(SecretRef.Parse("secret:dbdata:build:connection:orders:read")))!.RevealString());
        Assert.Equal("y", (await p.TryResolveAsync(SecretRef.Parse("secret:secret-one")))!.RevealString());
    }

    [Fact]
    public async Task File_TrimsContents_MissingIsNull_OversizeThrows()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var f = Path.Combine(dir.FullName, "s");
            await File.WriteAllTextAsync(f, "  pw\r\n");
            var p = new FileSecretProvider(new FileSecretOptions { MaxBytes = 16 });
            Assert.Equal("pw", (await p.TryResolveAsync(SecretRef.Parse("file:" + f)))!.RevealString());
            Assert.Null(await p.TryResolveAsync(SecretRef.Parse("file:" + f + ".missing")));

            var big = Path.Combine(dir.FullName, "big");
            await File.WriteAllTextAsync(big, new string('x', 100));
            await Assert.ThrowsAsync<InvalidDataException>(async () => await p.TryResolveAsync(SecretRef.Parse("file:" + big)));

            var empty = Path.Combine(dir.FullName, "empty");
            await File.WriteAllTextAsync(empty, " \n");
            Assert.Null(await p.TryResolveAsync(SecretRef.Parse("file:" + empty)));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task File_SecretsDirectory_ResolvesNames_AndBlocksTraversal()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "dbdata_proxy_datasource_erp"), "pw\n");
            var p = new FileSecretProvider(new FileSecretOptions { SecretsDirectory = dir.FullName });
            Assert.Equal("pw", (await p.TryResolveAsync(SecretRef.Parse("secret:dbdata:proxy:datasource:erp")))!.RevealString());
            Assert.Null(await p.TryResolveAsync(SecretRef.Parse("secret:../../etc/passwd")));
        }
        finally { dir.Delete(true); }
    }

    private sealed class FakeProvider(string name, SecretRefKind kind, string? value) : ISecretProvider
    {
        public int Calls;
        public string Name => name;
        public bool CanHandle(SecretRef r) => r.Kind == kind;
        public ValueTask<SecretValue?> TryResolveAsync(SecretRef r, CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult(value is null ? null : SecretValue.From(value));
        }
    }

    private sealed class Audit : ISecretAudit
    {
        public readonly List<string> Lines = [];
        public void Resolved(string reference, string? provider, bool found) => Lines.Add($"{reference}|{provider}|{found}");
    }

    [Fact]
    public async Task Chain_FirstNonNullWins_SkipsNonHandlers_AuditsNamesOnly_RegistersRedactor()
    {
        var a = new FakeProvider("a", SecretRefKind.Secret, null);
        var b = new FakeProvider("b", SecretRefKind.Secret, "topsecret");
        var c = new FakeProvider("c", SecretRefKind.Secret, "later");
        var other = new FakeProvider("o", SecretRefKind.Env, "no");
        var audit = new Audit();
        var redactor = new Redactor();
        var chain = new SecretResolverChain([other, a, b, c], audit, redactor);

        using var v = await chain.ResolveAsync(SecretRef.Parse("secret:dbdata:x:y:z"));
        Assert.Equal("topsecret", v.RevealString());
        Assert.Equal(0, other.Calls);
        Assert.Equal(0, c.Calls);
        Assert.Equal(["secret:dbdata:x:y:z|b|True"], audit.Lines);
        Assert.DoesNotContain("topsecret", string.Concat(audit.Lines));
        Assert.Equal("pw=***", redactor.Redact("pw=topsecret"));
    }

    [Fact]
    public async Task Chain_NotFound_ThrowsAndAudits_WithoutDpapiBlob()
    {
        var audit = new Audit();
        var chain = new SecretResolverChain([new FakeProvider("a", SecretRefKind.Dpapi, null)], audit);
        var ex = await Assert.ThrowsAsync<SecretNotFoundException>(async () => await chain.ResolveAsync(SecretRef.Parse("dpapi:BLOBDATA")));
        Assert.DoesNotContain("BLOBDATA", ex.Message);
        Assert.Equal(["dpapi:<blob>|<null>|False".Replace("<null>", "")], audit.Lines);
    }

    [Fact]
    public async Task Chain_NoProviderForKind_NotFound()
    {
        var chain = new SecretResolverChain([]);
        await Assert.ThrowsAsync<SecretNotFoundException>(async () => await chain.ResolveAsync(SecretRef.Parse("env:X")));
    }

    [Fact]
    public void DelegateAudit_WritesNamesOnly()
    {
        var lines = new List<string>();
        var a = new DelegateSecretAudit(lines.Add);
        a.Resolved("env:X", "env", true);
        a.Resolved("env:Y", null, false);
        Assert.Equal(["secret resolved: env:X via env", "secret NOT FOUND: env:Y"], lines);
    }
}

public class RedactorTests
{
    private const string Pw = "p@ss w/rd+\"q\"&=é";

    [Fact]
    public void ExactValue_AndAllEncodings_AreScrubbed()
    {
        var r = new Redactor();
        r.Register(SecretValue.From(Pw));
        var variants = new[]
        {
            Pw,
            Uri.EscapeDataString(Pw),
            WebUtility.UrlEncode(Pw),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Pw)),
            JsonSerializer.Serialize(Pw).Trim('"'),
        };
        foreach (var v in variants)
        {
            var text = $"failed to connect with [{v}] today";
            var redacted = r.Redact(text);
            Assert.DoesNotContain(v, redacted);
            Assert.Contains("***", redacted);
        }
    }

    [Fact]
    public void LongestFirst_AndShortValuesIgnored()
    {
        var r = new Redactor();
        r.Register("abcd");
        r.Register("abcdef");
        r.Register("ab"); // below MinLength
        Assert.Equal("*** and ab", r.Redact("abcdef and ab"));
    }

    [Theory]
    [InlineData("Server=a;Password=hunter2;Database=b", "Server=a;Password=***;Database=b")]
    [InlineData("Server=a;PWD = hunter2", "Server=a;PWD = ***")]
    [InlineData("Server=a;Password=\"h;un\"ter;Db=b", "Server=a;Password=***ter;Db=b")]
    [InlineData("x;Account Key=abc==;y", "x;Account Key=***;y")]
    [InlineData("conn postgres://bob:hunter2@host:5432/db failed", "conn postgres://bob:***@host:5432/db failed")]
    [InlineData("Server=a;Database=PasswordVault", "Server=a;Database=PasswordVault")]
    public void ConnectionStringFragments_AreScrubbedWithoutRegistration(string input, string expected) =>
        Assert.Equal(expected, new Redactor().Redact(input));

    [Fact]
    public void Redact_EmptyAndNull_Safe()
    {
        var r = new Redactor();
        Assert.Equal("", r.Redact(""));
        r.Register("");
        Assert.Equal("plain", r.Redact("plain"));
    }

    [Fact]
    public void Fuzz_RandomSecrets_NeverLeakInAnyVariant()
    {
        var rnd = new Random(1234);
        const string alphabet = "abcXYZ019 !@#$%^&*()_+-=[]{};:'\",.<>/?\\|`~é€";
        for (var i = 0; i < 300; i++)
        {
            var secret = new string(Enumerable.Range(0, rnd.Next(6, 24)).Select(_ => alphabet[rnd.Next(alphabet.Length)]).ToArray()).Trim();
            if (secret.Length < 6) continue;
            var r = new Redactor();
            r.Register(secret);
            var forms = new[]
            {
                secret, Uri.EscapeDataString(secret), WebUtility.UrlEncode(secret),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), JsonSerializer.Serialize(secret).Trim('"'),
            };
            foreach (var f in forms)
                Assert.DoesNotContain(f, r.Redact($"prefix {f} suffix {f}"));
        }
    }

    [Fact]
    public void ThreadSafe_ConcurrentRegisterAndRedact()
    {
        var r = new Redactor();
        Parallel.For(0, 200, i =>
        {
            r.Register($"secret-{i:D4}");
            _ = r.Redact($"x secret-{i:D4} y");
        });
        Assert.Equal("x *** y", r.Redact("x secret-0042 y"));
    }
}

public class PasswordHasherTests
{
    [Fact]
    public void Hash_Format_AndVerify()
    {
        var h = PasswordHasher.Hash("correct horse", 1000);
        var parts = h.Split('$');
        Assert.Equal(4, parts.Length);
        Assert.Equal("PBKDF2-SHA256", parts[0]);
        Assert.Equal("1000", parts[1]);
        Assert.Equal(16, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[3]).Length);
        Assert.True(PasswordHasher.Verify("correct horse", h));
        Assert.False(PasswordHasher.Verify("wrong", h));
        Assert.NotEqual(h, PasswordHasher.Hash("correct horse", 1000)); // random salt
    }

    [Fact]
    public void Verify_KnownProxyVector_AndMalformedInputs()
    {
        // Computed independently with the same algorithm: salt = 16 zero bytes, 1000 iterations.
        var salt = new byte[16];
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2("pw", salt, 1000,
            System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        var stored = $"PBKDF2-SHA256$1000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        Assert.True(PasswordHasher.Verify("pw", stored));
        foreach (var bad in new[] { "", "x", "PBKDF2-SHA256$1$2", "PBKDF2-SHA1$1000$AA==$AA==", "PBKDF2-SHA256$0$AA==$AA==",
                     "PBKDF2-SHA256$abc$AA==$AA==", "PBKDF2-SHA256$1000$!!$AA==", "PBKDF2-SHA256$1000$$" })
            Assert.False(PasswordHasher.Verify("pw", bad));
    }

    [Fact]
    public void NeedsRehash_WhenIterationsLow()
    {
        Assert.True(PasswordHasher.NeedsRehash(PasswordHasher.Hash("a", 1000)));
        Assert.False(PasswordHasher.NeedsRehash(PasswordHasher.Hash("a", 1000), 1000));
        Assert.True(PasswordHasher.NeedsRehash("garbage"));
    }
}
