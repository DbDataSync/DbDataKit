using System.Text;
using DbDataKit.Hosting.Startup;

namespace DbDataKit.Hosting.Cli;

/// <summary>The console's part in <c>setup single-server</c>: the join tokens and requests, which servers it lists, and the first administrator's link.</summary>
public interface ISingleServerConsole
{
    /// <summary>The console has made its certificate (at its first start), so a join command can name its thumbprint.</summary>
    bool CanMakeTokens { get; }

    /// <summary>
    /// Makes a single-use join token for <paramref name="member"/> and writes the join request into its data folder; returns the request's path.
    /// Throws <see cref="InvalidOperationException"/> saying why it cannot.
    /// </summary>
    string WriteJoinRequest(SetupMember member);

    /// <summary>The console lists a server that presented the certificate with this fingerprint (lower-case hex SHA-256).</summary>
    bool IsJoined(string certificateFingerprint);

    /// <summary>What the summary says about the first administrator: the setup link from <c>FIRST-RUN.txt</c>, or that the console has a user already.</summary>
    string FirstAdministrator();
}

/// <summary>How <c>setup single-server</c> runs.</summary>
public sealed class SingleServerSetupOptions
{
    /// <summary>Register each service with <c>--skip-folder-permissions-check</c> on its command line.</summary>
    public bool SkipFolderPermissionsCheck { get; init; }

    /// <summary>How long one service may take to listen and join after it was started.</summary>
    public TimeSpan Wait { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>How often the service is looked at while it starts.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a service may stay stopped after it was started, with no startup-failure file, before it counts as failed.</summary>
    public TimeSpan StoppedFor { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long a stop may take.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// <c>dbdatakit setup single-server</c>: registers, starts and joins every suite service installed on the machine, one after another in start order, and
/// <c>--stop</c>. For each service it registers the service when it is not registered as it should be, gives its top folder its owner and access list
/// and its program folder its access list, writes a join request with a fresh single-use token (not for the console), and starts it; it then waits for
/// the service to listen and to be listed by the console, printing how far each got (<c>registered · started · listening · joined</c>) and, for a service
/// that fails, its startup-failure file. It ends with a summary: what is ready, what is not, the console's address and the first administrator's link.
/// A service that is registered as it should be, running and joined is left alone, so a second run picks up where the first stopped.
/// Design: docs/install.md#setting-up-one-machine-or-inviting-another
/// </summary>
public sealed class SingleServerSetup(ISuiteServiceHost host, ISingleServerConsole console, Func<Uri, CancellationToken, Task<string?>>? probe = null, TimeProvider? clock = null)
{
    /// <summary>The stages a service passes, in order, as the progress line names them.</summary>
    public static readonly IReadOnlyList<string> Stages = ["registered", "started", "listening", "joined"];

    private readonly Func<Uri, CancellationToken, Task<string?>> _probe = probe ?? SuiteListen.ProbeAsync;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Sets up <paramref name="members"/> (the console first) and prints what happened to <paramref name="output"/>. Returns 0 when every service is
    /// ready, 1 when one is not, 3 when the command is not elevated.
    /// </summary>
    public async Task<int> RunAsync(IReadOnlyList<SetupMember> members, SingleServerSetupOptions options, TextWriter output, CancellationToken ct = default,
        IReadOnlyList<(SuiteService Service, IReadOnlyList<string> LookedIn)>? missing = null)
    {
        if (!host.IsElevated)
        {
            await output.WriteLineAsync(ElevationDetector.Message("setup single-server"));
            return ExitCodes.NotElevated;
        }

        var width = members.Select(m => m.Service.Program.Length).DefaultIfEmpty(0).Max();
        var outcomes = new List<(SetupMember Member, bool Ready, string? Why)>();
        var consoleReady = false;
        var anyWork = false;
        foreach (var member in members.OrderBy(m => m.Service.StartOrder))
        {
            var isConsole = member.Service == SuiteServices.Console;
            if (!isConsole && !consoleReady)
            {
                await output.WriteLineAsync($"{member.Service.Program.PadRight(width)}  not set up: the console is not ready, so it cannot join");
                outcomes.Add((member, false, "the console is not ready"));
                continue;
            }

            var result = await SetUpAsync(member, isConsole, options, ct);
            anyWork |= result.Worked;
            var line = string.Join(" · ", Stages.Take(result.Reached)) + (result.Worked ? "" : " (already)");
            await output.WriteLineAsync($"{member.Service.Program.PadRight(width)}  {(result.Reached == 0 ? "not registered" : line)}");
            foreach (var note in result.Notes) await output.WriteLineAsync($"{"".PadRight(width)}  {note}");
            if (result.Failure is { } failure)
                foreach (var text in failure.ReplaceLineEndings("\n").Split('\n')) await output.WriteLineAsync($"{"".PadRight(width)}  {text}");
            outcomes.Add((member, result.Ready, result.Why));
            if (isConsole) consoleReady = result.Ready;
        }

        foreach (var (service, lookedIn) in missing ?? [])
            await output.WriteLineAsync($"{service.Program.PadRight(width)}  not installed beside dbdatakit (looked for {string.Join(", ", lookedIn)})");

        await output.WriteLineAsync();
        if (!anyWork && outcomes.All(o => o.Ready))
            await output.WriteLineAsync("Nothing to do: every service is registered, running and joined.");
        var ready = outcomes.Where(o => o.Ready).Select(o => o.Member.Service.Program).ToList();
        var notReady = outcomes.Where(o => !o.Ready).ToList();
        await output.WriteLineAsync(ready.Count > 0 ? $"Ready: {string.Join(", ", ready)}." : "Ready: none.");
        if (notReady.Count > 0)
        {
            await output.WriteLineAsync($"Not ready: {string.Join("; ", notReady.Select(o => $"{o.Member.Service.Program} ({o.Why})"))}.");
            await output.WriteLineAsync("Fix what is said above and run `dbdatakit setup single-server` again: it leaves what is ready alone and picks up where it stopped.");
        }
        var consoleMember = members.FirstOrDefault(m => m.Service == SuiteServices.Console);
        if (consoleMember is not null && SuiteListen.Address(SuiteServices.Console, consoleMember.DataFolder) is { } address)
            await output.WriteLineAsync($"The console is on {address.GetLeftPart(UriPartial.Authority)}.");
        if (consoleReady) await output.WriteLineAsync(console.FirstAdministrator());
        return notReady.Count == 0 ? ExitCodes.Ok : ExitCodes.Failure;
    }

    private sealed record Outcome(int Reached, bool Ready, bool Worked, string? Why, string? Failure, IReadOnlyList<string> Notes);

    private async Task<Outcome> SetUpAsync(SetupMember member, bool isConsole, SingleServerSetupOptions options, CancellationToken ct)
    {
        var notes = new List<string>();
        var registration = SuiteRegistration.For(member, options.SkipFolderPermissionsCheck);
        var service = member.Service;
        RegistrationCheck check;
        try { check = host.Check(registration); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new Outcome(0, false, true, $"its registration could not be read: {ex.Message}", null, notes);
        }

        var state = host.State(service);
        var listen = SuiteListen.Address(service, member.DataFolder);
        var listening = listen is not null && state == SuiteServiceState.Running ? await _probe(listen, ct) : null;
        var joined = isConsole ? console.CanMakeTokens : listening is not null && !File.Exists(RequestPath(member)) && console.IsJoined(listening);
        if (check.State == RegistrationState.Same && state == SuiteServiceState.Running && listening is not null && joined)
            return new Outcome(isConsole ? 3 : 4, true, false, null, null, notes);

        // register, folders, join request
        var restart = false;
        try
        {
            if (check.State != RegistrationState.Same)
            {
                host.Register(registration);
                notes.Add(check.State == RegistrationState.NotRegistered
                    ? $"registered {registration.Spec.Name}: {Describe(registration)}"
                    : $"registered {registration.Spec.Name} again: it ran `{check.Registered}`, now {Describe(registration)}");
                restart = true;
            }
            host.PrepareFolders(registration);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException
                                       or System.Security.Principal.IdentityNotMappedException or PlatformNotSupportedException
                                       or DbDataKit.Hosting.WindowsService.ServiceInstallException or DbDataKit.Hosting.WindowsService.ServiceControlException)
        {
            return new Outcome(0, false, true, "it could not be registered", ex.Message, notes);
        }

        // A server that never started has no address yet and gets its join request before its first start, and a request left by an earlier run is
        // replaced, since its token may have ended. Any other server is started as it is, and is given a request only once it listens and the console
        // does not list the certificate it presents: a server that is stopped but joined then spends no token.
        var requested = false;
        if (!isConsole && !joined && (listen is null || File.Exists(RequestPath(member)) || state == SuiteServiceState.Running && listening is not null))
        {
            if (await RequestJoinAsync(registration, notes) is { } refused) return refused;
            requested = restart = true;
        }

        // start, or start again so it runs as registered now and reads its join request
        var startedAt = _clock.GetUtcNow();
        if (await StartAsync(service, state is SuiteServiceState.Running or SuiteServiceState.Starting, restart, options) is { } problem)
            return new Outcome(1, false, true, "it could not be started", problem, notes);

        return await WaitAsync(registration, isConsole, startedAt, requested, options, notes, ct);
    }

    // writes the join request with a new token; an outcome when that cannot be done
    private Task<Outcome?> RequestJoinAsync(SuiteRegistration registration, List<string> notes)
    {
        try
        {
            var request = console.WriteJoinRequest(registration.Member);
            host.GiveToService(registration, request);
            notes.Add($"wrote the join request {request} with a single-use token");
            return Task.FromResult<Outcome?>(null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<Outcome?>(new Outcome(1, false, true, "no join request could be written for it", ex.Message, notes));
        }
    }

    // starts the service, stopping it first when it runs and must start again; what went wrong, or null
    private async Task<string?> StartAsync(SuiteService service, bool running, bool restart, SingleServerSetupOptions options)
    {
        try
        {
            if (running && restart) await host.StopAsync(service, options.StopTimeout);
            if (!running || restart) host.Start(service);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or DbDataKit.Hosting.WindowsService.ServiceControlException)
        {
            return ex.Message;
        }
    }

    private async Task<Outcome> WaitAsync(SuiteRegistration registration, bool isConsole, DateTimeOffset startedAt, bool requested, SingleServerSetupOptions options,
        List<string> notes, CancellationToken ct)
    {
        var member = registration.Member;
        var service = member.Service;
        var logsRoot = SuiteServices.LogsRootFor(service, member.DataFolder);
        var until = startedAt + options.Wait;
        DateTimeOffset? stoppedSince = null;
        var reached = 2;
        while (true)
        {
            if (FailedStart(service, logsRoot, startedAt))
                return new Outcome(reached, false, true, "its start failed: its startup-failure file is above", StartupFailureFile.DescribeLastStart(logsRoot, service), notes);

            var state = host.State(service);
            if (state == SuiteServiceState.Failed)
                return new Outcome(reached, false, true, "the service manager says it failed", StartupFailureFile.DescribeLastStart(logsRoot, service), notes);
            stoppedSince = state == SuiteServiceState.Stopped ? stoppedSince ?? _clock.GetUtcNow() : null;
            if (stoppedSince is { } since && _clock.GetUtcNow() - since > options.StoppedFor)
                return new Outcome(reached, false, true, "it stopped", StartupFailureFile.DescribeLastStart(logsRoot, service), notes);

            if (state == SuiteServiceState.Running && SuiteListen.Address(service, member.DataFolder) is { } listen && await _probe(listen, ct) is { } fingerprint)
            {
                reached = 3;
                if (isConsole)
                {
                    if (console.CanMakeTokens) return new Outcome(3, true, true, null, null, notes);
                }
                else if (!File.Exists(RequestPath(member)))
                {
                    if (console.IsJoined(fingerprint)) return new Outcome(4, true, true, null, null, notes);
                    if (!requested)
                    {
                        // it listens and the console does not list it: it joins with a request, at a start of its own
                        if (await RequestJoinAsync(registration, notes) is { } refused) return refused with { Reached = 3 };
                        requested = true;
                        startedAt = _clock.GetUtcNow();
                        until = startedAt + options.Wait;
                        if (await StartAsync(service, running: true, restart: true, options) is { } problem)
                            return new Outcome(3, false, true, "it could not be started again", problem, notes);
                        reached = 2;
                    }
                }
            }

            if (_clock.GetUtcNow() >= until)
                return new Outcome(reached, false, true, reached == 3 ? $"it listens, but the console did not list it within {options.Wait.TotalSeconds:0} s" : $"it did not listen within {options.Wait.TotalSeconds:0} s",
                    StartupFailureFile.DescribeLastStart(logsRoot, service), notes);
            await Task.Delay(options.Poll, ct);
        }
    }

    /// <summary>
    /// <c>setup single-server --stop</c>: stops every suite service registered on this machine, the last in start order first. Returns 0 when each is
    /// stopped (or not registered), 1 when one could not be stopped, 3 when the command is not elevated.
    /// </summary>
    public async Task<int> StopAllAsync(SingleServerSetupOptions options, TextWriter output, CancellationToken ct = default)
    {
        if (!host.IsElevated)
        {
            await output.WriteLineAsync(ElevationDetector.Message("setup single-server --stop"));
            return ExitCodes.NotElevated;
        }

        var width = SuiteServices.All.Max(s => s.Program.Length);
        var failed = false;
        foreach (var service in SuiteServices.All.OrderByDescending(s => s.StartOrder))
        {
            var state = host.State(service);
            string said;
            if (state == SuiteServiceState.NotRegistered) said = "not registered";
            else if (state == SuiteServiceState.Stopped) said = "stopped already";
            else
            {
                try
                {
                    await host.StopAsync(service, options.StopTimeout);
                    said = "stopped";
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or DbDataKit.Hosting.WindowsService.ServiceControlException)
                {
                    failed = true;
                    said = $"not stopped: {ex.Message}";
                }
            }
            await output.WriteLineAsync($"{service.Program.PadRight(width)}  {said}");
        }
        return failed ? ExitCodes.Failure : ExitCodes.Ok;
    }

    private static string RequestPath(SetupMember member) => Path.Combine(member.DataFolder, "join-request.yaml");

    private static string Describe(SuiteRegistration registration) =>
        OperatingSystem.IsWindows() ? $"`{registration.Spec.CommandLine}` as {registration.Spec.EffectiveAccount.Name}" : $"`{registration.Unit.ExecStart}` as {registration.Service.LinuxUser}";

    /// <summary>A start that began at or after <paramref name="startedAt"/> wrote its failure: the file holds a reason, not the line of a start going on.</summary>
    private static bool FailedStart(SuiteService service, string logsRoot, DateTimeOffset startedAt)
    {
        var path = service.FailureFileIn(logsRoot);
        try
        {
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < startedAt.UtcDateTime.AddSeconds(-1)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            return text.Length > 0 && !text.StartsWith("starting since ", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
