using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// The ports a further instance of a Gateway group takes from its group's first instance at its join: the three shared listeners (<c>public.listen</c>,
/// <c>listen</c>, <c>management.listen</c>) on the group's schemes and ports, each on this instance's own address (the listener's default address when it has
/// none), written to its <c>server.yaml</c> with the rest of the file kept; what a Gateway reports of its own when it joins.
/// </summary>
public sealed class GroupListenersTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("gateway-group-listen-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static readonly Dictionary<string, string[]> Group = new()
    {
        ["public.listen"] = ["https://0.0.0.0:7820"],
        ["listen"] = ["https://0.0.0.0:7821"],
        ["management.listen"] = ["https://127.0.0.1:7822"],
        ["apps.listen"] = ["https://0.0.0.0:7830"],
    };

    private static GatewayOptions Own(string? publicListen = null, string? listen = null, string? management = null)
    {
        var options = new GatewayOptions();
        options.Public.Listen = publicListen is null ? [] : [publicListen];
        options.Listen = listen is null ? [] : [listen];
        options.Management.Listen = management is null ? [] : [management];
        return options;
    }

    [Fact]
    public void A_new_instance_takes_the_group_ports_on_the_default_addresses_and_nothing_else()
    {
        var taken = GatewayListeners.FromGroup(Own(), Group).ToDictionary(t => t.Setting, t => t.Urls.Single().ToString());

        Assert.Equal(new Dictionary<string, string>
        {
            ["public.listen"] = "https://0.0.0.0:7820",
            ["listen"] = "https://0.0.0.0:7821",
            ["management.listen"] = "https://127.0.0.1:7822",
        }, taken);
    }

    [Fact]
    public void An_instance_keeps_its_own_address_and_takes_the_group_port()
    {
        var taken = GatewayListeners.FromGroup(Own("https://10.0.0.6:7825", "https://10.0.0.6:7826", "https://127.0.0.2:7827"), Group).ToDictionary(t => t.Setting, t => t.Urls.Single().ToString());

        Assert.Equal("https://10.0.0.6:7820", taken["public.listen"]);
        Assert.Equal("https://10.0.0.6:7821", taken["listen"]);
        Assert.Equal("https://127.0.0.2:7822", taken["management.listen"]);
    }

    [Fact]
    public void The_group_ports_are_written_to_server_yaml_and_say_whether_the_instance_must_start_again()
    {
        var file = Path.Combine(_folder, "server.yaml");
        File.WriteAllText(file, "# kept\nschemaVersion: 2\ntls: { mode: selfSigned }\npublic:\n  listen: https://127.0.0.2:7825\n");
        var own = Own("https://127.0.0.2:7825", "https://127.0.0.2:7826", "https://127.0.0.2:7827");

        Assert.True(GatewayListeners.TakeGroup(file, own, Group));

        var text = File.ReadAllText(file);
        Assert.StartsWith("# kept\nschemaVersion: 2\n", text);
        Assert.Contains("https://127.0.0.2:7820", text);
        Assert.Contains("https://127.0.0.2:7821", text);
        Assert.Contains("https://127.0.0.2:7822", text);
        Assert.DoesNotContain("7825", text);
        Assert.DoesNotContain("7830", text);
        // the instance already on the group's ports does not start again
        Assert.False(GatewayListeners.TakeGroup(file, Own("https://127.0.0.2:7820", "https://127.0.0.2:7821", "https://127.0.0.2:7822"), Group));
    }

    [Fact]
    public void A_list_of_addresses_is_written_as_a_list()
    {
        var file = Path.Combine(_folder, "server.yaml");
        File.WriteAllText(file, "schemaVersion: 2\n");

        GatewayListeners.TakeGroup(file, Own(), new Dictionary<string, string[]> { ["listen"] = ["https://0.0.0.0:7821", "https://0.0.0.0:9443"] });

        Assert.Contains("[\"https://0.0.0.0:7821\", \"https://0.0.0.0:9443\"]", File.ReadAllText(file));
    }

    [Fact]
    public void A_gateway_reports_its_three_shared_listeners()
    {
        var reported = GatewayListeners.Reported(Own("https://0.0.0.0:7820", "https://0.0.0.0:7821", "https://127.0.0.1:7822"));

        Assert.Equal(["https://0.0.0.0:7820"], reported["public.listen"]);
        Assert.Equal(["https://0.0.0.0:7821"], reported["listen"]);
        Assert.Equal(["https://127.0.0.1:7822"], reported["management.listen"]);
        Assert.Equal(3, reported.Count);
    }
}
