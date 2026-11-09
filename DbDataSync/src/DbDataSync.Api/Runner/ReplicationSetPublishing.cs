using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Runner.Client;
using DbDataKit.Web.Trust;
using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;

namespace DbDataSync.Api.Runner;

/// <summary>A publish that was refused, or that could not be finished: the message is the words of whoever refused it (the verb's, for a mapping whose column cache is missing or two replications that make one task name; the runner's, for an unbound slot), unchanged.</summary>
public sealed class ReplicationSetRefusedException(string message) : InvalidOperationException(message);

/// <summary>What one replication became in the package: its task, schedule, the slots it needs and the Sync connection each slot is for.</summary>
public sealed record PublishedReplicationView(string Replication, string Task, string Mode, string? Cron, int? FrequencySeconds, IReadOnlyList<string> Slots, int Mappings, IReadOnlyDictionary<string, string>? SlotConnections = null);

/// <summary>What a publish (or a check) built and, for a publish, what the runner answered: the version and how many files had to go.</summary>
public sealed record ReplicationSetPublication(string Package, string? Channel, string Mode, int? Version, bool? Changed, int? Uploaded, int? Skipped, int Files, IReadOnlyList<string> Bound, IReadOnlyList<PublishedReplicationView> Replications);

/// <summary>The body of the publish action: <c>stage</c> (the default: the runners prepare the version and activate it once every one has it ready) or <c>serve</c> (served at once), the slot bindings to make in the channel first, and the version the caller saw.</summary>
public sealed record PublishRunnerRequest(string? Mode = null, IReadOnlyDictionary<string, string>? Bind = null, int? ExpectedVersion = null);

/// <summary>The outcome of a run of <c>dbdatasync package publish</c>.</summary>
public sealed record PackageLaunch(int ExitCode, string Output, string Errors);

/// <summary>Runs <c>dbdatasync package publish</c>: a seam for a test; the real one starts the CLI as a child process.</summary>
public interface IPackageLauncher
{
    Task<PackageLaunch> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct);
}

/// <summary>
/// Starts the CLI of this application as a child process, the way <see cref="LibraryValidationLauncher"/> does, and waits for it. A child because the publish restores the libraries of the repository for the platform (a real load of NuGet
/// packages into a process that exits afterwards) and reads the whole definition; the API host itself never touches either.
/// </summary>
public sealed class CliPackageLauncher(ApiOptions options) : IPackageLauncher
{
    public async Task<PackageLaunch> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var (fileName, leading) = ChildCommand.ForCli(options);
        var info = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in leading) info.ArgumentList.Add(argument);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--repo");
        info.ArgumentList.Add(options.RepoRoot);
        using var process = new Process { StartInfo = info };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* ended meanwhile */ }
            throw;
        }

        return new PackageLaunch(process.ExitCode, stdout.ToString().Trim(), stderr.ToString().Trim());
    }
}

/// <summary>What the Sync API asks of the runner about its package: what it holds, and the binding of a channel's slots.</summary>
public interface IRunnerPackages
{
    /// <summary>What the runner answers to <c>describe</c>, null when it has no such package.</summary>
    Task<JsonNode?> DescribeAsync(CancellationToken ct);

    Task BindAsync(IReadOnlyDictionary<string, string> slots, CancellationToken ct);
}

/// <summary>The runner a <see cref="RunnerLink"/> names, spoken to through the paired client (the pairing's roles decide what it may do; a refusal comes back in the runner's words).</summary>
public sealed class ClientRunnerPackages(RunnerPublishClient client, RunnerLink link) : IRunnerPackages
{
    public Task<JsonNode?> DescribeAsync(CancellationToken ct) => client.DescribeAsync(link.Server, null, link.Package, null, ct);

    public async Task BindAsync(IReadOnlyDictionary<string, string> slots, CancellationToken ct) =>
        await client.BindAsync(link.Server, null, link.Package, link.Channel, slots, null, null, ct);

    public static ClientRunnerPackages For(RunnerLink link, Func<string, string?> environment) =>
        new(new RunnerPublishClient(new RunnerClientStore(link.ClientDirectory ?? RunnerClientStore.DefaultDirectory(environment)), environment), link);
}

/// <summary>One connection slot of the package in the channel: what the manifests declare and what it is bound to (nothing: the runner refuses to activate a version while it is unbound).</summary>
public sealed record RunnerSlotView(string Slot, string Kind, IReadOnlyList<string> Tasks, IReadOnlyList<int> Versions, string? BoundTo, int? Revision);

/// <summary>How far the runners are with the staged version (a staged version is activated once every live runner of the group has it ready).</summary>
public sealed record RunnerStagingView(int Version, bool AllReady, string Summary, IReadOnlyList<string> Failed, bool NeedsConfirmation, IReadOnlyList<string> Losses);

/// <summary>What one runner of the group reports of the channel's versions.</summary>
public sealed record RunnerInstanceView(string Instance, int Version, string State, string? Error, string? Note, bool Gone, string? At);

/// <summary>One line of the package's history on the runner.</summary>
public sealed record RunnerEventView(string Action, string? Channel, int? Version, string? Actor, string? At, string? Note);

/// <summary>
/// The package of the linked runner as the Sync screens show it (plan 28c E08): whether it is published, the version the channel serves, the one staged behind it and how far the runners are, the slots and what each is bound to, what the
/// runners report and the latest events. <see cref="Linked"/> is false while no runner is linked; <see cref="Published"/> is false while the runner has no such package.
/// </summary>
public sealed record RunnerPackageView(
    bool Linked, string? Server, string? Channel, string? Package, bool Published, int? Latest, int? Active, int? Previous, int? Staged,
    RunnerStagingView? Staging, IReadOnlyList<RunnerSlotView> Slots, IReadOnlyList<RunnerInstanceView> Instances, IReadOnlyList<RunnerEventView> Events);

/// <summary>
/// The Sync back-end action that publishes the replication set to the paired runner (plan 28c E08): <c>dbdatasync package publish --to runner</c>, run as a child process with the link <c>PUT /api/runner</c> stored (server, channel, package,
/// client directory), so the console and the command line publish with the same code and refuse in the same words: a mapping whose column cache is missing (the text of <c>MetadataNotCachedException</c>), two replications that make one task
/// name, a schedule that does not validate. Nothing is sent until the package is built; a refusal says "Nothing was sent". Every publish and binding is audited (who, the command, the package, the channel and how it ended, never a value).
/// </summary>
public sealed class ReplicationSetPublishing(RunnerAccess runner, IPackageLauncher launcher, Func<RunnerLink, IRunnerPackages> packages, IAuditLog audit, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _one = new(1, 1);

    public const string PublishCommand = "runner.publish";
    public const string BindCommand = "runner.bind";

    private RunnerLink Link() => runner.Link ?? throw new OwnershipException($"No runner is linked: set the runner (server, channel and package) before the replications are published ({runner.File.Path}).");

    /// <summary>What a publish would hold (the replications, their tasks and slots), or the refusal: the manifest is built from the repository and nothing is restored or sent.</summary>
    public async Task<ReplicationSetPublication> CheckAsync(CancellationToken ct)
    {
        var link = runner.Link;
        List<string> arguments = ["package", "publish", "--to", "runner", "--check", "--json"];
        if (link is not null) arguments.AddRange(["--package", link.Package]);
        return await RunAsync(arguments, ct);
    }

    public async Task<ReplicationSetPublication> PublishAsync(PublishRunnerRequest request, string userId, string actor, CancellationToken ct)
    {
        var link = Link();
        var mode = (request.Mode ?? "stage").ToLowerInvariant();
        if (mode is not ("stage" or "serve")) throw new ArgumentException($"'{request.Mode}' is not a way to publish: stage (the runners activate it once every one has it ready) or serve.");
        List<string> arguments = ["package", "publish", "--to", "runner", "--json", "--server", link.Server, "--channel", link.Channel, "--package", link.Package];
        if (link.ClientDirectory is not null) arguments.AddRange(["--client-dir", link.ClientDirectory]);
        foreach (var (slot, connection) in (request.Bind ?? new Dictionary<string, string>()).OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(slot) || slot.Contains('=') || string.IsNullOrWhiteSpace(connection)) throw new ArgumentException($"A binding is a slot and the name of the connection it is bound to on the runner, not '{slot}' and '{connection}'.");
            arguments.AddRange(["--bind", $"{slot.Trim()}={connection.Trim()}"]);
        }

        if (mode == "serve") arguments.Add("--serve");
        if (request.ExpectedVersion is { } expected) arguments.AddRange(["--expected-version", expected.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        if (!await _one.WaitAsync(0, ct)) throw new OwnershipException("A publish is already running; wait for it to end.");
        var target = $"{link.Package}/{link.Channel}@{link.Server}";
        try
        {
            ReplicationSetPublication done;
            try { done = await RunAsync(arguments, ct); }
            catch (ReplicationSetRefusedException ex)
            {
                Record(userId, actor, PublishCommand, target, "refused: " + FirstLine(ex.Message));
                throw;
            }

            Record(userId, actor, PublishCommand, target, $"ok: {(done.Mode == "stage" ? "staged" : "published")} version {done.Version}{(done.Changed == false ? " (unchanged)" : "")}, {done.Uploaded} uploaded, {done.Skipped} already there");
            return done;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Binds the channel's slots to connections of the runner (a slot name to the name of what it is bound to, never a value). The runner's own words come back when it refuses.</summary>
    public async Task BindAsync(IReadOnlyDictionary<string, string> slots, string userId, string actor, CancellationToken ct)
    {
        var link = Link();
        if (slots.Count == 0 || slots.Any(s => string.IsNullOrWhiteSpace(s.Key) || string.IsNullOrWhiteSpace(s.Value))) throw new ArgumentException("Name the slots and the connection each is bound to on the runner.");
        var target = $"{link.Package}/{link.Channel}@{link.Server}";
        try { await packages(link).BindAsync(slots, ct); }
        catch (RunnerClientException ex)
        {
            Record(userId, actor, BindCommand, target, "refused: " + FirstLine(ex.Message));
            throw;
        }

        Record(userId, actor, BindCommand, target, "ok: " + string.Join(", ", slots.Keys.Order(StringComparer.Ordinal)));
    }

    /// <summary>The package as the linked runner holds it, narrowed to the link's channel.</summary>
    public async Task<RunnerPackageView> StatusAsync(CancellationToken ct)
    {
        if (runner.Link is not { } link) return new RunnerPackageView(false, null, null, null, false, null, null, null, null, null, [], [], []);
        var described = await packages(link).DescribeAsync(ct);
        if (described is null) return new RunnerPackageView(true, link.Server, link.Channel, link.Package, false, null, null, null, null, null, [], [], []);
        return ViewOf(link, described);
    }

    internal static RunnerPackageView ViewOf(RunnerLink link, JsonNode described)
    {
        static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
        static int? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var number) ? number : null;
        bool Mine(JsonNode? node) => Text(node?["channel"]) == link.Channel;

        var head = described["channels"]?.AsArray().FirstOrDefault(Mine);
        var staging = described["staging"]?.AsArray().FirstOrDefault(Mine) is { } s
            ? new RunnerStagingView(Number(s["version"]) ?? 0, s["all_ready"]?.GetValue<bool>() ?? false, Text(s["summary"]) ?? "",
                [.. (s["failed"]?.AsArray() ?? []).Select(f => $"{Text(f?["instance"])}: {Text(f?["error"])}")], s["needs_confirmation"]?.GetValue<bool>() ?? false,
                [.. (s["losses"]?.AsArray() ?? []).Select(l => Text(l) ?? "")])
            : null;
        var bindings = (described["bindings"]?.AsArray() ?? []).Where(Mine).ToDictionary(b => Text(b!["slot"])!, b => (BoundTo: Text(b!["bound_to"]), Revision: Number(b["revision"])), StringComparer.Ordinal);
        var slots = (described["slots"]?.AsArray() ?? []).Where(Mine).Select(x =>
        {
            var slot = Text(x!["slot"])!;
            bindings.TryGetValue(slot, out var bound);
            return new RunnerSlotView(slot, Text(x["kind"]) ?? "", [.. (x["tasks"]?.AsArray() ?? []).Select(t => Text(t) ?? "")], [.. (x["versions"]?.AsArray() ?? []).Select(v => Number(v) ?? 0)], bound.BoundTo, bound.Revision);
        }).ToList();
        var instances = (described["instances"]?.AsArray() ?? []).Where(Mine)
            .Select(i => new RunnerInstanceView(Text(i!["instance"]) ?? "", Number(i["version"]) ?? 0, Text(i["state"]) ?? "", Text(i["error"]), Text(i["note"]), i["gone"]?.GetValue<bool>() ?? false, Text(i["at"]))).ToList();
        var events = (described["events"]?.AsArray() ?? []).Where(e => Text(e?["channel"]) is null || Mine(e)).TakeLast(12)
            .Select(e => new RunnerEventView(Text(e!["action"]) ?? "", Text(e["channel"]), Number(e["version"]), Text(e["actor"]), Text(e["at"]), Text(e["note"]))).ToList();
        return new RunnerPackageView(true, link.Server, link.Channel, link.Package, true, Number(described["latest_version"]), Number(head?["active"]), Number(head?["previous"]), Number(head?["staged"]), staging, slots, instances, events);
    }

    private async Task<ReplicationSetPublication> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var launched = await launcher.RunAsync(arguments, ct);
        if (launched.ExitCode != 0)
            throw new ReplicationSetRefusedException(launched.Errors.Length > 0 ? launched.Errors : launched.Output.Length > 0 ? launched.Output : $"dbdatasync package publish ended with exit code {launched.ExitCode} and said nothing.");
        var line = launched.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.StartsWith('{'));
        try { return (line is null ? null : JsonSerializer.Deserialize<ReplicationSetPublication>(line, Json)) ?? throw new JsonException("no result line"); }
        catch (JsonException ex) { throw new ReplicationSetRefusedException($"dbdatasync package publish succeeded but its result could not be read ({ex.Message}): {launched.Output}"); }
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    /// <summary>One line of the audit log beside the state: who (the user a console vouched for), what, on which package and channel, and how it ended. The caller's own id and name are all the API has of the token (the console that vouched is not in the claims).</summary>
    private void Record(string userId, string actor, string command, string target, string outcome) =>
        audit.Append(new AuditEntry(clock.GetUtcNow(), "console", "the console", userId, actor, true, command, target, outcome, "-"));
}
