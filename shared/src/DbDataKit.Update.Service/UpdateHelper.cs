using System.Security.Cryptography;

namespace DbDataKit.Update.Service;

public interface IServiceControl
{
    Task<bool> IsRunningAsync(CancellationToken ct);
    Task StopAsync(TimeSpan timeout, CancellationToken ct);
    Task StartAsync(CancellationToken ct);
}

public sealed record HealthResult(bool Healthy, string? Detail = null);

public interface IHealthProbe
{
    /// <summary>Wait until the service reports <paramref name="expectedVersion"/> ready, or the timeout elapses.</summary>
    Task<HealthResult> WaitHealthyAsync(string expectedVersion, string? healthUrl, TimeSpan timeout, CancellationToken ct);
}

public sealed record MigrationContext(InstallLayout Layout, UpdatePlan Plan, string NewVersionDir, string ConfigDir);

public interface IMigrationRunner
{
    /// <summary>True if any step cannot be undone by restoring the config backup (e.g. a data schema change). Must be idempotent per step.</summary>
    bool HasIrreversibleSteps(UpdatePlan plan);
    Task MigrateAsync(MigrationContext context, CancellationToken ct);
}

public interface IPreUpdateBackup
{
    /// <summary>Product pre-update backup hook (e.g. database backup). Returns true only if the backup was verified.</summary>
    Task<bool> BackupAsync(MigrationContext context, CancellationToken ct);
}

public sealed class NoMigrations : IMigrationRunner
{
    public bool HasIrreversibleSteps(UpdatePlan plan) => false;
    public Task MigrateAsync(MigrationContext context, CancellationToken ct) => Task.CompletedTask;
}

public enum UpdateOutcomeKind { NoPlan, Committed, RolledBack, Rejected, NeedsOperator }

public sealed record UpdateOutcome(UpdateOutcomeKind Kind, string Message);

public sealed record UpdateHelperOptions
{
    public string Product { get; init; } = "";
    /// <summary>Channel this machine follows (machine configuration, not the plan).</summary>
    public Channel Channel { get; init; } = Channel.Stable;
    /// <summary>Set only by an interactive administrator (e.g. <c>dbdata-updater --allow-downgrade</c>).</summary>
    public bool AllowDowngrade { get; init; }
    public string? UpdaterVersion { get; init; }
    public string? KnownMinimumSafeVersion { get; init; }
    public int RetainVersions { get; init; } = 2;
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// The privileged helper's state machine: verify -> stop -> backup -> migrate -> repoint -> start -> health -> commit, or rollback.
/// It trusts nothing in the plan: it re-verifies manifest signature, artifact hash and downgrade rules itself.
/// Cancellation (<see cref="OperationCanceledException"/>) models an interruption (power loss, killed helper): the journal is left as-is
/// and the next run decides via <see cref="RecoveryPlanner"/>.
/// </summary>
public sealed class UpdateHelper(
    InstallLayout layout, ICurrentPointer pointer, IServiceControl service, IHealthProbe health,
    IMigrationRunner migrations, TrustStore trust, UpdateHelperOptions options, IPreUpdateBackup? preBackup = null, Action<string>? log = null)
{
    private void Log(string m) => log?.Invoke(m);

    public async Task<UpdateOutcome> RunAsync(CancellationToken ct = default)
    {
        UpdatePlan? plan;
        try { plan = PlanStore.Load(layout); }
        catch (UpdatePlanException ex) { return Quarantine(ex.Message); }
        if (plan is null) return new(UpdateOutcomeKind.NoPlan, "No update plan.");

        try { PlanStore.Validate(layout, plan, options.Product); }
        catch (UpdatePlanException ex)
        {
            plan.State = PlanState.Rejected; plan.Reason = ex.Message;
            return Finish(plan, UpdateOutcomeKind.Rejected, "Rejected: " + ex.Message, safeSave: true);
        }

        switch (RecoveryPlanner.Decide(plan))
        {
            case RecoveryAction.NothingToDo:
                return new(plan.State == PlanState.Committed ? UpdateOutcomeKind.Committed : plan.State == PlanState.RolledBack ? UpdateOutcomeKind.RolledBack : UpdateOutcomeKind.Rejected, "Plan already finished: " + plan.State);
            case RecoveryAction.NeedsOperator:
                return new(UpdateOutcomeKind.NeedsOperator, "Update needs operator attention: " + (plan.Reason ?? plan.State.ToString()));
            case RecoveryAction.Rollback:
                return await RollbackAsync(plan, plan.Reason ?? "Recovered from an interrupted update.", ct).ConfigureAwait(false);
            default:
                return await ForwardAsync(plan, ct).ConfigureAwait(false);
        }
    }

    private async Task<UpdateOutcome> ForwardAsync(UpdatePlan plan, CancellationToken ct)
    {
        plan.State = PlanState.InProgress;
        PlanStore.Save(layout, plan);
        var newDir = layout.VersionDir(plan.To);

        // Verify happens before the service is touched; a failure here changes nothing on the machine.
        if (plan.StateOf(StepName.Verify) != StepState.Done)
        {
            try { await RunStep(plan, StepName.Verify, () => VerifyAndStageAsync(plan, newDir, ct)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is UpdateVerificationException or UpdatePlanException or FormatException or IOException or InvalidDataException)
            {
                plan.State = PlanState.Rejected; plan.Reason = ex.Message;
                TryDeleteDir(newDir + ".extracting");
                return Finish(plan, UpdateOutcomeKind.Rejected, "Rejected: " + ex.Message);
            }
        }

        var ctx = new MigrationContext(layout, plan, newDir, layout.ConfigDir);
        try
        {
            if (plan.StateOf(StepName.Stop) != StepState.Done)
                await RunStep(plan, StepName.Stop, async () =>
                {
                    plan.ServiceWasRunning = await service.IsRunningAsync(ct).ConfigureAwait(false);
                    if (plan.ServiceWasRunning) await service.StopAsync(options.StopTimeout, ct).ConfigureAwait(false);
                }).ConfigureAwait(false);

            if (plan.StateOf(StepName.Backup) != StepState.Done)
                await RunStep(plan, StepName.Backup, async () =>
                {
                    CopyDirectory(layout.ConfigDir, ConfigBackup(plan));
                    if (migrations.HasIrreversibleSteps(plan))
                    {
                        if (preBackup is null || !await preBackup.BackupAsync(ctx, ct).ConfigureAwait(false))
                            throw new InvalidOperationException("An irreversible migration requires a verified pre-update backup, which is not available.");
                    }
                    else if (preBackup is not null) await preBackup.BackupAsync(ctx, ct).ConfigureAwait(false);
                }).ConfigureAwait(false);

            if (plan.StateOf(StepName.Migrate) != StepState.Done)
                await RunStep(plan, StepName.Migrate, async () =>
                {
                    if (migrations.HasIrreversibleSteps(plan)) { plan.IrreversibleMigrationStarted = true; PlanStore.Save(layout, plan); }
                    await migrations.MigrateAsync(ctx, ct).ConfigureAwait(false);
                }).ConfigureAwait(false);

            if (plan.StateOf(StepName.Repoint) != StepState.Done)
                await RunStep(plan, StepName.Repoint, () => { pointer.Set(plan.To); return Task.CompletedTask; }).ConfigureAwait(false);

            if (plan.StateOf(StepName.Start) != StepState.Done)
                await RunStep(plan, StepName.Start, () => service.StartAsync(ct)).ConfigureAwait(false);

            if (plan.StateOf(StepName.HealthCheck) != StepState.Done)
                await RunStep(plan, StepName.HealthCheck, async () =>
                {
                    var r = await health.WaitHealthyAsync(plan.To, plan.HealthUrl, TimeSpan.FromSeconds(plan.HealthTimeoutSeconds), ct).ConfigureAwait(false);
                    if (!r.Healthy) throw new InvalidOperationException("Health check failed: " + (r.Detail ?? "service did not report ready"));
                }).ConfigureAwait(false);

            await RunStep(plan, StepName.Commit, () => { Prune(plan); return Task.CompletedTask; }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; } // interruption: leave the journal for recovery
        catch (Exception ex)
        {
            Log("Update failed: " + ex.Message);
            plan.Reason = ex.Message;
            if (plan.IrreversibleMigrationStarted)
            {
                plan.State = PlanState.Failed;
                return Finish(plan, UpdateOutcomeKind.NeedsOperator, "Failed after an irreversible migration; automatic rollback is not possible: " + ex.Message);
            }
            return await RollbackAsync(plan, ex.Message, ct).ConfigureAwait(false);
        }

        plan.State = PlanState.Committed;
        return Finish(plan, UpdateOutcomeKind.Committed, $"Updated {plan.From} -> {plan.To}.");
    }

    private async Task VerifyAndStageAsync(UpdatePlan plan, string newDir, CancellationToken ct)
    {
        var manifestBytes = await File.ReadAllBytesAsync(layout.ResolveUnderUpdates(plan.ManifestFile), ct).ConfigureAwait(false);
        var sig = await File.ReadAllTextAsync(layout.ResolveUnderUpdates(plan.SignatureFile), ct).ConfigureAwait(false);
        var v = ReleaseVerifier.VerifyManifest(manifestBytes, sig, trust, options.Product);
        if (!string.Equals(v.Sha256, plan.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdateVerificationException(VerificationFailure.HashMismatch, "Manifest hash differs from the one recorded in the plan.");
        var m = v.Manifest;
        if (!string.Equals(m.Version, plan.To, StringComparison.OrdinalIgnoreCase) && !m.ParsedVersion.Equals(ReleaseVersion.Parse(plan.To)))
            throw new UpdateVerificationException(VerificationFailure.VersionMismatch, "Signed manifest version differs from the plan target.");

        var actual = pointer.CurrentVersion();
        if (actual is not null && !ReleaseVersion.Parse(actual).Equals(ReleaseVersion.Parse(plan.From)))
            throw new UpdatePlanException($"Plan is stale: installed version is {actual}, plan starts from {plan.From}.");

        var decision = UpdateChecker.Evaluate(m, ReleaseVersion.Parse(plan.From),
            new UpdateCheckOptions
            {
                Channel = options.Channel, AllowDowngrade = options.AllowDowngrade,
                UpdaterVersion = options.UpdaterVersion, KnownMinimumSafeVersion = options.KnownMinimumSafeVersion,
            }, options.Product);
        if (!decision.CanApply) throw new UpdateVerificationException(VerificationFailure.WrongProduct, $"Update refused ({decision.Kind}/{decision.Reason}): {decision.Message}");

        var art = m.FindArtifact(plan.Rid, "service-zip", "zip", "tarball")
                  ?? throw new UpdateVerificationException(VerificationFailure.MalformedManifest, $"Manifest has no installable artifact for '{plan.Rid}'.");
        var artifactPath = layout.ResolveUnderUpdates(plan.ArtifactFile);
        await ReleaseVerifier.VerifyFileAsync(artifactPath, art, ct).ConfigureAwait(false);

        // Extract ourselves from the verified bytes; never trust a pre-extracted tree.
        var tmp = newDir + ".extracting";
        TryDeleteDir(tmp);
        TryDeleteDir(newDir);
        Directory.CreateDirectory(layout.VersionsDir);
        ArchiveExtractor.Extract(artifactPath, tmp);
        Directory.Move(tmp, newDir);
    }

    private async Task RunStep(UpdatePlan plan, StepName name, Func<Task> body)
    {
        var s = plan.Step(name);
        s.State = StepState.Running; s.UpdatedAt = DateTimeOffset.UtcNow; s.Error = null;
        PlanStore.Save(layout, plan);
        try { await body().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            s.State = StepState.Failed; s.Error = ex.Message; s.UpdatedAt = DateTimeOffset.UtcNow;
            PlanStore.Save(layout, plan);
            throw;
        }
        s.State = StepState.Done; s.UpdatedAt = DateTimeOffset.UtcNow;
        PlanStore.Save(layout, plan);
    }

    private async Task<UpdateOutcome> RollbackAsync(UpdatePlan plan, string reason, CancellationToken ct)
    {
        plan.State = PlanState.RollingBack; plan.Reason = reason;
        PlanStore.Save(layout, plan);
        try
        {
            var touched = plan.StateOf(StepName.Stop) != StepState.Pending;
            if (touched)
            {
                if (await service.IsRunningAsync(ct).ConfigureAwait(false)) await service.StopAsync(options.StopTimeout, ct).ConfigureAwait(false);
                if (plan.StateOf(StepName.Backup) == StepState.Done && plan.StateOf(StepName.Migrate) != StepState.Pending && Directory.Exists(ConfigBackup(plan)))
                    ReplaceDirectory(ConfigBackup(plan), layout.ConfigDir);
                if (pointer.CurrentVersion() is { } cur && !ReleaseVersion.Parse(cur).Equals(ReleaseVersion.Parse(plan.From)))
                    pointer.Set(plan.From);
                if (plan.ServiceWasRunning) await service.StartAsync(ct).ConfigureAwait(false);
            }
            TryDeleteDir(layout.VersionDir(plan.To));
            TryDeleteDir(layout.VersionDir(plan.To) + ".extracting");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            plan.State = PlanState.Failed; plan.Reason = reason + " | rollback failed: " + ex.Message;
            return Finish(plan, UpdateOutcomeKind.NeedsOperator, plan.Reason);
        }
        plan.State = PlanState.RolledBack;
        return Finish(plan, UpdateOutcomeKind.RolledBack, "Rolled back to " + plan.From + ": " + reason);
    }

    private UpdateOutcome Finish(UpdatePlan plan, UpdateOutcomeKind kind, string message, bool safeSave = false)
    {
        try { PlanStore.Save(layout, plan); } catch (IOException) when (safeSave) { }
        PlanStore.WriteResult(layout, new UpdateResult(plan.Id, plan.Product, plan.From, plan.To, plan.State.ToString(), plan.Reason, DateTimeOffset.UtcNow));
        Log(message);
        return new(kind, message);
    }

    private UpdateOutcome Quarantine(string why)
    {
        try { File.Move(layout.PlanPath, layout.PlanPath + ".rejected", true); } catch (IOException) { }
        PlanStore.WriteResult(layout, new UpdateResult("", options.Product, "", "", "Rejected", why, DateTimeOffset.UtcNow));
        return new(UpdateOutcomeKind.Rejected, "Plan unreadable: " + why);
    }

    private string ConfigBackup(UpdatePlan plan) => Path.Combine(layout.BackupDir, plan.Id, "config");

    private void Prune(UpdatePlan plan)
    {
        // Keep the new version, the previous one, and up to RetainVersions old versions in total.
        var old = layout.InstalledVersions().Where(v => !string.Equals(v, plan.To, StringComparison.OrdinalIgnoreCase) && !string.Equals(v, plan.From, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => ReleaseVersion.Parse(v)).Skip(Math.Max(0, options.RetainVersions - 1)).ToList();
        foreach (var v in old) TryDeleteDir(layout.VersionDir(v));
        TryDeleteDir(Path.Combine(layout.BackupDir, plan.Id));
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void CopyDirectory(string src, string dst)
    {
        TryDeleteDir(dst);
        Directory.CreateDirectory(dst);
        if (!Directory.Exists(src)) return;
        foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, d)));
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(dst, Path.GetRelativePath(src, f)), true);
    }

    private static void ReplaceDirectory(string backup, string target)
    {
        TryDeleteDir(target);
        CopyDirectory(backup, target);
    }
}

/// <summary>Service-side helper that stages a downloaded release and writes the plan. Contains no trust decisions.</summary>
public static class UpdateStager
{
    public static UpdatePlan Stage(InstallLayout layout, string product, string from, byte[] manifestBytes, string signatureText,
        string artifactPath, string rid, string? healthUrl = null, IEnumerable<string>? migrationSteps = null, int healthTimeoutSeconds = 60)
    {
        var m = ReleaseManifest.Parse(manifestBytes);
        var rel = Path.Combine("staging", m.Version);
        var dir = Path.Combine(layout.UpdatesDir, rel);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "release.json"), manifestBytes);
        File.WriteAllText(Path.Combine(dir, "release.json.sig"), signatureText);
        File.Copy(artifactPath, Path.Combine(dir, "artifact.bin"), true);
        var plan = new UpdatePlan
        {
            Product = product, From = from, To = m.Version, Rid = rid,
            ManifestFile = Path.Combine(rel, "release.json"), SignatureFile = Path.Combine(rel, "release.json.sig"), ArtifactFile = Path.Combine(rel, "artifact.bin"),
            ManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
            HealthUrl = healthUrl, HealthTimeoutSeconds = healthTimeoutSeconds, MigrationSteps = migrationSteps?.ToList() ?? new(),
        };
        PlanStore.Save(layout, plan);
        return plan;
    }
}
