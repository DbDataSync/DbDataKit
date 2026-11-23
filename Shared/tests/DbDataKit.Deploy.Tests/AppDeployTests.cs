using System.Text.Json.Nodes;

namespace DbDataKit.Deploy.Tests;

public sealed class AppDeployTests
{
    private static readonly DeployTarget Gateway = new("edge", "https://edge:8443", "g1", DeployProducts.Gateway);
    private static readonly AppToDeploy App = new("orders-ui", new JsonObject { ["manifest"] = "name: orders-ui\n", ["files"] = new JsonObject(), ["note"] = "build 3" });

    [Fact]
    public void A_folder_without_a_built_app_is_refused_before_anything_is_sent()
    {
        var folder = Directory.CreateTempSubdirectory("deploy-app-").FullName;
        try
        {
            var steps = new List<string>();
            var refused = Assert.Throws<DeployException>(() => AppDeploy.Read(folder, null, steps.Add));
            Assert.Equal(409, refused.Status);
            Assert.StartsWith("The built app cannot be sent: ", refused.Message, StringComparison.Ordinal);
            Assert.Equal(["Read the built app."], steps);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task The_gateway_checks_the_app_then_the_version_is_published_through_the_link()
    {
        using var link = new FakeLink(Gateway.Url)
        {
            Answer = (method, path, _) => (method, path) switch
            {
                ("POST", "apps/check") => (200, new JsonObject { ["diagnostics"] = new JsonArray() }),
                ("PUT", "apps/orders-ui") => (200, new JsonObject { ["version"] = 4 }),
                _ => null,
            },
        };
        var steps = new List<string>();

        var answer = await AppDeploy.SendAsync(link, Gateway, App, steps.Add);

        Assert.Equal([("POST", "apps/check"), ("PUT", "apps/orders-ui")], link.Calls.Select(c => (c.Method, c.Path)));
        Assert.Equal("build 3", (string?)JsonNode.Parse(link.Calls[1].Body!)!["note"]);
        Assert.Equal((4, "orders-ui", "edge", "https://edge:8443"), ((int)answer["version"]!, (string?)answer["app"], (string?)answer["target"], (string?)answer["gateway"]));
        Assert.Equal(["Asked https://edge:8443 what publishing orders-ui would do.", "Published orders-ui to https://edge:8443."], steps);
    }

    [Fact]
    public async Task An_error_the_gateway_s_check_finds_stops_the_deploy_before_the_version()
    {
        using var link = new FakeLink(Gateway.Url)
        {
            Answer = (_, path, _) => path == "apps/check" ? (200, new JsonObject { ["diagnostics"] = new JsonArray(new JsonObject { ["severity"] = "error", ["message"] = "The view 'map' is not known." }) }) : null,
        };

        var refused = await Assert.ThrowsAsync<DeployException>(() => AppDeploy.SendAsync(link, Gateway, App, _ => { }));

        Assert.Equal("The Gateway's check of the app found errors: The view 'map' is not known.", refused.Message);
        Assert.Single(link.Calls);
    }

    [Fact]
    public async Task A_refusal_comes_back_in_the_gateway_s_words_and_a_forbidden_one_keeps_its_status()
    {
        using var link = new FakeLink(Gateway.Url)
        {
            Answer = (method, path, _) => (method, path) switch
            {
                ("POST", "apps/check") => (200, new JsonObject()),
                _ => (403, new JsonObject { ["error"] = "The app orders-ui is another client's." }),
            },
        };

        var refused = await Assert.ThrowsAsync<DeployException>(() => AppDeploy.SendAsync(link, Gateway, App, _ => { }));

        Assert.Equal(403, refused.Status);
        Assert.Equal("The Gateway refused the app orders-ui (403): The app orders-ui is another client's.", refused.Message);
    }

    [Fact]
    public async Task A_preview_goes_to_the_preview_channel_and_points_the_instance_s_preview_at_it()
    {
        using var link = new FakeLink(Gateway.Url)
        {
            Answer = (method, path, _) => (method, path) switch
            {
                ("POST", "apps/check") => (200, new JsonObject()),
                ("PUT", "apps/orders-ui/channels/preview") => (200, new JsonObject { ["version"] = 5 }),
                ("PUT", "app-instances/sales/channels/preview") => (200, new JsonObject { ["path"] = "/apps/sales/preview/", ["address"] = "https://edge:8443/apps/sales/preview/" }),
                _ => null,
            },
        };

        var answer = await AppDeploy.SendAsync(link, Gateway, App, _ => { }, previewInstance: "sales");

        Assert.Equal("orders-ui", (string?)JsonNode.Parse(link.Calls[2].Body!)!["app"]);
        Assert.Equal(("sales", "/apps/sales/preview/", "https://edge:8443/apps/sales/preview/"), ((string?)answer["instance"], (string?)answer["preview_path"], (string?)answer["preview_address"]));
    }

    [Fact]
    public async Task A_gateway_that_cannot_be_reached_is_a_502_naming_its_address()
    {
        using var link = new FakeLink(Gateway.Url) { Unreachable = true };

        var refused = await Assert.ThrowsAsync<DeployException>(() => AppDeploy.SendAsync(link, Gateway, App, _ => { }));

        Assert.Equal(502, refused.Status);
        Assert.Equal("https://edge:8443 could not be reached (Connection refused).", refused.Message);
    }
}
