using DbDataBuild.Cli.Runner;

namespace DbDataBuild.Tests.Unit;

/// <summary>What a refresh loaded, read from its document: the run lineage a refresh task reports to the Runner (plan 24a UC13), which tells the serving groups it is paired with.</summary>
public sealed class RefreshOutputTests
{
    private const string Refreshed = """
        {"command":"connection refresh","ok":true,"data":{"connection":"sqlserver","dry_run":false,"success":true,"results":[
          {"model":"marts.orders","member":"marts.orders","load":"full","state":"done","status":"ok","rows":10},
          {"model":"marts.lines","member":"marts.lines","load":"full","state":"failed","status":"failed"},
          {"model":"marts.people","member":"marts.people","load":"full","state":"skipped","status":"skipped: busy"}]}}
        """;


    [Fact]
    public void Only_the_models_that_finished_are_read_from_the_document_and_nothing_else_is()
    {
        Assert.Equal([("sqlserver", "marts.orders")], RefreshOutput.LoadedModels(Refreshed));
        Assert.Empty(RefreshOutput.LoadedModels(null));
        Assert.Empty(RefreshOutput.LoadedModels("""{"data":{"connection":"x","results":"no"}}"""));
        Assert.Empty(RefreshOutput.LoadedModels("""{"data":{"results":[{"model":"a","state":"done"}]}}"""));
    }
}
