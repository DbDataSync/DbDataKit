using DbDataKit.Cli;
using DbDataKit.Server;
using DbDataKit.State;
using Microsoft.Data.Sqlite;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Scaling out is a step, not a reinstall (plan 23a FQ20), the console's half on the real engines: a bundle on SQLite (<c>bundle init</c>, people, the setup checklist, the results of data quality checks and what was alerted) is moved with
/// <c>state move</c> to SQL Server or PostgreSQL, and the restarted console on the new engine has all of it; the commands are the ones <c>DbDataKit/docs/scale-out.md</c> names.
/// </summary>
public sealed class ConsoleScaleOutTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("ddk-console-scale").FullName;
    private readonly StateVerbDatabases databases = new();

    public void Dispose()
    {
        databases.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private static async Task<(int Exit, string Out, string Err)> Run(Func<string, string?>? environment, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = await ConsoleCli.Router(environment).RunAsync(args, new CliContext(o, e, TextReader.Null));
        return (exit, o.ToString(), e.ToString());
    }

    [Theory]
    [MemberData(nameof(StateVerbDatabases.ServerEngines), MemberType = typeof(StateVerbDatabases))]
    public async Task A_bundle_on_sqlite_moves_to_a_server_engine_and_the_restarted_console_has_its_people_its_checklist_and_its_check_results(string engine)
    {
        var directory = Path.Combine(root, "console");
        var connection = databases.Provision(engine);
        var environment = databases.EnvFor(null, null);
        Assert.Equal(0, (await Run(environment, "bundle", "init", "--members", "dbdatasync,dbdatabuild", "--repo", directory)).Exit);

        // a console that has been in use: a person, a skipped step, a failing block check and the alert it sent
        var dataset = "mssql://sales/dbo.orders";
        var before = ConsoleState.Open(directory);
        var admin = new IdentityUserStore(before).CreateUser("Pat Admin", "pat@example.test", UserRole.Admin, emailVerified: true);
        new SetupItems(before).Skip("git-remotes", admin.Id);
        var quality = new QualityStore(before);
        var at = DateTimeOffset.Parse("2026-11-12T09:00:00Z");
        quality.Record(new CheckResultRecord("orders_name_present", dataset, CheckOutcome.Fail, at, 3, 0, ["order_id=7"], "run-1", "block"));
        quality.SetAlerted("sync-1", dataset, "orders_name_present", CheckOutcome.Fail, at);
        SqliteConnection.ClearAllPools();

        // the step of docs/scale-out.md: stop the console, move the state, restart it
        var moved = await Run(environment, "state", "move", "--to-engine", engine, "--to-connection", connection, "--yes", "--repo", directory);
        Assert.True(moved.Exit == 0, moved.Out + moved.Err);
        SqliteConnection.ClearAllPools();

        var settings = ConsoleState.SettingsFrom(environment, null, null, directory)!;
        Assert.Equal(engine, settings.Engine);
        var after = ConsoleState.Open(directory, settings, environment);
        Assert.Equal("Pat Admin", new IdentityUserStore(after).List().Single().DisplayName);
        Assert.Contains("git-remotes", new SetupItems(after).Skipped().Keys);
        var status = new QualityStore(after).Status(dataset, "orders_name_present")!;
        Assert.Equal((CheckOutcome.Fail, true), (status.Outcome, status.Blocking));
        Assert.Equal(["order_id=7"], new QualityStore(after).History(dataset, "orders_name_present").Single().SampleKeys);
        Assert.Equal(CheckOutcome.Fail, new QualityStore(after).AlertedOutcome("sync-1", dataset, "orders_name_present"));      // a restart on the new engine does not send the alert again

        // the bundle is files, not state: the token and the pairing stay where they were, and the console still pairs the servers of this machine by itself
        var (bundle, problem) = ConsoleBundle.Load(directory);
        Assert.Null(problem);
        Assert.Equal(["dbdatasync", "dbdatabuild"], bundle!.Members);
    }
}
