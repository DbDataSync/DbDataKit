using DbDataKit.Authoring;

namespace DbDataKit.Deploy.Tests;

public sealed class DeployRoutesTests
{
    private static readonly DeployTarget Runner = new("jobs", "https://runner:8495", "r1", DeployProducts.Runner);
    private static readonly DeployTarget Gateway = Runner with { Name = "edge", Url = "https://edge:8443", Product = DeployProducts.Gateway };

    [Fact]
    public void A_kind_is_deployed_only_to_a_product_it_names_and_the_refusal_says_where_it_goes()
    {
        ProjectPublishRoute[] app = [new(DeployProducts.Gateway, "app", "an app version")];

        Assert.Equal("app", DeployRoutes.RouteFor("app", app, Gateway).What);
        var refused = Assert.Throws<DeployException>(() => DeployRoutes.RouteFor("app", app, Runner));
        Assert.Equal(409, refused.Status);
        Assert.Equal("The app kind of project is published to a gateway (app), and 'jobs' is a runner (https://runner:8495). Publish it to a target of the right product.", refused.Message);
    }

    [Fact]
    public void A_kind_with_two_routes_takes_the_one_of_the_target_s_product_or_the_one_named()
    {
        ProjectPublishRoute[] build = [new(DeployProducts.Runner, "package", "a package"), new(DeployProducts.Gateway, "semantic-model", "the models"), new(DeployProducts.Gateway, "view", "the views")];

        Assert.Equal("package", DeployRoutes.RouteFor("build", build, Runner).What);
        Assert.Equal("semantic-model", DeployRoutes.RouteFor("build", build, Gateway).What);
        Assert.Equal("view", DeployRoutes.RouteFor("build", build, Gateway, "view").What);
        Assert.Contains("published to nowhere", Assert.Throws<DeployException>(() => DeployRoutes.RouteFor("notes", [], Gateway)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_server_s_own_name_says_which_product_it_is()
    {
        Assert.Equal(DeployProducts.Gateway, DeployProducts.OfServer("dbdatagateway"));
        Assert.Equal(DeployProducts.Runner, DeployProducts.OfServer("dbdatarunner"));
        Assert.Null(DeployProducts.OfServer("dbdatakit"));
        Assert.Null(DeployProducts.OfServer(null));
    }

    [Fact]
    public void The_origin_token_names_the_instance_project_and_workspace_each_escaped()
    {
        Assert.Equal("[workspace:ws%201/sales%2Fnorth/main]", DeployOrigin.Token("ws 1", "sales/north", "main"));
    }
}
