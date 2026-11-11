using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Apps;
using DbDataKit.Apps.Bundling;
using DbDataKit.Authoring;
using DbDataKit.State;

namespace DbDataKit.Authoring.Server;

/// <summary>A build or a preview could not be started or read; <see cref="Status"/> is the HTTP status the route answers with.</summary>
public sealed class WorkspaceBuildException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The builds of a draft (plan 22a GP35, part 1): a build of an app project runs <see cref="AppSourceBuilder"/> on the draft's folder with the vendored package store, the administrator's allow-list and the isolation the settings say
/// (<see cref="WorkspaceAppSettings"/>), with the views generated from the manifest, and keeps the result in <c>builds/&lt;project&gt;/&lt;id&gt;</c> of the server's directory: <c>app/</c> is the built folder (what a preview serves and a
/// publish sends) and <c>result.json</c> says what happened, with the bundler's diagnostics split into file, line and reason. Each build is a row of the <c>build</c> table (queued, running, succeeded or failed) and runs in the background with a
/// time limit; the bundler is a child process of the server (Bun, in a network namespace with a cleared environment: the "child process per build"), and the server's own process runs no app code. At most two builds run at once.
/// </summary>
public sealed partial class WorkspaceBuilds : IDisposable
{
    private readonly string directory;
    private readonly WorkspaceStore store;
    private readonly Workspaces workspaces;
    private readonly Func<WorkspaceAppSettings> settings;
    private readonly Action<string> log;
    private readonly SemaphoreSlim slots = new(2);
    private readonly CancellationTokenSource stopping = new();
    private readonly List<Task> running = [];

    /// <param name="settings">Read for each build, so a changed <c>server.yaml</c> applies to the next one.</param>
    public WorkspaceBuilds(string directory, WorkspaceStore store, Workspaces workspaces, Func<WorkspaceAppSettings> settings, Action<string> log)
    {
        this.directory = directory;
        this.store = store;
        this.workspaces = workspaces;
        this.settings = settings;
        this.log = log;
        // a build that was queued or running when the server stopped will not finish: say so, instead of leaving it running for ever
        foreach (var project in store.Projects())
            foreach (var build in store.Builds(project.Id, null, 200).Where(b => b.Status is "queued" or "running"))
                store.SetBuildStatus(build.Id, "failed", "The server stopped while this build was waiting or running. Build again.");
    }

    public string FolderOf(string project, Guid id) => Path.Combine(directory, "builds", project, id.ToString("D"));

    public string AppFolderOf(string project, Guid id) => Path.Combine(FolderOf(project, id), "app");

    /// <summary>What a build needs and whether this machine has it.</summary>
    public WorkspaceBuildReadiness Readiness()
    {
        try { return settings().Check(); }
        catch (InvalidOperationException ex) { return new WorkspaceBuildReadiness([ex.Message], null, null, null, "required", false); }     // a server.yaml that is not valid says where
    }

    /// <summary>
    /// Asks for a build of a draft and returns at once with the queued row; the build runs in the background. Refused (409) when the server cannot build (no store, no Bun, no isolation: the sentences say what to set), when the project is not an
    /// app project, or when the draft has no manifest.
    /// </summary>
    public WorkspaceBuildRow Request(AuthoringProject project, string kind, Workspace workspace, string requestedBy)
    {
        if (kind != "app") throw new WorkspaceBuildException($"A {kind} project is not built here: only app projects are (plan 27 adds the others).", 409);
        var readiness = Readiness();
        if (!readiness.Ready) throw new WorkspaceBuildException(string.Join(" ", readiness.Problems), 409);
        var root = workspaces.RootOf(workspace);
        if (!File.Exists(Path.Combine(root, "app.yaml"))) throw new WorkspaceBuildException("This draft has no app.yaml, so there is nothing to build. An app project has its manifest, app.yaml, at the top.", 409);

        var current = settings();
        var (commit, dirty) = Head(root);
        var row = store.RequestBuild(project.Id, workspace.Id, requestedBy, commit);
        lock (running)
        {
            running.RemoveAll(t => t.IsCompleted);
            running.Add(Task.Run(() => RunAsync(row, root, current, readiness.Store!, dirty)));
        }
        return row;
    }

    /// <summary>Waits for the builds in flight (tests, and a server that stops).</summary>
    public async Task WhenIdleAsync()
    {
        Task[] pending;
        lock (running) pending = [.. running];
        await Task.WhenAll(pending);
    }

    private static (string? Commit, bool Dirty) Head(string root)
    {
        try
        {
            using var repository = new LibGit2Sharp.Repository(root);
            return (repository.Head.Tip?.Sha, repository.RetrieveStatus(new LibGit2Sharp.StatusOptions { IncludeUntracked = true }).IsDirty);
        }
        catch (LibGit2Sharp.LibGit2SharpException) { return (null, false); }
    }

    private async Task RunAsync(WorkspaceBuildRow row, string root, WorkspaceAppSettings current, VendoredPackageStore packages, bool dirty)
    {
        var folder = FolderOf(row.Project, row.Id);
        try
        {
            await slots.WaitAsync(stopping.Token);
        }
        catch (OperationCanceledException)
        {
            Safely(() => store.SetBuildStatus(row.Id, "canceled", "The server stopped before this build started."));
            return;
        }
        try
        {
            Safely(() => store.SetBuildStatus(row.Id, "running"));
            Directory.CreateDirectory(folder);
            var timeout = TimeSpan.FromSeconds(current.BuildTimeoutSeconds);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            limit.CancelAfter(timeout + TimeSpan.FromSeconds(15));      // the bundler stops itself at the timeout; this is the cap on everything around it
            var started = DateTimeOffset.UtcNow;
            AppBuildOutcome outcome;
            try
            {
                var bun = BunLocator.Locate(current.Bun);
                outcome = await AppSourceBuilder.BuildAsync(
                    new AppBuildOptions(root, Path.Combine(folder, "app"), Timeout: timeout, Store: packages, Allowed: current.Allowed, Isolation: current.Isolation),
                    _ => bun.Found ? new AppBundlerChoice(new BunAppBundler(bun.Path!)) : new AppBundlerChoice(null, bun.Problem), limit.Token);
            }
            catch (OperationCanceledException)
            {
                var why = stopping.IsCancellationRequested ? "The server stopped while this build ran." : $"The build took longer than {current.BuildTimeoutSeconds} seconds and was stopped (apps.buildTimeoutSeconds in server.yaml).";
                Finish(row, folder, started, dirty, current, null, stopping.IsCancellationRequested ? "canceled" : "failed", why);
                return;
            }
            var summary = outcome.Succeeded
                ? $"Built {outcome.Manifest!.Name} {outcome.Manifest.Version}: {outcome.Files} files."
                : outcome.Errors.FirstOrDefault(e => !e.StartsWith("The build failed", StringComparison.Ordinal))?.Trim() ?? "The build failed.";
            Finish(row, folder, started, dirty, current, outcome, outcome.Succeeded ? "succeeded" : "failed", summary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Safely(() => store.SetBuildStatus(row.Id, "failed", $"The build could not be run: {ex.Message}"));
            log($"Build {row.Id} of {row.Project}/{row.WorkspaceId} could not be run: {ex.Message}");
        }
        finally
        {
            slots.Release();
        }
    }

    private void Finish(WorkspaceBuildRow row, string folder, DateTimeOffset started, bool dirty, WorkspaceAppSettings current, AppBuildOutcome? outcome, string status, string message)
    {
        var notes = outcome?.Notes.ToList() ?? [];
        // what the bundler said when it failed (a file, a line, a reason), else the build's own errors (a manifest that is not valid)
        var diagnostics = new JsonArray();
        foreach (var line in outcome is null ? [] : outcome.Bundler.Count > 0 ? outcome.Bundler : outcome.Errors.ToList()) diagnostics.Add(Diagnostic(line));
        var isolated = notes.Any(n => n.Contains("network namespace with no route", StringComparison.Ordinal));
        var result = new JsonObject
        {
            ["status"] = status,
            ["message"] = message,
            ["app"] = outcome?.Manifest?.Name,
            ["version"] = outcome?.Manifest?.Version,
            ["entry"] = outcome?.Entry,
            ["files"] = outcome?.Files ?? 0,
            ["bundled"] = new JsonArray([.. (outcome?.Bundled ?? []).Select(f => (JsonNode?)JsonValue.Create(f))]),
            ["notes"] = new JsonArray([.. notes.Select(n => (JsonNode?)JsonValue.Create(n))]),
            ["diagnostics"] = diagnostics,
            ["isolated"] = isolated,
            ["isolation_setting"] = current.Isolation == NetworkIsolation.Required ? "required" : "auto",
            ["uncommitted_changes"] = dirty,
            ["commit"] = row.CommitSha,
            ["seconds"] = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
        };
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "result.json"), result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Safely(() => store.SetBuildStatus(row.Id, status, message));
    }

    [GeneratedRegex(@"^\s*(?<file>[^\s:'""]+\.[A-Za-z0-9]+):(?<line>\d+)(?::(?<column>\d+))?:?\s*(?<message>.*)$")]
    private static partial Regex Located();

    /// <summary>A line of the bundler's words as a file, a line and a reason when it names a place (<c>src/main.ts:3: imports 'lodash', which is not...</c>), else only the reason.</summary>
    public static JsonObject Diagnostic(string text)
    {
        var line = text.Trim();
        if (Located().Match(text) is { Success: true } m)
            return new JsonObject { ["file"] = m.Groups["file"].Value, ["line"] = int.Parse(m.Groups["line"].Value), ["message"] = m.Groups["message"].Value.Trim() };
        return new JsonObject { ["message"] = line };
    }

    private void Safely(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log($"A build's record could not be written: {ex.Message}"); }
    }

    /// <summary>The build's record and what it wrote down, or null when this project has no such build.</summary>
    public (WorkspaceBuildRow Row, JsonObject? Result)? Find(string project, string workspaceId, Guid id)
    {
        if (store.Builds(project, workspaceId, 200).FirstOrDefault(b => b.Id == id) is not { } row) return null;
        var file = Path.Combine(FolderOf(project, id), "result.json");
        try { return (row, File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null); }
        catch (Exception ex) when (ex is IOException or JsonException) { return (row, null); }
    }

    private readonly object publishGate = new();

    /// <summary>What was published from a build, oldest first: the lines of <c>publishes.jsonl</c> in the build's folder (when, who, to which target, whether it worked and what the Gateway said).</summary>
    public JsonArray Publishes(string project, Guid id)
    {
        var file = Path.Combine(FolderOf(project, id), "publishes.jsonl");
        var all = new JsonArray();
        lock (publishGate)
        {
            if (!File.Exists(file)) return all;
            foreach (var line in File.ReadAllLines(file).Where(l => l.Length > 0))
            {
                try { all.Add(JsonNode.Parse(line)); }
                catch (JsonException) { /* a damaged line is skipped */ }
            }
        }
        return all;
    }

    public void RecordPublish(string project, Guid id, JsonObject entry)
    {
        var folder = FolderOf(project, id);
        try
        {
            lock (publishGate)
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "publishes.jsonl"), entry.ToJsonString() + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log($"The record of a publish of build {id} could not be written: {ex.Message}"); }
    }

    public void Dispose()
    {
        stopping.Cancel();
        try { WhenIdleAsync().Wait(TimeSpan.FromSeconds(20)); }
        catch (AggregateException) { /* a build that fails is already recorded */ }
        stopping.Dispose();
    }
}
