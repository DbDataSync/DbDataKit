using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Runner;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Runner.Staging;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Runner.Api;

/// <summary>A publishing call that is refused or fails, with the HTTP status the API answers it with (400 invalid, 403 policy, 404 unknown, 409 stale, 413 too large).</summary>
public sealed class RunnerPublishException(int status, string message) : InvalidOperationException(message)
{
    public int Status { get; } = status;
}

/// <summary>One file of a version as the publisher lists it: path inside the package, SHA-256 in hexadecimal, size and media type.</summary>
public sealed record PublishFile(string Path, string Sha256, long Size, string? MediaType);

/// <summary>A publish: the manifest text (package/1 YAML) and files of the next version of the <c>integration</c> <paramref name="Name"/>.</summary>
/// <param name="Mode">Null takes the kind's default (<c>serve</c> until staging activates by itself, R17).</param>
/// <param name="ExpectedVersion">The highest version number the package has, as the publisher saw it (0 for a first publish); null does not check.</param>
/// <param name="Format">The format of the manifest (24a UC01): the body's <c>format</c>; null takes the manifest's own <c>schemaVersion</c>, or 1. A format this runner does not read is refused naming both numbers.</param>
public sealed record PublishInput(string Name, string Manifest, IReadOnlyList<PublishFile> Files, string Channel, PublishMode? Mode = null, IReadOnlyList<string>? DependsOn = null,
    int? ExpectedVersion = null, string? Note = null, string? Signature = null, int? Format = null);

/// <summary>What a publish did. <see cref="Signature"/> is what the signature check found (<c>unsigned</c> until the verifier of plan 30 section 6.2 exists).</summary>
public sealed record PublishOutcome(string Package, string Channel, int Version, bool Changed, PublishMode Mode, string Signature, IReadOnlyList<string> Tasks);

/// <summary>What an administrator says to an activation: the losses confirmed (with the list echoed), or the choice to activate although instances failed.</summary>
public sealed record ActivateOptions(bool ConfirmLosses = false, IReadOnlyList<string>? Losses = null, bool Anyway = false, string? Reason = null);

/// <summary>What an upload did: the file's hash and size, and whether the runner had it already (nothing was read then).</summary>
public sealed record ArtifactStored(string Sha256, long Size, bool Already);

/// <summary>
/// Plan 30's publishing for the <c>integration</c> kind on a runner (plan 28a R16), as a service layer the management API and the development command <c>package install</c> both call, so both take the same path: artifacts
/// streamed into the payload store by hash and checked, the publish validated by the kind's validator, the life cycle with <c>expected_version</c>, bindings, the task projection rebuilt for the package and channel whenever
/// what a channel serves changes (so the scheduler picks the new version up, or drops a retired one), and every change (and every refusal) in the audit with who, kind, name, version, channel and outcome, never file content.
/// </summary>
public sealed partial class RunnerPublishing
{
    public const string Kind = RunnerPackages.Kind;
    public const string PublishVerb = "runner.package.publish";
    public const string InstallVerb = "runner.package.install";
    public const string UploadVerb = "runner.artifact.upload";
    public const string ActivateVerb = "runner.package.activate";
    public const string SwitchVerb = "runner.package.switch";
    public const string RollbackVerb = "runner.package.rollback";
    public const string RetireVerb = "runner.package.retire";
    public const string BindVerb = "runner.package.bind";
    public const string SettingVerb = "runner.setting.set";

    /// <summary>The owner of uploaded files in the payload store.</summary>
    public const string PayloadOwner = "runner";

    public const string RequireSignaturesSetting = "require_signatures";

    /// <summary>The default size cap of one uploaded file: 512 MiB.</summary>
    public const long DefaultMaxArtifactBytes = 512L * 1024 * 1024;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Rule();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex SlotRule();

    private readonly StateDatabase _state;
    private readonly PayloadStore _payloads;
    private readonly DefinitionStore _store;
    private readonly TaskProjections _projections;
    private readonly AuditLog _audit;
    private readonly DbDataKit.Web.Trust.GroupSettings _settings;
    private readonly IVersionSignatureVerifier _verifier;
    private readonly RunnerInstances _instances;
    private readonly TimeSpan _lease;

    /// <param name="instanceLease">How long an instance may be silent before it is not live to the group (what activation counts as "every live instance").</param>
    /// <param name="activation">How an activation waits for runs in flight; the defaults when null.</param>
    public RunnerPublishing(StateDatabase state, long maxArtifactBytes = DefaultMaxArtifactBytes, IVersionSignatureVerifier? verifier = null, TimeSpan? instanceLease = null, ActivationOptions? activation = null, string serviceId = RunnerNames.ServiceId)
    {
        _lease = instanceLease ?? TimeSpan.FromSeconds(90);
        _instances = new RunnerInstances(state);
        _state = state;
        MaxArtifactBytes = maxArtifactBytes;
        _verifier = verifier ?? new NoSignatureVerifier();
        _payloads = new PayloadStore(state);
        _store = new DefinitionStore(state);
        Publisher = new DefinitionPublisher(state, RunnerPackages.Kinds(), _payloads, "runner");
        Activator = new PackageActivator(state, Publisher, activation);
        _projections = new TaskProjections(state);
        _audit = new AuditLog(state, serviceId);
        _settings = new DbDataKit.Web.Trust.GroupSettings(state);
    }

    public DefinitionPublisher Publisher { get; }

    /// <summary>
    /// A host's rule about the channels it serves (the console's embedded runner serves no production channel until an administrator chose it): says why a channel is refused, or null. Asked by every publish, activate, switch and
    /// rollback, which are refused with 403 and the message when it answers.
    /// </summary>
    public Func<string, string?>? ChannelRefusal { get; init; }

    private void RequireChannelServed(string channel)
    {
        if (ChannelRefusal?.Invoke(channel) is { } refusal) throw new RunnerPublishException(403, refusal);
    }

    /// <summary>Activation, switch and rollback with the progress migration (plan 28a R18).</summary>
    public PackageActivator Activator { get; }

    /// <summary>The ids of the instances that are live now (inside their lease): what "every instance is ready" is counted over.</summary>
    public IReadOnlyList<string> LiveInstanceIds() => [.. _instances.LiveInstances(_lease).Select(i => i.Instance.Id.ToString("D"))];

    /// <summary>The size cap of one uploaded file, in bytes.</summary>
    public long MaxArtifactBytes { get; }

    // ---- settings

    /// <summary>The administrator's setting: refuse a version that is not signed. Shared by the group (kept in the state).</summary>
    public bool RequireSignatures => _settings.Get(RequireSignaturesSetting) == "true";

    public void SetRequireSignatures(bool value, RunnerActor actor)
    {
        var text = value ? "true" : "false";
        var current = _settings.Get(RequireSignaturesSetting);
        if (current is null) _settings.Put(RequireSignaturesSetting, text);
        else if (current != text) _settings.Put(RequireSignaturesSetting, text, current);
        Record(SettingVerb, actor, "setting", RequireSignaturesSetting, AuditEvent.Ok, new { value });
    }

    // ---- ownership (OwnerScope: a publisher changes only the packages it created)

    private static string OwnerKey(string name) => $"owner/{Kind}/{name}";

    /// <summary>The id of the client that created the package, null for one an administrator made (or none yet).</summary>
    public string? Owner(string name) => _settings.Get(OwnerKey(name));

    /// <summary>Records <paramref name="clientId"/> as the owner of a new package; the first claim stands. True when it is the owner now.</summary>
    public bool ClaimOwner(string name, string clientId) => _settings.Put(OwnerKey(name), clientId) == clientId;

    public bool Exists(string name) => _store.Versions(Kind, name).Count > 0;

    // ---- artifacts

    private Guid? Find(string sha) => Publisher.FindArtifact(sha) ?? _payloads.FindComplete(sha, PayloadOwner);

    /// <summary>Whether a file with this hash is stored (by a version or by an upload not yet named by one).</summary>
    public bool HasArtifact(string sha256) => Sha256Rule().IsMatch(sha256.ToLowerInvariant()) && Find(sha256.ToLowerInvariant()) is not null;

    public static bool IsSha256(string text) => Sha256Rule().IsMatch(text.ToLowerInvariant());

    /// <summary>
    /// Streams <paramref name="body"/> into the payload store (one buffer and one chunk in memory, whatever the size), hashing as it goes. When the hash of what arrived is not <paramref name="sha256"/>, nothing is kept and the
    /// refusal names both hashes; a file past <see cref="MaxArtifactBytes"/> is refused with 413. A file the runner has already is not read.
    /// </summary>
    public async Task<ArtifactStored> StoreArtifactAsync(string sha256, Stream body, string? mediaType, RunnerActor actor, CancellationToken ct = default, bool audit = true)
    {
        var sha = sha256.ToLowerInvariant();
        if (!Sha256Rule().IsMatch(sha)) throw new RunnerPublishException(400, $"'{sha256}' is not a SHA-256: write 64 hexadecimal digits.");
        if (Find(sha) is { } existing) return new ArtifactStored(sha, _payloads.Get(existing)?.Size ?? 0, true);

        var writer = _payloads.OpenWrite(new PayloadOptions(mediaType, PayloadOwner, $"upload/{sha}", actor.Id, MaxBytes: MaxArtifactBytes, Label: $"the upload {sha}"));
        var id = writer.Id;
        string? actual = null;
        long size;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await writer.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            size = writer.Length;
            actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual == sha) writer.Complete();
        }
        catch (PayloadTooLargeException)
        {
            await writer.DisposeAsync();
            _payloads.Delete(id);
            var message = $"The file named {sha} is larger than the cap of {MaxArtifactBytes} bytes.";
            Record(UploadVerb, actor, "artifact", sha, AuditEvent.Refused, new { reason = "too large", max_bytes = MaxArtifactBytes });
            throw new RunnerPublishException(413, message);
        }
        catch
        {
            await writer.DisposeAsync();
            _payloads.Delete(id);
            throw;
        }

        await writer.DisposeAsync();
        if (actual != sha)
        {
            _payloads.Delete(id);
            var message = $"The bytes received hash to {actual}, not to {sha}, the hash the upload names; nothing was stored.";
            Record(UploadVerb, actor, "artifact", sha, AuditEvent.Refused, new { reason = "hash mismatch", sha256_named = sha, sha256_received = actual, size });
            throw new RunnerPublishException(400, message);
        }

        if (audit) Record(UploadVerb, actor, "artifact", sha, AuditEvent.Ok, new { size });
        return new ArtifactStored(sha, size, false);
    }

    // ---- publish

    /// <summary>Validates and stores the next version of an <c>integration</c> and serves or stages it. A served version replaces the channel's task projection; a staged one is not run until it is activated.</summary>
    public PublishOutcome Publish(PublishInput input, RunnerActor actor, string verb = PublishVerb)
    {
        var target = $"{input.Name}/{input.Channel}";
        try
        {
            if (!PackageManifestValidator.NameRule.IsMatch(input.Name)) throw new RunnerPublishException(400, $"'{input.Name}' is not a package name: use 1 to 64 lowercase letters, digits, '-' or '_', starting with a letter or digit.");
            if (!PackageManifestValidator.NameRule.IsMatch(input.Channel)) throw new RunnerPublishException(400, $"'{input.Channel}' is not a channel name: use 1 to 64 lowercase letters, digits, '-' or '_', starting with a letter or digit.");

            RequireChannelServed(input.Channel);
            var refs = new List<ArtifactRef>();
            foreach (var file in input.Files)
            {
                var sha = file.Sha256.ToLowerInvariant();
                refs.Add(new ArtifactRef(file.Path, sha, file.Size, file.MediaType, Sha256Rule().IsMatch(sha) ? Find(sha) : null));
            }

            // the signature (plan 30 section 6.2) is not verified in this slice: the default verifier records 'unsigned', and a group that requires signatures refuses what it cannot verify
            var verdict = _verifier.Verify(new VersionSignatureRequest(Kind, input.Name, input.Manifest, refs, input.Signature));
            if (RequireSignatures && verdict.State != SignatureState.Verified)
                throw new RunnerPublishException(403, $"This runner requires signed versions (require_signatures is on) and the {Kind} '{input.Name}' has no valid signature: the signature of its manifest, which lists every file's path and SHA-256, is missing"
                    + (verdict.Message is null ? "" : $" ({verdict.Message})") + ". Publish it signed, or have an administrator turn require_signatures off.");

            // a trigger loop across the packages of the channel is refused before anything is published, naming the loop (plan 28a R13)
            if (PackageManifestReader.Load(input.Manifest, $"{Kind}/{input.Name}") is { Ok: true, Manifest: { } candidate })
            {
                try { ProjectionDocuments.RequireNoCycle(_projections.ListByChannel(input.Channel), input.Name, input.Channel, ProjectionDocuments.RowsOf(candidate, "0")); }
                catch (TriggerCycleException ex) { throw new RunnerPublishException(400, ex.Message); }
            }

            PublishResult result;
            try
            {
                result = Publisher.Publish(new PublishRequest(Kind, input.Name, input.Manifest, actor.Id, input.Channel, input.Mode, input.DependsOn, input.Note, refs, ExpectedVersion: input.ExpectedVersion, Format: input.Format));
            }
            catch (Exception ex) when (Translate(ex) is { } translated) { throw translated; }

            if (result.Mode == PublishMode.Serve) RebuildProjection(input.Name, input.Channel);
            var tasks = TasksOf(input.Manifest);
            Record(verb, actor, "package", target, AuditEvent.Ok,
                new { kind = Kind, name = input.Name, version = result.Version.Version, channel = input.Channel, mode = result.Mode.ToString().ToLowerInvariant(), changed = result.Changed, files = refs.Count, signature = verdict.Word });
            return new PublishOutcome(input.Name, input.Channel, result.Version.Version, result.Changed, result.Mode, verdict.Word, tasks);
        }
        catch (RunnerPublishException ex)
        {
            Record(verb, actor, "package", target, AuditEvent.Refused, new { kind = Kind, name = input.Name, channel = input.Channel, status = ex.Status, reason = ex.Message });
            throw;
        }
    }

    // ---- life cycle

    /// <summary>
    /// Makes the staged version the channel's active one, as an administrator's act: when every live instance is ready (or <see cref="ActivateOptions.Anyway"/>, a recorded choice: the instances that failed keep the previous version and say so),
    /// and, when the dry run listed losses, only with <see cref="ActivateOptions.ConfirmLosses"/> and the list echoed. It waits for the runs in flight, migrates the progress and rebuilds the projection in one transaction. The returned outcome says
    /// when it is still waiting for runs.
    /// </summary>
    public Task<ActivationOutcome> ActivateAsync(string name, string channel, int? expectedVersion, RunnerActor actor, ActivateOptions? options = null, CancellationToken ct = default) =>
        ActAsync(ActivateVerb, name, channel, actor, async () =>
        {
            options ??= new ActivateOptions();
            var preview = Activator.Preview(name, channel) ?? throw new RunnerPublishException(404, $"The integration '{name}' has nothing staged in channel '{channel}'.");
            var confirmed = false;
            if (preview.Losses.Count > 0)
            {
                if (!options.ConfirmLosses)
                    throw new RunnerPublishException(409, $"Version {preview.Version} of '{name}' loses progress: {string.Join("; ", preview.Losses)}. To activate it anyway, send confirm_losses: true with the same list in losses.");
                if (options.Losses is null || !preview.Losses.OrderBy(l => l, StringComparer.Ordinal).SequenceEqual(options.Losses.OrderBy(l => l, StringComparer.Ordinal), StringComparer.Ordinal))
                    throw new RunnerPublishException(409, $"The losses you confirmed are not the ones version {preview.Version} of '{name}' has now. It loses: {string.Join("; ", preview.Losses)}. Send them in losses with confirm_losses: true.");
                confirmed = true;
            }

            var outcome = await Activator.ActivateAsync(name, channel, new ActivationRequest(options.Anyway ? ActivationMode.Anyway : ActivationMode.Administrator, actor.Id, LiveInstanceIds(), confirmed, options.Reason, expectedVersion), ct).ConfigureAwait(false);
            return Settled(name, outcome, anywayHint: !options.Anyway);
        }, o => new { version = o.Version, state = o.State.ToString(), anyway = options?.Anyway ?? false, confirmed_losses = o.Losses.ToArray(), busy_tasks = o.BusyTasks.ToArray() }, rebuild: false);

    /// <summary>Points the channel at any stored version (promotion), migrating the progress of every task (a migration that loses progress needs <see cref="ActivateOptions.ConfirmLosses"/> and the list echoed).</summary>
    public Task<ActivationOutcome> SwitchAsync(string name, string channel, int version, int? expectedVersion, RunnerActor actor, ActivateOptions? options = null, CancellationToken ct = default) =>
        ActAsync(SwitchVerb, name, channel, actor, async () =>
        {
            options ??= new ActivateOptions();
            var outcome = await Activator.SwitchAsync(name, channel, version, new ActivationRequest(ActivationMode.Administrator, actor.Id, LiveInstanceIds(), options.ConfirmLosses, null, expectedVersion), ct).ConfigureAwait(false);
            return Settled(name, outcome, anywayHint: false);
        }, o => new { version = o.Version, state = o.State.ToString(), confirmed_losses = o.Losses.ToArray(), busy_tasks = o.BusyTasks.ToArray() }, rebuild: false);

    /// <summary>Returns the channel to its previous version; the progress the version being left migrated is put back from its history, in one transaction with the pointer and the projection.</summary>
    public Task<ActivationOutcome> RollbackAsync(string name, string channel, int? expectedVersion, RunnerActor actor, ActivateOptions? options = null, CancellationToken ct = default) =>
        ActAsync(RollbackVerb, name, channel, actor, async () =>
        {
            options ??= new ActivateOptions();
            var outcome = await Activator.RollbackAsync(name, channel, new ActivationRequest(ActivationMode.Administrator, actor.Id, LiveInstanceIds(), options.ConfirmLosses, null, expectedVersion), ct).ConfigureAwait(false);
            return Settled(name, outcome, anywayHint: false);
        }, o => new { version = o.Version, state = o.State.ToString(), confirmed_losses = o.Losses.ToArray(), busy_tasks = o.BusyTasks.ToArray() }, rebuild: false);

    /// <summary>What an activation that did not happen means to the caller: a refusal with its status, or (still waiting for runs) an outcome to show.</summary>
    private static ActivationOutcome Settled(string name, ActivationOutcome outcome, bool anywayHint) => outcome.State switch
    {
        ActivationState.Activated or ActivationState.WaitingForRuns => outcome,
        ActivationState.NothingStaged => throw new RunnerPublishException(404, outcome.Message),
        ActivationState.NotReady => throw new RunnerPublishException(409, outcome.Message + (anywayHint ? " An administrator can activate it anyway (anyway: true): the instances that failed keep the previous version and say so, and the choice is recorded." : "")),
        ActivationState.AlreadyDone => throw new RunnerPublishException(409, outcome.Message),
        _ => throw new RunnerPublishException(409, outcome.Message),
    };

    private async Task<T> ActAsync<T>(string verb, string name, string channel, RunnerActor actor, Func<Task<T>> work, Func<T, object> detail, bool rebuild)
    {
        var target = $"{name}/{channel}";
        try
        {
            if (!PackageManifestValidator.NameRule.IsMatch(name)) throw new RunnerPublishException(400, $"'{name}' is not a package name.");
            if (!PackageManifestValidator.NameRule.IsMatch(channel)) throw new RunnerPublishException(400, $"'{channel}' is not a channel name.");
            RequireChannelServed(channel);
            T result;
            try { result = await work().ConfigureAwait(false); }
            catch (ProgressMigrationException ex) { throw new RunnerPublishException(400, ex.Message); }
            catch (Exception ex) when (Translate(ex) is { } translated) { throw translated; }
            if (rebuild) RebuildProjection(name, channel);
            Record(verb, actor, "package", target, AuditEvent.Ok, Merge(new { kind = Kind, name, channel }, detail(result)));
            return result;
        }
        catch (RunnerPublishException ex)
        {
            Record(verb, actor, "package", target, AuditEvent.Refused, new { kind = Kind, name, channel, status = ex.Status, reason = ex.Message });
            throw;
        }
    }

    /// <summary>Stops serving the package in the channel and drops its schedule there; the versions stay. False when the channel had nothing.</summary>
    public bool Retire(string name, string channel, int? expectedVersion, RunnerActor actor) =>
        Act(RetireVerb, name, channel, actor, () => Publisher.Retire(Kind, name, actor.Id, channel, expectedVersion), retired => new { retired });

    private T Act<T>(string verb, string name, string channel, RunnerActor actor, Func<T> work, Func<T, object> detail)
    {
        var target = $"{name}/{channel}";
        try
        {
            if (!PackageManifestValidator.NameRule.IsMatch(name)) throw new RunnerPublishException(400, $"'{name}' is not a package name.");
            if (!PackageManifestValidator.NameRule.IsMatch(channel)) throw new RunnerPublishException(400, $"'{channel}' is not a channel name.");
            T result;
            try { result = work(); }
            catch (Exception ex) when (Translate(ex) is { } translated) { throw translated; }
            RebuildProjection(name, channel);
            Record(verb, actor, "package", target, AuditEvent.Ok, Merge(new { kind = Kind, name, channel }, detail(result)));
            return result;
        }
        catch (RunnerPublishException ex)
        {
            Record(verb, actor, "package", target, AuditEvent.Refused, new { kind = Kind, name, channel, status = ex.Status, reason = ex.Message });
            throw;
        }
    }

    private static JsonObject Merge(object first, object second)
    {
        var result = JsonSerializer.SerializeToNode(first)!.AsObject();
        foreach (var (key, value) in JsonSerializer.SerializeToNode(second)!.AsObject()) result[key] = value?.DeepClone();
        return result;
    }

    private static RunnerPublishException? Translate(Exception ex) => ex switch
    {
        DefinitionConflictException => new RunnerPublishException(409, ex.Message),
        DefinitionNotFoundException => new RunnerPublishException(404, ex.Message),
        DefinitionRejectedException => new RunnerPublishException(400, ex.Message),
        ArgumentException => new RunnerPublishException(400, ex.Message),
        _ => null,
    };

    /// <summary>
    /// Replaces the channel's task projection with the tasks of the version the channel serves now (what the scheduler reads), or removes it when the channel serves nothing. It reads the head again, so whichever
    /// change comes last leaves the projection of what is served.
    /// </summary>
    public void RebuildProjection(string name, string channel)
    {
        IReadOnlyList<TaskProjectionRow> rows = [];
        if (Publisher.Head(Kind, name, channel)?.Active is { } active && _store.Get(Kind, name, active) is { } version)
        {
            var manifest = PackageManifestReader.Load(version.Content, $"{Kind}/{name}@{active}").ThrowIfInvalid();
            rows = ProjectionDocuments.RowsOf(manifest, active.ToString(CultureInfo.InvariantCulture));
        }

        _projections.Replace(name, channel, rows);
    }

    private static IReadOnlyList<string> TasksOf(string manifestText) =>
        PackageManifestReader.Load(manifestText, "manifest") is { Ok: true, Manifest: { } m } ? [.. m.Tasks.Select(t => t.Name)] : [];

    // ---- bindings

    /// <summary>Fills slots for a channel (slot name to what it is bound to); the package must exist. Names, never values, are audited.</summary>
    public IReadOnlyList<DefinitionBinding> Bind(string name, string channel, IReadOnlyDictionary<string, string> slots, int? expectedRevision, RunnerActor actor)
    {
        var target = $"{name}/{channel}";
        try
        {
            if (!PackageManifestValidator.NameRule.IsMatch(name)) throw new RunnerPublishException(400, $"'{name}' is not a package name.");
            if (!PackageManifestValidator.NameRule.IsMatch(channel)) throw new RunnerPublishException(400, $"'{channel}' is not a channel name.");
            if (!Exists(name)) throw new RunnerPublishException(404, $"There is no {Kind} '{name}'; publish it before binding its slots.");
            if (slots.Count == 0) throw new RunnerPublishException(400, "The body names no slot: {\"slots\": {\"<slot>\": \"<what it is bound to>\"}}.");
            foreach (var (slot, to) in slots)
            {
                if (!SlotRule().IsMatch(slot)) throw new RunnerPublishException(400, $"'{slot}' is not a slot name.");
                if (string.IsNullOrWhiteSpace(to)) throw new RunnerPublishException(400, $"The slot '{slot}' must be bound to something.");
            }

            try
            {
                foreach (var (slot, to) in slots.OrderBy(s => s.Key, StringComparer.Ordinal)) Publisher.Bind(channel, Kind, name, slot, to, actor.Id, expectedRevision);
            }
            catch (Exception ex) when (Translate(ex) is { } translated) { throw translated; }

            Record(BindVerb, actor, "package", target, AuditEvent.Ok, new { kind = Kind, name, channel, slots = slots.Keys.Order(StringComparer.Ordinal).ToArray() });
            return Publisher.Bindings(channel, Kind, name);
        }
        catch (RunnerPublishException ex)
        {
            Record(BindVerb, actor, "package", target, AuditEvent.Refused, new { kind = Kind, name, channel, status = ex.Status, reason = ex.Message });
            throw;
        }
    }

    // ---- reading

    /// <summary>Every integration with what each channel serves.</summary>
    public JsonObject List()
    {
        var items = new JsonArray();
        foreach (var name in Publisher.Names(Kind))
            items.Add(new JsonObject
            {
                ["name"] = name,
                ["latest_version"] = _store.Versions(Kind, name).Max(v => v.Version),
                ["owner"] = Owner(name),
                ["channels"] = Channels(name),
            });
        return new JsonObject { ["kind"] = Kind, ["definitions"] = items };
    }

    private JsonArray Channels(string name) => new([.. Publisher.HeadsOf(Kind, name).Select(h => (JsonNode)new JsonObject { ["channel"] = h.Channel, ["active"] = h.Active, ["previous"] = h.Previous, ["staged"] = h.Staged })]);

    /// <summary>For each channel with a staged version: how far the instances are, and the progress its activation would lose (what an administrator confirms).</summary>
    private JsonArray Staging(string name) => new([.. Publisher.HeadsOf(Kind, name).Where(h => h.Staged is not null).Select(h =>
    {
        var progress = Publisher.Status.Progress(Kind, name, h.Channel, LiveInstanceIds());
        var preview = Activator.Preview(name, h.Channel);
        return (JsonNode)new JsonObject
        {
            ["channel"] = h.Channel, ["version"] = h.Staged, ["summary"] = progress.Describe(), ["all_ready"] = progress.IsAllReady,
            ["failed"] = new JsonArray([.. progress.Failed.Select(f => (JsonNode)new JsonObject { ["instance"] = f.Instance, ["error"] = f.Error })]),
            ["losses"] = new JsonArray([.. (preview?.Losses ?? []).Select(l => (JsonNode)JsonValue.Create(l)!)]),
            ["needs_confirmation"] = (preview?.Losses.Count ?? 0) > 0,
        };
    })]);

    /// <summary>One integration: its versions, what each channel serves (active, previous, staged), the bindings, what each instance reports and the latest events. Null when there is no such package.</summary>
    public JsonObject? Describe(string name)
    {
        var versions = _store.Versions(Kind, name);
        if (versions.Count == 0) return null;
        var heads = Publisher.HeadsOf(Kind, name);
        return new JsonObject
        {
            ["kind"] = Kind,
            ["name"] = name,
            ["owner"] = Owner(name),
            ["latest_version"] = versions.Max(v => v.Version),
            ["versions"] = new JsonArray([.. versions.OrderByDescending(v => v.Version).Select(v => (JsonNode)new JsonObject
            {
                ["version"] = v.Version, ["hash"] = v.Hash, ["published_by"] = v.PublishedBy, ["published_at"] = v.PublishedAt.UtcDateTime.ToString("O"), ["note"] = v.Note,
                ["depends_on"] = new JsonArray([.. v.DependsOn.Select(d => (JsonNode)JsonValue.Create(d)!)]), ["files"] = Publisher.Artifacts(Kind, name, v.Version).Count,
            })]),
            ["channels"] = Channels(name),
            ["staging"] = Staging(name),
            ["bindings"] = new JsonArray([.. heads.SelectMany(h => Publisher.Bindings(h.Channel, Kind, name)).Select(b => (JsonNode)new JsonObject { ["channel"] = b.Channel, ["slot"] = b.Slot, ["bound_to"] = b.BoundTo, ["revision"] = b.Revision })]),
            ["instances"] = new JsonArray([.. heads.SelectMany(h => Publisher.Status.List(Kind, name, h.Channel)).Select(s => (JsonNode)new JsonObject
            {
                ["instance"] = s.Instance, ["channel"] = s.Channel, ["version"] = s.Version, ["state"] = s.State, ["error"] = s.Error, ["note"] = s.Note, ["has_losses"] = s.HasLosses, ["at"] = s.At.UtcDateTime.ToString("O"), ["gone"] = s.Gone,
            })]),
            ["events"] = new JsonArray([.. Publisher.EventsOf(Kind, name).Select(e => (JsonNode)new JsonObject
            {
                ["id"] = e.Id, ["action"] = e.Action, ["channel"] = e.Channel, ["version"] = e.Version, ["actor"] = e.ActorId, ["at"] = e.At.UtcDateTime.ToString("O"), ["note"] = e.Note,
            })]),
        };
    }

    // ---- audit

    /// <summary>Notes a call that a role or an owner rule refused: the call changed nothing.</summary>
    public void AuditRefused(string verb, RunnerActor actor, string targetKind, string targetId, string reason) =>
        Record(verb, actor, targetKind, targetId, AuditEvent.Refused, new { kind = Kind, status = 403, reason });

    private void Record(string verb, RunnerActor actor, string targetKind, string targetId, string outcome, object? detail) =>
        _audit.Record(new AuditEvent("person", actor.Id, verb, outcome, ViaKind: actor.ViaKind, ViaId: actor.ViaId, TargetKind: targetKind, TargetId: targetId,
            Detail: detail is null ? null : JsonSerializer.Serialize(detail)));
}
