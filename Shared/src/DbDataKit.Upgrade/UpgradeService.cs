using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Hosting;
using DbDataKit.Update;
using DbDataKit.Update.Detect;
using DbDataKit.Update.Service;
using DbDataKit.Web.Trust;

namespace DbDataKit.Upgrade;

/// <summary>A call the upgrade cannot do, with the status the answer has and the words for the person.</summary>
public sealed class UpgradeException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>A server paired with this one and the compatibility generation it reported (24a UC04), for the check that an upgrade does not leave a pair outside N and N-1.</summary>
public sealed record PeerGeneration(string Name, int? Generation);

/// <summary>What the server needs to upgrade itself. <see cref="ProgramDirectory"/> is the folder the program runs from; the upgrade area is its sibling <c>&lt;folder&gt;.upgrade</c>.</summary>
public sealed record UpgradeOptions
{
    public required string Product { get; init; }

    /// <summary>The version that is running, as <c>/version</c> says it.</summary>
    public required string Version { get; init; }

    public required string ProgramDirectory { get; init; }

    /// <summary>This program's compatibility generation (<see cref="SuiteCompatibility.Generation"/>).</summary>
    public int Generation { get; init; } = SuiteCompatibility.Generation;

    /// <summary>The name of the Windows service or systemd unit the program runs as; null when it was started by hand (it cannot be stopped and started again, so it is not upgraded from the console).</summary>
    public string? ServiceName { get; init; }

    /// <summary>Its own address on the machine for <c>/version</c> (loopback); null: not known, so the health of the new version cannot be checked and the server is not upgraded from the console.</summary>
    public Uri? HealthUrl { get; init; }

    public string Rid { get; init; } = PlatformRid.Current;

    /// <summary>How long the drain may wait for the work that is running (24a QU5: 10 minutes). Past it nothing is changed and the person is asked; work is never killed silently.</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public Func<IReadOnlyList<PeerGeneration>> Peers { get; init; } = () => [];

    public InstallEnvironment? Environment { get; init; }

    public string WorkDirectory => ProgramDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".upgrade";
}

/// <summary>Starts the helper that does the swap: a process that outlives the server it replaces.</summary>
public interface IUpgradeLauncher
{
    Task LaunchAsync(HelperInvocation invocation, CancellationToken ct);
}

/// <summary>
/// A server upgrading itself (plan 24a UC09, D6, QU3): <see cref="PrepareAsync"/> downloads the release for this platform, checks it against the release's <c>SHA256SUMS</c>, checks the compatibility generation against the
/// servers paired with it, stages it and says what will happen; <see cref="ApplyAsync"/> drains, then starts the helper (<see cref="UpgradeHelperHost"/>, which is <see cref="UpdateHelper"/> of the shelved updater used as code),
/// which stops the service, swaps the program folder, starts it, waits for the new version to answer <c>/version</c> and commits, or puts the old version back; <see cref="Status"/> is what the console polls, and what the new
/// (or the old, rolled back) server process answers after the swap, from the journal the helper keeps. An image, a Scoop, WinGet or tool install is not upgraded here: <c>prepare</c> returns the command (QU3).
/// </summary>
public sealed class UpgradeService
{
    public const string AttestKeyId = "local";

    private readonly UpgradeOptions _options;
    private readonly ReleaseFeed _feed;
    private readonly IDrainCoordinator _drain;
    private readonly IUpgradeLauncher _launcher;
    private readonly InstallLayout _layout;
    private readonly object _gate = new();
    private string _phase = "idle";
    private string? _problem;

    public UpgradeService(UpgradeOptions options, ReleaseFeed feed, IDrainCoordinator drain, IUpgradeLauncher launcher)
    {
        _options = options;
        _feed = feed;
        _drain = drain;
        _launcher = launcher;
        _layout = new InstallLayout(options.WorkDirectory);
    }

    public UpgradeOptions Options => _options;

    // ---- what this install allows

    private InstallDetection Detect() => InstallMethodDetector.Detect(_options.Environment ?? InstallEnvironment.FromCurrent());

    /// <summary>Null when the console may upgrade this install; otherwise why not.</summary>
    private string? CannotApply(InstallMethod method) => method switch
    {
        InstallMethod.Container => "this server runs in a container, which is replaced, not changed",
        InstallMethod.Scoop or InstallMethod.Winget or InstallMethod.DotnetTool => $"this server was installed with {method}, which upgrades it itself",
        _ when string.IsNullOrWhiteSpace(_options.ServiceName) => "this server was not started as a Windows service or a systemd unit, so it cannot be stopped and started again from here",
        _ when _options.HealthUrl is null || !_options.HealthUrl.IsLoopback => "this server does not know its own loopback address, so the health of the new version could not be checked",
        _ when !ReleaseVersion.TryParse(_options.Version, out _) => $"this server's version '{_options.Version}' is not a version number, so a newer release cannot be told from an older one",
        _ => CannotChangeProgramFolder(),
    };

    /// <summary>The helper runs with the rights of the service. An install that keeps the program folder read-only for it (the default layouts do: the service may read and run its binaries, not change them) cannot be swapped from the console.</summary>
    private string? CannotChangeProgramFolder()
    {
        try
        {
            var parent = Path.GetDirectoryName(_options.ProgramDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? throw new IOException("the program folder has no parent");
            var probe = Path.Combine(parent, ".dbdata-upgrade-probe-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
            Directory.CreateDirectory(_options.WorkDirectory);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the account this server runs as may not change its own program folder ({ex.Message}); an install that keeps it read-only is upgraded with the install script or the installer";
        }
    }

    private JsonObject Instruction(InstallMethod method, FeedRelease? target)
    {
        var now = ReleaseVersion.TryParse(_options.Version, out var current) ? current : ReleaseVersion.Parse("0.0.0");
        var version = target?.Parsed ?? now;
        string text;
        string? command = null;
        if (method is InstallMethod.Container or InstallMethod.Scoop or InstallMethod.Winget or InstallMethod.DotnetTool)
        {
            var result = UpdateApplierSelector.For(method).Plan(new UpdateApplyRequest(_options.Product, _options.Product, now, version, target?.Channel ?? Channel.Stable));
            text = result.Message;
            command = result.Command?.ToString();
        }
        else
        {
            var zip = target?.Asset(target.ZipName(_options.Rid));
            text = $"Install {_options.Product} {version} over this one with the install script of the release (install.sh --version {version} on Linux, install.ps1 -Version {version} on Windows), or unpack {(zip is null ? "its zip" : zip.Name)} over the program folder, and restart the service.";
            command = OperatingSystem.IsWindows() ? $"install.ps1 -Version {version}" : $"install.sh --version {version}";
        }

        return new JsonObject { ["text"] = text, ["command"] = command };
    }

    // ---- prepare

    /// <summary>Downloads and checks a release and stages it; <paramref name="version"/> null takes the newest the feed offers. Nothing about the running server changes.</summary>
    public async Task<JsonObject> PrepareAsync(string? version, CancellationToken ct)
    {
        Settle();
        lock (_gate)
        {
            if (_phase is "draining" or "applying") throw new UpgradeException(409, "An upgrade is being applied: wait until it has finished (GET /api/v1/upgrade).");
            if (LoadJournal() is { State: PlanState.InProgress or PlanState.RollingBack }) throw new UpgradeException(409, "An upgrade is still running on this machine (its journal says so): wait until it has finished.");
        }

        var detection = Detect();
        var blocked = CannotApply(detection.Method);
        var running = ReleaseVersion.TryParse(_options.Version, out var current) ? current : null;

        FeedRelease? target;
        try
        {
            var releases = await _feed.ListAsync(_options.Product, ct);
            target = version is null
                ? releases.FirstOrDefault()
                : releases.FirstOrDefault(r => r.Parsed.Equals(ReleaseVersion.TryParse(version, out var asked) ? asked : null))
                  ?? throw new UpgradeException(404, $"The feed {_feed.Source} has no release {version} of {_options.Product}.");
        }
        catch (FeedException ex) { throw new UpgradeException(502, ex.Message); }

        var report = new JsonObject
        {
            ["product"] = _options.Product, ["from"] = _options.Version, ["generation"] = _options.Generation,
            ["install_method"] = detection.Method.ToString(), ["feed"] = _feed.Source,
        };
        if (target is null || running is not null && target.Parsed <= running && version is null)
        {
            report["state"] = "up_to_date";
            report["to"] = target?.Version;
            report["can_apply"] = false;
            report["message"] = target is null ? $"The feed {_feed.Source} lists no release of {_options.Product}." : $"{_options.Product} {_options.Version} is the newest release the feed lists.";
            return report;
        }

        if (running is not null && target.Parsed < running) throw new UpgradeException(409, $"{target.Version} is older than the running {_options.Version}: a downgrade is not done from the console.");
        report["to"] = target.Version;
        report["notes_url"] = target.NotesUrl?.AbsoluteUri;
        if (blocked is not null)
        {
            report["state"] = "instructions";
            report["can_apply"] = false;
            report["reason"] = blocked;
            report["instruction"] = Instruction(detection.Method, target);
            report["message"] = $"The console does not upgrade this install: {blocked}.";
            return report;
        }

        FeedReleaseDetails details;
        try { details = await _feed.DetailsAsync(target, ct); }
        catch (FeedException ex) { throw new UpgradeException(502, ex.Message); }
        report["generation_of_release"] = details.Generation;

        var zipName = target.ZipName(_options.Rid);
        var asset = target.Asset(zipName) ?? throw new UpgradeException(404, $"The release {target.Version} has no {zipName}, the zip for this platform ({_options.Rid}).");
        if (!details.Sha256.TryGetValue(zipName, out var sha256)) throw new UpgradeException(422, $"The SHA256SUMS of {_options.Product} {target.Version} does not list {zipName}, so the zip cannot be checked and is not used.");
        if (asset.Size <= 0) throw new UpgradeException(422, $"The feed does not say how large {zipName} is, so the download cannot be checked and is not used.");

        // a pair this release would leave further apart than N and N-1 is named, and the upgrade is not offered
        var problems = new JsonArray();
        if (details.Generation is { } generation)
            foreach (var peer in _options.Peers())
                if (peer.Generation is { } theirs && !SuiteCompatibility.Supports(theirs, generation))
                    problems.Add($"After this release {peer.Name} (compatibility generation {theirs}) and this server (generation {generation}) would be {Math.Abs(theirs - generation)} generations apart; they work together up to 1 apart. Upgrade {(theirs < generation ? peer.Name : "the other side")} first.");
        report["problems"] = problems;

        var downloads = Path.Combine(_layout.UpdatesDir, "downloads");
        var artifact = new ReleaseArtifact { Rid = _options.Rid, Kind = "zip", Name = zipName, Url = asset.Url.AbsoluteUri, Sha256 = sha256, Size = asset.Size };
        string file;
        try
        {
            Directory.CreateDirectory(_layout.UpdatesDir);
            var policy = new TrustedUrlPolicy([_feed.TrustedPrefix]) { AllowInsecureHttp = _feed.Settings.Mirror?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true };
            file = await new Downloader(_feed.Fetcher, policy).DownloadAsync(artifact, null, downloads, ct);
        }
        catch (UpdateVerificationException ex)
        {
            throw new UpgradeException(422, $"{zipName} was refused and nothing was changed: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
        {
            throw new UpgradeException(502, $"{zipName} could not be downloaded from {_feed.Source}: {ex.Message}");
        }

        if (problems.Count > 0)
        {
            report["state"] = "refused";
            report["can_apply"] = false;
            report["message"] = problems[0]!.GetValue<string>();
            return report;
        }

        // the manifest (release/1) the helper checks again, signed with this install's own key: the helper trusts nothing in the journal, the service can hand it only what it checked
        var manifest = new ReleaseManifest
        {
            Product = _options.Product, Version = target.Parsed.ToString(), Channel = target.Channel.ToString().ToLowerInvariant(), Published = target.Published,
            SchemaVersions = details.Generation is { } g ? new Dictionary<string, int> { ["generation"] = g } : new(),
            Artifacts = [artifact], Notes = target.NotesUrl?.AbsoluteUri,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ReleaseManifest.JsonOptions);
        var (seed, _) = AttestKey();
        var signature = ManifestSigner.Sign(bytes, seed, AttestKeyId);
        lock (_gate)
        {
            if (_phase is "draining" or "applying") throw new UpgradeException(409, "An upgrade is being applied: wait until it has finished.");
            ClearFinishedJournal();
            var plan = UpdateStager.Stage(_layout, _options.Product, _options.Version, bytes, signature, file, _options.Rid, _options.HealthUrl!.AbsoluteUri, null, (int)_options.HealthTimeout.TotalSeconds);
            _phase = "idle";
            _problem = null;
            report["plan"] = plan.Id;
        }

        report["state"] = "prepared";
        report["can_apply"] = true;
        report["artifact"] = new JsonObject { ["name"] = zipName, ["size"] = asset.Size, ["sha256"] = sha256, ["verified"] = true };
        report["steps"] = new JsonArray("drain: stop taking new work and wait for the work running (up to " + _options.DrainTimeout.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture) + " minutes)",
            "stop the service " + _options.ServiceName, "move the program folder aside and put " + target.Version + " in its place", "start the service and wait for " + target.Version + " to answer /version",
            "if it does not become healthy: put " + _options.Version + " back and start it");
        report["message"] = $"{_options.Product} {target.Version} is downloaded and checked. Apply it to restart this server on the new version.";
        return report;
    }

    /// <summary>The journal, or a refusal (409) that names the file when it is not one this program reads: a call that needs it stops rather than writing over what it cannot read.</summary>
    private UpdatePlan? LoadJournal()
    {
        try { return PlanStore.Load(_layout); }
        catch (UpdatePlanException ex) { throw new UpgradeException(409, ex.Message); }
    }

    private (byte[] Seed, byte[] Public) AttestKey()
    {
        var path = Path.Combine(_layout.Root, "attest.seed");
        Directory.CreateDirectory(_layout.Root);
        byte[] seed;
        if (File.Exists(path) && Convert.FromBase64String(File.ReadAllText(path).Trim()) is { Length: Ed25519.KeySize } existing) seed = existing;
        else
        {
            (seed, _) = Ed25519.GenerateKeyPair();
            File.WriteAllText(path, Convert.ToBase64String(seed));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return (seed, Ed25519.PublicKeyFromSeed(seed));
    }

    public string AttestPublicKey() => Convert.ToBase64String(AttestKey().Public);

    private void ClearFinishedJournal()
    {
        if (LoadJournal() is null) return;
        File.Delete(_layout.PlanPath);
        if (File.Exists(_layout.ResultPath)) File.Delete(_layout.ResultPath);
        if (Directory.Exists(_layout.StagingDir)) Directory.Delete(_layout.StagingDir, true);
    }

    // ---- apply

    /// <summary>Drains and starts the helper for the plan <see cref="PrepareAsync"/> staged. Answers when the helper is running; the console polls <see cref="Status"/> from there.</summary>
    public async Task<JsonObject> ApplyAsync(CancellationToken ct)
    {
        UpdatePlan plan;
        Settle();
        lock (_gate)
        {
            if (_phase is "draining" or "applying") throw new UpgradeException(409, "An upgrade is already being applied.");
            plan = LoadJournal() is { State: PlanState.Pending } pending ? pending : throw new UpgradeException(409, "Nothing is prepared: prepare the upgrade first (POST /api/v1/upgrade with action prepare).");
            if (CannotApply(Detect().Method) is { } blocked) throw new UpgradeException(409, $"This install cannot be upgraded from here: {blocked}.");
            if (!string.Equals(plan.From, _options.Version, StringComparison.OrdinalIgnoreCase)) throw new UpgradeException(409, $"The prepared upgrade starts from {plan.From} and this server runs {_options.Version}: prepare it again.");
            _phase = "draining";
            _problem = null;
        }

        bool idle;
        try { idle = await _drain.DrainAsync(_options.DrainTimeout, ct); }
        catch { Fail("the drain was interrupted"); throw; }
        if (!idle)
        {
            _drain.Resume();
            Fail($"{_drain.ActiveWork} piece(s) of work were still running after {_options.DrainTimeout.TotalMinutes:0.##} minutes");
            throw new UpgradeException(409, $"{_drain.ActiveWork} piece(s) of work are still running after {_options.DrainTimeout.TotalMinutes:0.##} minutes. Nothing was changed and the server takes work again; apply again when they have finished (running work is never stopped silently).");
        }

        try
        {
            lock (_gate) _phase = "applying";
            await _launcher.LaunchAsync(new HelperInvocation(_layout.Root, _options.Product, _options.ProgramDirectory, _options.ServiceName!, _options.Version, $"{AttestKeyId}={AttestPublicKey()}", _options.HealthUrl!.AbsoluteUri), ct);
        }
        catch (Exception ex)
        {
            _drain.Resume();
            Fail("the helper could not be started: " + ex.Message);
            throw new UpgradeException(500, $"The helper that swaps the program could not be started ({ex.Message}). Nothing was changed and the server takes work again.");
        }

        return Status();
    }

    /// <summary>The helper has ended without replacing this process (it refused the plan before it changed anything, or its journal says so): the server takes work again and the next upgrade can be prepared.</summary>
    private void Settle()
    {
        lock (_gate)
        {
            if (_phase != "applying") return;
            UpdatePlan? plan;
            try { plan = PlanStore.Load(_layout); }
            catch (UpdatePlanException) { return; }
            if (plan?.State is not (PlanState.Rejected or PlanState.RolledBack or PlanState.Committed or PlanState.Failed)) return;
            _phase = "idle";
            if (plan.State is PlanState.Rejected) _drain.Resume();
        }
    }

    private void Fail(string problem)
    {
        lock (_gate)
        {
            _phase = "idle";
            _problem = problem;
        }
    }

    // ---- status

    /// <summary>Where the upgrade is: what this process knows (draining, helper started) and the journal the helper writes, which a restarted server reads too.</summary>
    public JsonObject Status()
    {
        Settle();
        var detection = Detect();
        var blocked = CannotApply(detection.Method);
        UpdatePlan? plan = null;
        string? journalProblem = null;
        try { plan = PlanStore.Load(_layout); }
        catch (UpdatePlanException ex) { journalProblem = ex.Message; }
        string phase;
        lock (_gate) phase = _phase;

        var state = phase is "draining" or "applying" && plan?.State is not (PlanState.Committed or PlanState.RolledBack or PlanState.Rejected or PlanState.Failed) ? phase
            : plan?.State switch
            {
                null => "none",
                PlanState.Pending => "prepared",
                PlanState.InProgress or PlanState.RollingBack => "applying",
                PlanState.Committed => "committed",
                PlanState.RolledBack => "rolled_back",
                PlanState.Rejected => "rejected",
                _ => "failed",
            };
        var node = new JsonObject
        {
            ["product"] = _options.Product, ["version"] = _options.Version, ["generation"] = _options.Generation, ["install_method"] = detection.Method.ToString(),
            ["can_apply"] = blocked is null, ["reason"] = blocked, ["state"] = state, ["problem"] = _problem ?? journalProblem, ["draining"] = _drain.IsDraining, ["active_work"] = _drain.ActiveWork,
        };
        if (plan is not null)
        {
            node["plan"] = new JsonObject
            {
                ["id"] = plan.Id, ["from"] = plan.From, ["to"] = plan.To, ["state"] = plan.State.ToString(), ["reason"] = plan.Reason,
                ["steps"] = new JsonArray([.. plan.Steps.Select(s => (JsonNode?)new JsonObject { ["name"] = s.Name.ToString(), ["state"] = s.State.ToString(), ["error"] = s.Error, ["at"] = s.UpdatedAt?.ToString("O", CultureInfo.InvariantCulture) })]),
            };
            node["message"] = state switch
            {
                "committed" => $"Upgraded {plan.From} to {plan.To}.",
                "rolled_back" => $"The new version {plan.To} did not become healthy and {plan.From} was put back: {plan.Reason}",
                "rejected" => $"The upgrade was refused before anything was changed: {plan.Reason}",
                "failed" => $"The upgrade failed and needs an administrator: {plan.Reason}",
                "applying" => "The program is being swapped; the server is unreachable while it restarts.",
                "draining" => "Stopping new work and waiting for the work running to finish.",
                _ => null,
            };
        }
        else if (_problem is not null) node["message"] = _problem;
        return node;
    }
}
