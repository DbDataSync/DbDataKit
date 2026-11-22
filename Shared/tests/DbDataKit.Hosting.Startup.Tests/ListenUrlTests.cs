using System.Net;

namespace DbDataKit.Hosting.Startup.Tests;

/// <summary>The <c>listen</c> setting: an address written the way Kestrel writes it, or a list of them, in a settings file and on the command line.</summary>
public sealed class ListenUrlTests : StartupTestBase
{
    [Theory]
    [InlineData("https://0.0.0.0:7820", "https", "0.0.0.0", 7820, true)]
    [InlineData("https://127.0.0.1:9443", "https", "127.0.0.1", 9443, false)]
    [InlineData("https://[::1]:9443", "https", "::1", 9443, false)]
    [InlineData("https://localhost:7800", "https", "localhost", 7800, false)]
    [InlineData("https://*:7841", "https", "*", 7841, true)]
    [InlineData("https://+:7841", "https", "+", 7841, true)]
    [InlineData("http://127.0.0.1:0", "http", "127.0.0.1", 0, false)]
    [InlineData("HTTPS://10.1.2.3:8080/", "https", "10.1.2.3", 8080, false)]
    public void An_address_is_read_with_its_scheme_host_and_port(string text, string scheme, string host, int port, bool every)
    {
        var url = ListenUrl.Parse(text);
        Assert.Equal(scheme, url.Scheme);
        Assert.Equal(host, url.Host);
        Assert.Equal(port, url.Port);
        Assert.Equal(every, url.IsEveryAddress);
    }

    [Theory]
    [InlineData("")]
    [InlineData("7820")]
    [InlineData("0.0.0.0:7820")]
    [InlineData("https://0.0.0.0")]
    [InlineData("https://0.0.0.0:eighty")]
    [InlineData("https://0.0.0.0:70000")]
    [InlineData("https://0.0.0.0:7820/api")]
    [InlineData("ftp://0.0.0.0:7820")]
    [InlineData("https://:7820")]
    [InlineData("https://[::1]7820")]
    public void A_text_that_is_not_an_address_with_a_scheme_and_a_port_says_why(string text)
    {
        Assert.False(ListenUrl.TryParse(text, out var url, out var problem));
        Assert.Null(url);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void An_address_is_written_back_the_way_it_was_read_and_binds_the_address_it_names()
    {
        Assert.Equal("https://[::1]:9443", ListenUrl.Parse("https://[::1]:9443").ToString());
        Assert.Equal(IPAddress.Loopback, ListenUrl.Parse("https://localhost:7800").Address);
        Assert.Equal(IPAddress.Any, ListenUrl.Parse("https://*:7841").Address);
        Assert.Equal(new Uri("https://localhost:7800"), ListenUrl.Parse("https://localhost:7800").ToUri());
        Assert.Equal("localhost", ListenUrl.Parse("https://0.0.0.0:7800").ToClientUri().Host);
    }

    [Fact]
    public void A_flag_is_one_address_or_several_separated_by_a_semicolon_or_a_comma()
    {
        Assert.Equal([new Uri("https://127.0.0.1:7841")], ListenPorts.ParseFlag("https://127.0.0.1:7841"));
        Assert.Equal([new Uri("https://127.0.0.1:7841"), new Uri("https://127.0.0.1:9443")], ListenPorts.ParseFlag("https://127.0.0.1:7841;https://127.0.0.1:9443"));
        Assert.Equal(2, ListenPorts.ParseFlag("https://127.0.0.1:7841, https://127.0.0.1:9443").Count);
        Assert.Throws<FormatException>(() => ListenPorts.ParseFlag("http://127.0.0.1:7841"));
        Assert.Throws<FormatException>(() => ListenPorts.ParseFlag("https://127.0.0.1:7841;7842"));
    }

    [Theory]
    [InlineData("listen: [https://127.0.0.1:7841, https://127.0.0.1:9443]\n")]
    [InlineData("listen:\n  - https://127.0.0.1:7841\n  - https://127.0.0.1:9443  # the second\n")]
    public void A_list_in_the_settings_file_is_read_in_order_and_its_first_address_is_the_one_reported(string text)
    {
        var file = Path.Combine(Data, "list.yaml");
        File.WriteAllText(file, text);
        var all = ListenPorts.ReadPinnedAll(file)!;
        Assert.Equal([new Uri("https://127.0.0.1:7841"), new Uri("https://127.0.0.1:9443")], all);
        Assert.Equal(7841, ListenPorts.ReadPinned(file)!.Port);

        var choice = ListenPorts.ResolveAll(SuiteServices.Runner, null, file, new Uri("https://127.0.0.1:7840"), []);
        Assert.Equal(ListenSource.Settings, choice.Source);
        Assert.Equal(all, choice.Addresses);
        Assert.Equal([7841, 9443], choice.Ports);
        Assert.Equal(7841, choice.Port);
    }

    [Theory]
    [InlineData("listen: { address: 127.0.0.1, port: 7841 }\n")]
    [InlineData("listen: 7841\n")]
    [InlineData("listen: [https://127.0.0.1:7841, 9443]\n")]
    [InlineData("listen: [https://127.0.0.1:7841\n")]
    public void A_listen_setting_that_is_not_an_address_or_a_list_of_them_fails_naming_the_file(string text)
    {
        var file = Path.Combine(Data, "bad.yaml");
        File.WriteAllText(file, text);
        var error = Assert.Throws<InvalidOperationException>(() => ListenPorts.ReadPinnedAll(file));
        Assert.Contains(file, error.Message);
        Assert.Contains("listen", error.Message);
    }

    [Fact]
    public void A_flag_beats_the_settings_file_and_nothing_is_chosen_or_written_for_a_list()
    {
        var file = Path.Combine(Data, "flagged.yaml");
        File.WriteAllText(file, "listen: https://127.0.0.1:7841\n");
        var choice = ListenPorts.ResolveAll(SuiteServices.Runner, [new Uri("https://127.0.0.1:9100"), new Uri("https://127.0.0.1:9101")], file, new Uri("https://127.0.0.1:7840"), []);
        Assert.Equal(ListenSource.Flag, choice.Source);
        Assert.Equal([9100, 9101], choice.Ports);
        Assert.Equal("listen: https://127.0.0.1:7841\n", File.ReadAllText(file));
    }
}
