using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbDataKit.Update.Service;

public enum StepName { Verify, Stop, Backup, Migrate, Repoint, Start, HealthCheck, Commit }
public enum StepState { Pending, Running, Done, Failed, Skipped }
public enum PlanState { Pending, InProgress, RollingBack, Committed, RolledBack, Rejected, Failed }

public sealed class PlanStep
{
    public StepName Name { get; set; }
    public StepState State { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// The journal. Written by the (unprivileged) service, then mutated only by the helper. Nothing in it is trusted:
/// the helper re-verifies the manifest, signature and artifact it points at.
/// </summary>
public sealed class UpdatePlan
{
    public const string SchemaId = "update-plan/1";

    public string Schema { get; set; } = SchemaId;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Product { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    /// <summary>Paths relative to <c>updates\</c>.</summary>
    public string ManifestFile { get; set; } = "";
    public string SignatureFile { get; set; } = "";
    public string ArtifactFile { get; set; } = "";
    public string Rid { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public string? HealthUrl { get; set; }
    public int HealthTimeoutSeconds { get; set; } = 60;
    public List<string> MigrationSteps { get; set; } = new();
    /// <summary>Set to true once an irreversible (non-undoable) migration has begun: no automatic rollback past this point.</summary>
    public bool IrreversibleMigrationStarted { get; set; }
    public bool ServiceWasRunning { get; set; } = true;
    public PlanState State { get; set; } = PlanState.Pending;
    public string? Reason { get; set; }
    public List<PlanStep> Steps { get; set; } = Enum.GetValues<StepName>().Select(n => new PlanStep { Name = n }).ToList();

    public PlanStep Step(StepName n) => Steps.First(s => s.Name == n);
    public StepState StateOf(StepName n) => Step(n).State;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed record UpdateResult(string PlanId, string Product, string From, string To, string Outcome, string? Reason, DateTimeOffset CompletedAt);

public static class PlanStore
{
    public static UpdatePlan? Load(InstallLayout layout)
    {
        if (!File.Exists(layout.PlanPath)) return null;
        try
        {
            var p = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllBytes(layout.PlanPath), UpdatePlan.Json);
            if (p is null || p.Schema != UpdatePlan.SchemaId) throw new UpdatePlanException("Plan schema is not supported.");
            foreach (var n in Enum.GetValues<StepName>())
                if (p.Steps.All(s => s.Name != n)) p.Steps.Add(new PlanStep { Name = n });
            return p;
        }
        catch (JsonException ex) { throw new UpdatePlanException("Plan is not valid JSON: " + ex.Message); }
    }

    /// <summary>Atomic write (temp + rename) so a power cut leaves either the old or the new journal, never a torn one.</summary>
    public static void Save(InstallLayout layout, UpdatePlan plan)
    {
        Directory.CreateDirectory(layout.UpdatesDir);
        var tmp = layout.PlanPath + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(plan, UpdatePlan.Json));
        File.Move(tmp, layout.PlanPath, true);
    }

    public static void WriteResult(InstallLayout layout, UpdateResult r)
    {
        Directory.CreateDirectory(layout.UpdatesDir);
        File.WriteAllBytes(layout.ResultPath, JsonSerializer.SerializeToUtf8Bytes(r, UpdatePlan.Json));
    }

    /// <summary>Structural validation of everything the plan can steer: versions and paths. Throws <see cref="UpdatePlanException"/>.</summary>
    public static void Validate(InstallLayout layout, UpdatePlan p, string expectedProduct)
    {
        if (!string.Equals(p.Product, expectedProduct, StringComparison.OrdinalIgnoreCase)) throw new UpdatePlanException($"Plan is for '{p.Product}', helper serves '{expectedProduct}'.");
        if (!InstallLayout.IsSafeVersion(p.From)) throw new UpdatePlanException("Plan 'from' is not a safe version.");
        if (!InstallLayout.IsSafeVersion(p.To)) throw new UpdatePlanException("Plan 'to' is not a safe version.");
        layout.ResolveUnderUpdates(p.ManifestFile);
        layout.ResolveUnderUpdates(p.SignatureFile);
        layout.ResolveUnderUpdates(p.ArtifactFile);
        if (string.IsNullOrWhiteSpace(p.Rid) || p.Rid.Any(c => !(char.IsLetterOrDigit(c) || c == '-'))) throw new UpdatePlanException("Plan rid is invalid.");
        if (p.ManifestSha256.Length != 64) throw new UpdatePlanException("Plan manifest hash is invalid.");
        if (p.HealthTimeoutSeconds is < 1 or > 3600) throw new UpdatePlanException("Health timeout out of range.");
        if (p.HealthUrl is not null && (!Uri.TryCreate(p.HealthUrl, UriKind.Absolute, out var u) || !u.IsLoopback))
            throw new UpdatePlanException("Health URL must be a loopback address.");
    }
}

public enum RecoveryAction { NothingToDo, Resume, Rollback, ResumeForwardOnly, NeedsOperator }

/// <summary>Pure decision logic for what to do with a journal found on startup (power loss, crash, killed helper).</summary>
public static class RecoveryPlanner
{
    public static RecoveryAction Decide(UpdatePlan p)
    {
        switch (p.State)
        {
            case PlanState.Committed or PlanState.RolledBack or PlanState.Rejected: return RecoveryAction.NothingToDo;
            case PlanState.Failed: return RecoveryAction.NeedsOperator;
            case PlanState.RollingBack: return p.IrreversibleMigrationStarted ? RecoveryAction.NeedsOperator : RecoveryAction.Rollback;
            case PlanState.Pending: return RecoveryAction.Resume;
        }
        var anyFailed = p.Steps.Any(s => s.State == StepState.Failed);
        if (p.IrreversibleMigrationStarted)
            return anyFailed ? RecoveryAction.NeedsOperator : RecoveryAction.ResumeForwardOnly;
        if (anyFailed) return RecoveryAction.Rollback;
        // Once the pointer moved, the new version has not yet proven healthy: be conservative and go back.
        if (p.StateOf(StepName.Repoint) is StepState.Running or StepState.Done) return RecoveryAction.Rollback;
        return RecoveryAction.Resume;
    }
}
