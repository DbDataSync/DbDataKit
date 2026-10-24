using System.Text.RegularExpressions;
using DbDataProxy.Server.Auth;

namespace DbDataProxy.Tests.Auth;

/// <summary>
/// Pins the stored password format. The golden hashes were produced by the previous, Proxy-owned
/// <c>PasswordHasher</c> (<c>dbdataproxy hash-password</c> before the shared hasher was adopted); the shared
/// <c>DbDataKit.Secrets.PasswordHasher</c> behind the adapter must keep verifying them.
/// </summary>
public class PasswordHasherCompatibilityTests
{
    public static TheoryData<string, string> Golden => new()
    {
        { "correct horse battery staple", "PBKDF2-SHA256$210000$+BiI5/AI5LyxbW1TKKS6fg==$VmWzF6naQy8dik3RkJ6ju4HsFDrlTF7H4t2mFwdliTY=" },
        { "pässwörd-ünïcode", "PBKDF2-SHA256$210000$BnxtNbERZttT5RhvyRjD2Q==$IBG+H+8FenUCMOhKvwBlKg0dak1KKeEx9MRRjDxz6go=" },
        { "sentinel", "PBKDF2-SHA256$210000$oYgDatniy3iN2QyncDQM+A==$G17882GfopcdNYq3RHR6F2RGY/8vcbfvB1I2WP3wxPo=" },
    };

    [Theory]
    [MemberData(nameof(Golden))]
    public void Hashes_made_by_the_previous_implementation_still_verify(string password, string stored)
    {
        Assert.True(PasswordHasher.Verify(password, stored));
        Assert.True(DbDataKit.Secrets.PasswordHasher.Verify(password, stored));
        Assert.False(PasswordHasher.Verify(password + "x", stored));
    }

    [Fact]
    public void Output_format_is_unchanged()
    {
        string hash = PasswordHasher.Hash("pw");
        Assert.Matches(new Regex(@"^PBKDF2-SHA256\$210000\$[A-Za-z0-9+/]{22}==\$[A-Za-z0-9+/]{43}=$"), hash);
        Assert.True(PasswordHasher.Verify("pw", hash));
        Assert.False(PasswordHasher.Verify("other", hash));
        Assert.NotEqual(hash, PasswordHasher.Hash("pw")); // random salt
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("PBKDF2-SHA256$0$AA==$AA==")]
    [InlineData("PBKDF2-SHA256$1000$not-base64$AA==")]
    [InlineData("MD5$1000$AA==$AA==")]
    public void Malformed_stored_values_never_verify(string stored) => Assert.False(PasswordHasher.Verify("pw", stored));
}
