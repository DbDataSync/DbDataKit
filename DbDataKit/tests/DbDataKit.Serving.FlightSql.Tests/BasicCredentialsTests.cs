namespace DbDataKit.Serving.FlightSql.Tests;

public class BasicHeaderTests
{
    [Fact]
    public void Parses_basic_header_with_colon_in_password()
    {
        string h = "Basic " + Convert.ToBase64String("bob:pa:ss"u8.ToArray());
        Assert.True(BasicCredentials.TryParseBasic(h, out var u, out var p));
        Assert.Equal(("bob", "pa:ss"), (u, p));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer abc")]
    [InlineData("Basic !!!")]
    [InlineData("Basic Ym9i")] // "bob" with no colon
    public void Rejects_bad_basic_headers(string? header)
    {
        Assert.False(BasicCredentials.TryParseBasic(header, out _, out _));
    }
}

public class UnpaddedBasicHeaderTests
{
    [Theory]
    [InlineData("py:interop-pw")]   // 13 bytes: standard base64 needs padding
    [InlineData("bob:secret")]       // 10 bytes
    [InlineData("ab:cd")]            // 5 bytes
    [InlineData("abc:def")]          // 7 bytes
    public void Accepts_basic_credentials_with_or_without_base64_padding(string credentials)
    {
        string padded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(credentials));
        foreach (string encoded in new[] { padded, padded.TrimEnd('=') })
        {
            Assert.True(BasicCredentials.TryParseBasic("Basic " + encoded, out var user, out var password));
            Assert.Equal(credentials, user + ":" + password);
        }
    }
}
