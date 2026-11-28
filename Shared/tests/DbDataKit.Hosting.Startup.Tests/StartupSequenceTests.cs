using System.Net.Sockets;

namespace DbDataKit.Hosting.Startup.Tests;

/// <summary>A data folder and a shared logs folder of a test's own, removed afterwards.</summary>
public abstract class StartupTestBase : IDisposable
{
    protected StartupTestBase()
    {
        Root = Path.Combine(Path.GetTempPath(), "dbdk-startup-" + Guid.NewGuid().ToString("N"));
        Data = Path.Combine(Root, "runner");
        Logs = Path.Combine(Root, "logs");
        Directory.CreateDirectory(Data);
    }

    protected string Root { get; }

    protected string Data { get; }

    protected string Logs { get; }

    protected List<string> Unreported { get; } = [];

    protected string FailureFile => SuiteServices.Runner.FailureFileIn(Logs);

    protected StartupSequence Sequence(IReadOnlyList<PortRange>? excluded = null) => new(new StartupOptions
    {
        Service = SuiteServices.Runner,
        DataFolder = Data,
        LogsRoot = Logs,
        Version = "1.2.3",
        Folders = [new StartupFact("Configuration repository", Path.Combine(Data, "config"))],
        ExcludedPorts = () => excluded ?? [],
        Unreportable = Unreported.Add,
    });

    /// <summary>Reads the file as another program would while the server may still have it open.</summary>
    protected static string Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }
}

public sealed class StartupSequenceTests : StartupTestBase
{
    [Fact]
    public async Task A_clean_start_runs_every_step_in_order_and_leaves_no_failure_file()
    {
        var ran = new List<StartupStep>();
        var sequence = Sequence();
        // filled in out of order: the sequence keeps its own
        foreach (var step in StartupSteps.InOrder.Reverse()) sequence.Step(step, _ => ran.Add(step));

        var result = await sequence.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(StartupSteps.InOrder, ran);
        Assert.False(File.Exists(FailureFile));
        Assert.True(Directory.Exists(Path.Combine(Logs, "dbdatarunner")));
        Assert.Empty(Unreported);
    }

    [Fact]
    public async Task A_clean_start_deletes_the_file_an_earlier_failed_start_left()
    {
        var first = await Sequence().Step(StartupStep.State, _ => throw new InvalidOperationException("down")).RunAsync(TestContext.Current.CancellationToken);
        Assert.False(first.Succeeded);
        Assert.True(File.Exists(FailureFile));

        var second = await Sequence().Step(StartupStep.State, _ => { }).RunAsync(TestContext.Current.CancellationToken);

        Assert.True(second.Succeeded);
        Assert.False(File.Exists(FailureFile));
    }

    [Fact]
    public async Task The_file_says_which_step_is_running_while_it_runs()
    {
        var seen = new Dictionary<StartupStep, string>();
        var sequence = Sequence();
        foreach (var step in StartupSteps.InOrder) sequence.Step(step, _ => seen[step] = Read(FailureFile).Trim());

        await sequence.RunAsync(TestContext.Current.CancellationToken);

        foreach (var step in StartupSteps.InOrder)
            Assert.Matches($"^starting since \\d{{4}}-\\d\\d-\\d\\dT\\d\\d:\\d\\d:\\d\\dZ, step: {step.Name()}, not finished$", seen[step]);
        Assert.Single(seen.Values.Select(s => s[..s.IndexOf(',', StringComparison.Ordinal)]).Distinct()); // one start time for the whole start
    }

    [Fact]
    public async Task A_failed_step_stops_the_start_and_the_file_names_the_account_the_folders_and_the_exception_chain()
    {
        var ran = new List<StartupStep>();
        var sequence = Sequence();
        foreach (var step in StartupSteps.InOrder)
            sequence.Step(step, _ =>
            {
                ran.Add(step);
                if (step == StartupStep.Certificate)
                    throw new InvalidOperationException("outer problem", new FileNotFoundException("inner problem"));
            });

        var result = await sequence.RunAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(StartupStep.Certificate, result.FailedStep);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal([StartupStep.Folder, StartupStep.Keys, StartupStep.Certificate], ran);
        var text = Read(FailureFile);
        Assert.Equal(result.Report, text);
        Assert.StartsWith("Startup of dbdatarunner 1.2.3 failed at the certificate step, at ", text);
        Assert.Contains("Step: certificate", text);
        Assert.Contains($"Account: {StartupAccount.Current()}", text);
        Assert.Contains(Environment.UserName, text);
        Assert.Contains($"Data folder: {Data}", text);
        Assert.Contains($"Logs folder: {Path.Combine(Logs, "dbdatarunner")}", text);
        Assert.Contains($"Configuration repository: {Path.Combine(Data, "config")}", text);
        Assert.Contains("System.InvalidOperationException: outer problem", text);
        Assert.Contains("System.IO.FileNotFoundException: inner problem", text);
        Assert.Contains("What to check: Check the certificate file", text);
        Assert.DoesNotContain("not finished", text);
    }

    [Fact]
    public async Task The_folder_step_names_the_folder_its_owner_and_the_account()
    {
        var text = await FailWith(StartupStep.Folder, () => StartupStepException.Folder(Data, owner: null, account: null, "The folder is owned by another account."));

        Assert.Contains($"The folder '{Data}' is owned by {FileOwnership.DescribeOwner(Data)}", text);
        Assert.Contains($"Folder: {Data}", text);
        Assert.Contains($"Owner: {FileOwnership.DescribeOwner(Data)}", text);
        Assert.Contains($"Account: {StartupAccount.Current()}", text);
        Assert.Contains("`dbdatakit setup single-server` sets the folder up", text);
    }

    [Fact]
    public async Task The_keys_step_names_the_key_file_and_the_unexpected_grant()
    {
        var keyFile = Path.Combine(Data, "keys", "keyring.json");
        var text = await FailWith(StartupStep.Keys, () => StartupStepException.Keys(keyFile, @"BUILTIN\Users"));

        Assert.Contains($"Key file: {keyFile}", text);
        Assert.Contains(@"Unexpected grant: BUILTIN\Users", text);
        Assert.Contains($"The key file '{keyFile}' grants access to BUILTIN\\Users, which is not expected.", text);
    }

    [Fact]
    public async Task The_certificate_step_names_the_file_and_the_account()
    {
        var pfx = Path.Combine(Data, "certs", "server.pfx");
        var text = await FailWith(StartupStep.Certificate,
            () => StartupStepException.Certificate(pfx, @"NT SERVICE\DbDataRunner", new UnauthorizedAccessException("Access to the path is denied.")));

        Assert.Contains($"The certificate '{pfx}' cannot be opened or made by NT SERVICE\\DbDataRunner.", text);
        Assert.Contains($"Certificate: {pfx}", text);
        Assert.Contains("System.UnauthorizedAccessException: Access to the path is denied.", text);
    }

    [Fact]
    public async Task The_state_step_names_the_engine_the_database_and_the_migration()
    {
        var text = await FailWith(StartupStep.State,
            () => StartupStepException.State("sqlserver", "dbdatakit_runner", "0007_run_records", new InvalidOperationException("Invalid column name 'x'.")));

        Assert.Contains("The migration '0007_run_records' of the state database 'dbdatakit_runner' on sqlserver failed.", text);
        Assert.Contains("Engine: sqlserver", text);
        Assert.Contains("Database: dbdatakit_runner", text);
        Assert.Contains("Migration: 0007_run_records", text);
        Assert.Contains("Invalid column name 'x'.", text);
    }

    [Fact]
    public async Task The_ports_step_names_the_port_who_holds_it_and_the_excluded_ranges()
    {
        var excluded = new[] { new PortRange(5357, 5357), new PortRange(7800, 7899) };
        var text = await FailWith(StartupStep.Ports,
            () => StartupStepException.Ports(7841, "svchost.exe (pid 4242)", excluded, new SocketException((int)SocketError.AccessDenied)));

        Assert.Contains("Port 7841 cannot be bound: svchost.exe (pid 4242) holds it: it is inside the excluded port range 7800-7899.", text);
        Assert.Contains("Port: 7841", text);
        Assert.Contains("Held by: svchost.exe (pid 4242)", text);
        Assert.Contains("Excluded port ranges: 5357, 7800-7899", text);
        Assert.Contains("Hint: a listening port could not be bound.", text);
        Assert.Contains("netsh int ipv4 show excludedportrange protocol=tcp", text);
    }

    [Fact]
    public async Task The_join_step_names_the_consoles_address_and_its_answer()
    {
        var text = await FailWith(StartupStep.Join,
            () => StartupStepException.Join("https://console.example.com:7800", "401 the join token was already used"));

        Assert.Contains("The console at https://console.example.com:7800 answered: 401 the join token was already used", text);
        Assert.Contains("Console: https://console.example.com:7800", text);
        Assert.Contains("Answer: 401 the join token was already used", text);
    }

    [Fact]
    public async Task A_join_refused_inside_the_ports_step_is_the_join_step_s_failure()
    {
        var result = await Sequence().Step(StartupStep.Ports, _ => throw StartupStepException.Join("https://console.example.com:7800", "the join token was already used"))
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StartupStep.Join, result.FailedStep);
        var text = Read(FailureFile);
        Assert.Contains("failed at the join step", text);
        Assert.Contains($"Step: {StartupStep.Join.Name()}", text);
    }

    [Fact]
    public async Task A_port_refused_in_a_step_that_did_not_name_it_still_gets_the_excluded_ranges_and_the_hint()
    {
        var sequence = Sequence(excluded: [new PortRange(7840, 7859)])
            .Step(StartupStep.Ports, _ => throw new IOException("Failed to bind to address", new SocketException((int)SocketError.AddressAlreadyInUse)));

        var result = await sequence.RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Excluded port ranges: 7840-7859", result.Report);
        Assert.Contains("Hint: a listening port could not be bound.", result.Report);
        Assert.Contains("System.Net.Sockets.SocketException", result.Report);
    }

    [Fact]
    public void A_step_filled_in_twice_is_refused()
    {
        var sequence = Sequence().Step(StartupStep.Keys, _ => { });

        var error = Assert.Throws<InvalidOperationException>(() => sequence.Step(StartupStep.Keys, _ => { }));
        Assert.Contains("keys", error.Message);
    }

    [Fact]
    public async Task A_logs_folder_that_cannot_be_made_does_not_stop_the_start_and_the_reason_goes_elsewhere()
    {
        // a file where the logs folder should be: the folder cannot be made
        File.WriteAllText(Path.Combine(Root, "blocked"), "");
        var options = new StartupOptions
        {
            Service = SuiteServices.Runner,
            DataFolder = Data,
            LogsRoot = Path.Combine(Root, "blocked", "logs"),
            ExcludedPorts = () => [],
            Unreportable = Unreported.Add,
        };

        var result = await new StartupSequence(options).Step(StartupStep.State, _ => throw new InvalidOperationException("state is down")).RunAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains(Unreported, u => u.Contains("no startup-failure file is written", StringComparison.Ordinal));
        Assert.Contains(Unreported, u => u.Contains("state is down", StringComparison.Ordinal));
    }

    private async Task<string> FailWith(StartupStep step, Func<StartupStepException> failure)
    {
        var result = await Sequence().Step(step, _ => throw failure()).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(step, result.FailedStep);
        Assert.NotEqual(0, result.ExitCode);
        var text = Read(FailureFile);
        Assert.Contains($"Step: {step.Name()}", text);
        Assert.Contains($"Account: {StartupAccount.Current()}", text);
        Assert.Contains($"Data folder: {Data}", text);
        Assert.Contains("Exception chain:", text);
        Assert.Contains("DbDataKit.Hosting.Startup.StartupStepException", text);
        return text;
    }
}
