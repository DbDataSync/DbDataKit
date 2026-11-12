using DbDataKit.State;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Tests.Serving;

/// <summary>Plan 21 CH07: the Gateway's expiry of idle drafts reads <c>store.idleDays</c> and <c>store.noticeDays</c> at each pass and has the defaults the Store has. The sweep itself is the shared <c>DraftSweep</c>, tested on both engines in DraftTests.</summary>
public sealed class GatewayDraftSweeperTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "dbdata-draft-sweeper-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        foreach (var path in new[] { file, file + "-wal", file + "-shm" }.Where(File.Exists)) File.Delete(path);
    }

    [Fact]
    public async Task The_settings_default_to_thirty_days_and_a_week_and_are_asked_for_at_every_pass()
    {
        var defaults = new StoreModuleOptions();
        Assert.Equal((30, 7), (defaults.IdleDays, defaults.NoticeDays));

        var layout = StateLayout.Create(StateEngineIds.Sqlite, [DefinitionsModule.Module], new StateScope(Group: "main"));
        var models = new PublishedEntityModels(new DefinitionStore(new StateDatabase(file, layout)));
        var api = new GroupStoreApi(() => new GatewayOptions(), null!, models, null!);
        var asked = 0;
        var sweeper = new GatewayDraftSweeper(api, null!, () => { asked++; return new StoreModuleOptions { IdleDays = 10 + asked }; }, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.Equal(new DbDataKit.Store.DraftSweepResult(0, 0), await sweeper.RunOnceAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(1), CancellationToken.None));
        await sweeper.RunOnceAsync(DateTimeOffset.UtcNow, TimeSpan.FromHours(1), CancellationToken.None);
        Assert.Equal(2, asked);
    }
}
