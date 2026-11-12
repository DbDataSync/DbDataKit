using DbDataKit.Cli;
using static DbDataKit.State.Cli.Tests.StateCliHarness;

namespace DbDataKit.State.Cli.Tests;

/// <summary>Plan 22a GP20: <c>state backup</c> while the service holds the database, <c>state restore</c> into an empty one, on SQLite, SQL Server and PostgreSQL.</summary>
public sealed class StateBackupTests : IDisposable
{
    private readonly StateCliHarness _h = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dbdatakit-statebackup-");

    public void Dispose()
    {
        _h.Dispose();
        _dir.Delete(true);
    }

    public static TheoryData<string> Engines => StateCliHarness.Engines;

    private static readonly IReadOnlyList<StateModule> Modules = [Module("backup-demo")];

    /// <summary>Runs one verb once (the harness's helper runs it twice, once per output format, which a verb that writes cannot do).</summary>
    private static async Task<(int Exit, string Out, string Error)> Once(StateVerbContext context, params string[] args)
    {
        var router = new NounRouter("tool", [StateVerbs.Create(context)], "1.0");
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await router.RunAsync(["state", .. args], new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    private static void Insert(StateDatabase database, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            using var connection = database.OpenConnection();
            using var insert = database.Command("backup-demo", connection, "INSERT INTO {table:item} (id, label) VALUES ($id, $label);");
            insert.Bind(database, "id", "row-" + i).Bind(database, "label", "label " + i);
            insert.ExecuteNonQuery();
        }
    }

    private static long Count(StateDatabase database)
    {
        using var connection = database.OpenConnection();
        using var count = database.Command("backup-demo", connection, "SELECT COUNT(*) FROM {table:item};");
        return Convert.ToInt64(count.ExecuteScalar());
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_backup_is_taken_while_the_database_is_open_and_restored_into_an_empty_one_with_the_same_rows(string engine)
    {
        var source = _h.Provision(engine);
        using var live = source.Open(Modules);                        // the service holds it open
        Insert(live, 0, 40);
        var folder = Path.Combine(_dir.FullName, "backups");

        var backup = await Once(Context(source, Modules), "backup", "--to", folder);
        Assert.True(backup.Exit == 0, backup.Out + backup.Error);
        var made = Assert.Single(Directory.GetDirectories(folder));
        Assert.Matches(@"state-\d{8}T\d{6}Z$", made);
        Assert.True(File.Exists(Path.Combine(made, "manifest.json")));
        Assert.Empty(Directory.GetDirectories(folder, "*.partial"));
        Assert.Contains("Verified against its manifest", backup.Out);

        Insert(live, 40, 50);                                          // written after the backup: not in it

        var target = _h.Provision(engine);
        var restore = await Once(Context(target, Modules), "restore", "--from", folder);        // the folder of backups: the newest is used
        Assert.True(restore.Exit == 0, restore.Out + restore.Error);
        Assert.Contains("Using the newest backup", restore.Out);
        using var restored = target.Open(Modules);
        Assert.Equal(40, Count(restored));
        Assert.Equal(50, Count(live));

        var verify = await Once(Context(target, Modules), "verify", "--snapshot", made, "--db");
        Assert.True(verify.Exit == 0, verify.Out + verify.Error);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_changed_byte_is_found_before_the_target_is_touched_and_a_target_that_holds_data_is_refused(string engine)
    {
        var source = _h.Provision(engine);
        using (var live = source.Open(Modules)) Insert(live, 0, 10);
        var folder = Path.Combine(_dir.FullName, "backups");
        Assert.Equal(0, (await Once(Context(source, Modules), "backup", "--to", folder)).Exit);
        var made = Assert.Single(Directory.GetDirectories(folder));

        var file = Directory.GetFiles(made).Where(f => !f.EndsWith("manifest.json", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal).First(f => new FileInfo(f).Length > 0);
        var bytes = File.ReadAllBytes(file);
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(file, bytes);

        var target = _h.Provision(engine);
        var bad = await Once(Context(target, Modules), "restore", "--from", made);
        Assert.Equal(StateExit.VerifyDiffers, bad.Exit);
        Assert.Contains("nothing was restored", bad.Error);
        var untouched = Context(target, Modules);
        Assert.Equal(0, (await Once(untouched, "show")).Exit);      // the target has no module registered: it is still empty
        using (var opened = target.Open([])) Assert.NotNull(opened);

        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(file, bytes);                              // repaired: now it restores
        Assert.Equal(0, (await Once(Context(target, Modules), "restore", "--from", made)).Exit);
        var again = await Once(Context(target, Modules), "restore", "--from", made);               // the target now holds the module
        Assert.Equal(StateExit.TargetNotEmpty, again.Exit);
    }

    [Fact]
    public async Task A_folder_with_no_backup_and_a_missing_to_are_refused_with_the_usage()
    {
        var source = _h.Provision(StateEngineIds.Sqlite);
        var none = await Once(Context(source, Modules), "restore", "--from", _dir.FullName);
        Assert.Equal(StateExit.SnapshotInvalid, none.Exit);
        Assert.Contains("holds none", none.Error);
        var usage = await Once(Context(source, Modules), "backup");
        Assert.Equal(CliExit.Usage, usage.Exit);
        Assert.Contains("Usage: state backup --to <folder>", usage.Error);
    }
}
