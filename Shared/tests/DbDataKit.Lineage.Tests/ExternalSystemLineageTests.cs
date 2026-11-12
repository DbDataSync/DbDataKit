namespace DbDataKit.Lineage.Tests;

/// <summary>Plan 27a IX12 and plan 18: an outside system a task reads from or writes to is a node of its own kind, with a stable id, and a rerun adds no duplicate.</summary>
public class ExternalSystemLineageTests
{
    [Fact]
    public void A_system_the_task_read_is_upstream_of_it_one_it_wrote_to_is_downstream_and_each_has_an_unknown_upstream()
    {
        var graph = ExternalSystemLineage.Design("runner-1", "load-orders", [new ExternalTouch("HTTP-API", " Orders.Example.com ")], [new ExternalTouch("sftp", "partner")]);

        var api = graph.Node("external://http-api/orders.example.com")!;
        Assert.Equal((NodeKind.ExternalSystem, true, "Orders.Example.com"), (api.Kind, api.UnknownUpstream, api.Name));
        var provenance = graph.Provenance("suite://runner-1/task/load-orders");
        Assert.Equal(api.Id, Assert.Single(provenance.Reached).Node.Id);                                       // `lineage provenance` of the task names the API
        Assert.Equal(("wrote to", EdgeKind.Serves), (graph.Downstream("suite://runner-1/task/load-orders").Single().Detail, graph.Downstream("suite://runner-1/task/load-orders").Single().Kind));
    }

    [Fact]
    public void A_rerun_names_the_same_ids_and_adds_nothing_and_the_kind_goes_over_the_wire()
    {
        var once = ExternalSystemLineage.Design("runner-1", "t", [new ExternalTouch("sftp", "partner")], []);
        var twice = LineageGraph.Merge([once, ExternalSystemLineage.Design("runner-1", "t", [new ExternalTouch("SFTP", "partner ")], [])]);

        Assert.Equal(once.Fingerprint(), twice.Fingerprint());
        var document = LineageJson.Write(twice);
        Assert.Contains("\"externalsystem\"", document.ToJsonString());
        Assert.Equal(twice.Fingerprint(), LineageJson.Read(document).Fingerprint());
    }
}
