using System.Text.Json.Nodes;

namespace DbDataKit.Deploy.Tests;

public sealed class RunnerChannelConnectionsTests
{
    private static readonly DeployTarget Runner = new("jobs", "https://runner:8495", "r1", DeployProducts.Runner);
    private const string Secret = "dbdatakit:runner:dev-login:crm-to-dw.test.target:password";
    private const string Value = "pw-value-7731";
    private const string Definition = $"schema: connection/1\nname: crm-to-dw-target-test\nengine: postgres\nconnection_string: 'Host=db;Database=dw'\nauth: {{ mode: sql, user: ana, password_secret: 'secret:{Secret}' }}\n";

    private static JsonObject CopyAnswer(bool resolves) => new()
    {
        ["name"] = "crm-to-dw-target-test", ["version"] = "sha256:0123456789abcdef", ["kind"] = "postgres",
        ["secrets"] = new JsonArray(new JsonObject { ["path"] = "auth.password_secret", ["reference"] = "secret:" + Secret, ["resolves"] = resolves, ["problem"] = resolves ? null : "not in the store" }),
    };

    [Fact]
    public void Only_a_runner_keeps_a_channel_s_connections()
    {
        var refused = Assert.Throws<DeployException>(() => new RunnerChannelConnections(new FakeLink(), Runner with { Name = "edge", Product = DeployProducts.Gateway }));

        Assert.Contains("not a Runner", refused.Message);
    }

    [Fact]
    public async Task A_set_up_stores_the_login_then_puts_the_copy_then_binds_the_slot_through_the_link()
    {
        using var link = new FakeLink(Runner.Url)
        {
            Answer = (method, path, _) => (method, path) switch
            {
                ("PUT", "secrets/" + "dbdatakit%3Arunner%3Adev-login%3Acrm-to-dw.test.target%3Apassword") => (200, new JsonObject { ["name"] = Secret }),
                ("PUT", "connection-copies/crm-to-dw-target-test") => (200, CopyAnswer(true)),
                ("PUT", "bindings/test/integration/crm-to-dw") => (200, new JsonObject { ["bindings"] = new JsonArray(new JsonObject { ["slot"] = "target", ["bound_to"] = "crm-to-dw-target-test" }) }),
                _ => null,
            },
        };
        var heard = new List<string>();

        var result = await new RunnerChannelConnections(link, Runner).SetUpAsync(
            new ChannelConnectionSetUp("crm-to-dw", "test", "target", "crm-to-dw-target-test", Definition, new ChannelLogin(Secret, Value)), heard.Add);

        Assert.Equal(["PUT secrets/", "PUT connection-copies/crm-to-dw-target-test", "PUT bindings/test/integration/crm-to-dw"], link.Calls.Select(c => c.Method + " " + (c.Path.StartsWith("secrets/", StringComparison.Ordinal) ? "secrets/" : c.Path)));
        Assert.Equal(Value, (string?)JsonNode.Parse(link.Calls[0].Body!)!["value"]);
        Assert.Equal(Definition, (string?)JsonNode.Parse(link.Calls[1].Body!)!["definition"]);
        Assert.Equal("crm-to-dw-target-test", (string?)JsonNode.Parse(link.Calls[2].Body!)!["slots"]!["target"]);
        Assert.Equal(Secret, result.LoginSecret);
        Assert.True(result.Bind.Bound);
        Assert.Equal(("sha256:0123456789abcdef", "postgres"), (result.Copy.Version, result.Copy.Kind));
        Assert.Empty(result.Copy.Unresolved);
        Assert.Equal(heard, result.Steps);
        Assert.DoesNotContain(heard, l => l.Contains(Value, StringComparison.Ordinal));
        Assert.DoesNotContain(Value, new ChannelLogin(Secret, Value).ToString());
        Assert.Contains("Bound the slot 'target' of crm-to-dw in test to 'crm-to-dw-target-test'.", heard);
    }

    [Fact]
    public async Task A_reference_the_runner_cannot_resolve_and_a_login_the_copy_does_not_name_are_warnings_and_the_slot_is_still_bound()
    {
        using var link = new FakeLink(Runner.Url)
        {
            Answer = (method, path, _) => method != "PUT" ? null
                : path.StartsWith("secrets/", StringComparison.Ordinal) ? (200, new JsonObject { ["name"] = "dbdatakit:runner:dev-login:other:password" })
                : path.StartsWith("connection-copies/", StringComparison.Ordinal) ? (200, CopyAnswer(false))
                : (200, new JsonObject { ["bindings"] = new JsonArray() }),
        };

        var result = await new RunnerChannelConnections(link, Runner).SetUpAsync(
            new ChannelConnectionSetUp("crm-to-dw", "test", "target", "crm-to-dw-target-test", Definition, new ChannelLogin("dbdatakit:runner:dev-login:other:password", Value)));

        Assert.True(result.Bind.Bound);
        Assert.Contains(result.Steps, l => l.StartsWith("Warning: the definition of 'crm-to-dw-target-test' does not name secret:dbdatakit:runner:dev-login:other:password", StringComparison.Ordinal));
        Assert.Contains(result.Steps, l => l.StartsWith($"Warning: jobs cannot resolve secret:{Secret} (auth.password_secret): not in the store", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_bind_the_runner_holds_for_a_confirmation_says_what_to_confirm_and_a_second_bind_sends_the_scopes()
    {
        const string scope = "bindings/runner/crm-to-dw/target/test";
        using var link = new FakeLink(Runner.Url)
        {
            Answer = (_, _, body) => JsonNode.Parse(body!)!["confirm"] is JsonArray
                ? (200, new JsonObject { ["bindings"] = new JsonArray(new JsonObject { ["slot"] = "target", ["bound_to"] = "dw-prod" }) })
                : (409, new JsonObject
                {
                    ["error"] = "test would use production's connection.",
                    ["needsConfirmation"] = new JsonArray(new JsonObject { ["scope"] = scope, ["slot"] = "crm-to-dw/target", ["release"] = "test", ["connection"] = "dw-prod", ["effect"] = "writes reach production" }),
                }),
        };
        var connections = new RunnerChannelConnections(link, Runner);

        var held = await connections.BindAsync("crm-to-dw", "test", "target", "dw-prod");
        var need = Assert.Single(held.Needs);
        Assert.False(held.Bound);
        Assert.Equal((scope, "test", "dw-prod", "writes reach production"), (need.Scope, need.Channel, need.Connection, need.Effect));
        Assert.Equal("test would use production's connection.", held.Error);

        var bound = await connections.BindAsync("crm-to-dw", "test", "target", "dw-prod", [scope], "a load test on a copy of production");
        Assert.True(bound.Bound);
        var sent = JsonNode.Parse(link.Calls[^1].Body!)!;
        Assert.Equal(scope, (string?)sent["confirm"]![0]);
        Assert.Equal("a load test on a copy of production", (string?)sent["note"]);
    }

    [Fact]
    public async Task A_refusal_of_the_runner_comes_back_in_its_words_with_its_status_and_names_no_value()
    {
        using var link = new FakeLink(Runner.Url)
        {
            Answer = (_, path, _) => path.StartsWith("connection-copies/", StringComparison.Ordinal)
                ? (422, new JsonObject { ["error"] = "The definition does not load.", ["diagnostics"] = new JsonArray("line 3: `engine` is missing.") })
                : (403, new JsonObject { ["error"] = "This needs the admin role." }),
        };
        var connections = new RunnerChannelConnections(link, Runner);

        var copy = await Assert.ThrowsAsync<DeployException>(() => connections.PutCopyAsync("crm", "schema: connection/1\nname: crm\n"));
        Assert.Equal((422, "The definition does not load. line 3: `engine` is missing."), (copy.Status, copy.Message));

        var bind = await Assert.ThrowsAsync<DeployException>(() => connections.BindAsync("crm-to-dw", "test", "target", "crm"));
        Assert.Equal((403, "This needs the admin role."), (bind.Status, bind.Message));

        var login = await Assert.ThrowsAsync<DeployException>(() => connections.SendLoginAsync(new ChannelLogin(Secret, Value)));
        Assert.Equal(403, login.Status);
        Assert.DoesNotContain(Value, login.Message);

        link.Unreachable = true;
        Assert.Equal(502, (await Assert.ThrowsAsync<DeployException>(() => connections.PutCopyAsync("crm", Definition))).Status);
    }

    [Fact]
    public async Task A_name_that_is_not_a_package_or_channel_name_is_refused_before_anything_is_sent()
    {
        using var link = new FakeLink(Runner.Url);
        var connections = new RunnerChannelConnections(link, Runner);

        Assert.Equal(400, (await Assert.ThrowsAsync<DeployException>(() => connections.BindAsync("CRM to DW", "test", "target", "crm"))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<DeployException>(() => connections.BindAsync("crm-to-dw", "Test", "target", "crm"))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<DeployException>(() => connections.PutCopyAsync("crm", " "))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<DeployException>(() => connections.SendLoginAsync(new ChannelLogin(Secret, "")))).Status);
        Assert.Empty(link.Calls);
    }
}
