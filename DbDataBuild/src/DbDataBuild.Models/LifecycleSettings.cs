namespace DbDataBuild.Models;

/// <summary>Where a plan lives (`plans.<lane>.keep`). A refresh plan is compiled and committed; a deploy plan is committed, or kept only in the tracking tables, or not kept at all.</summary>
public enum PlanKeep { Committed, Database, Ephemeral }

/// <summary>How much an event records (`plans.<lane>.audit`): the event and its steps; plus the plan's hash, who and the commit; plus the plan text.</summary>
public enum AuditLevel { Minimal, Standard, Full }

/// <summary>What a refresh checks before it runs (`refresh.check`): nothing; that the project's structure was deployed here; each object it uses against what was deployed; each object against the live catalog.</summary>
public enum RefreshCheck { None, Project, Objects, Live }

/// <summary>What a failed refresh check does (`refresh.on_fail`).</summary>
public enum OnFail { Block, Warn }

/// <param name="Keep">Where the lane's plans live.</param>
/// <param name="Audit">How much each event of the lane records.</param>
public sealed record LanePolicy(PlanKeep Keep, AuditLevel Audit);

/// <summary>
/// The settings that say how a project is meant to operate (docs/research/lifecycle-model.md, section 7a): what is kept of a plan and recorded of an event, whether a deploy may run from a working tree with
/// changes, the safety check before a refresh, and how long the local statement logs are kept. The project sets them; a connection may set its own.
/// </summary>
public sealed record LifecycleSettings(LanePolicy Deploy, LanePolicy Refresh, bool RequireCleanTree, RefreshCheck Check, OnFail OnFail, int StatementLogDays)
{
    public static LifecycleSettings Default { get; } = new(new(PlanKeep.Committed, AuditLevel.Full), new(PlanKeep.Committed, AuditLevel.Standard), true, RefreshCheck.Objects, OnFail.Block, 30);

    public static string Name(Enum value) => value.ToString().ToLowerInvariant();

    /// <summary>One line for the header of a command: what is in force.</summary>
    public string Describe() =>
        $"plans: deploy {Name(Deploy.Keep)}/{Name(Deploy.Audit)}, refresh {Name(Refresh.Keep)}/{Name(Refresh.Audit)}; refresh check: {Name(Check)} (on failure: {Name(OnFail)})" +
        (RequireCleanTree ? "; deploy needs a clean working tree" : "");
}

/// <summary>What a connection says about its own lifecycle instead of the project: each field left out is the project's.</summary>
public sealed record LifecycleOverride(PlanKeep? DeployKeep = null, AuditLevel? DeployAudit = null, PlanKeep? RefreshKeep = null, AuditLevel? RefreshAudit = null, bool? RequireCleanTree = null, RefreshCheck? Check = null, OnFail? OnFail = null)
{
    public LifecycleSettings Apply(LifecycleSettings project) => project with
    {
        Deploy = new(DeployKeep ?? project.Deploy.Keep, DeployAudit ?? project.Deploy.Audit),
        Refresh = new(RefreshKeep ?? project.Refresh.Keep, RefreshAudit ?? project.Refresh.Audit),
        RequireCleanTree = RequireCleanTree ?? project.RequireCleanTree,
        Check = Check ?? project.Check,
        OnFail = OnFail ?? project.OnFail,
    };
}
