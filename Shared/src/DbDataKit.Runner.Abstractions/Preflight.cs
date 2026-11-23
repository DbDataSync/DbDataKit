using System.Text.Json.Nodes;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataKit.Runner;

/// <summary>One thing a preflight found: how serious it is (<see cref="Preflight.Error"/>, <see cref="Preflight.Warning"/> or <see cref="Preflight.Info"/>), in words, and what it is about (a connection, a table), when it is about one.</summary>
public sealed record PreflightFinding(string Severity, string Message, string? About = null);

/// <summary>
/// The <c>preflight</c> task: a package's read-only manual task that says, before a version is served, what would stand in the way of it on the databases its slots bind (Design: docs/publishing.md#preflight-before-activation).
/// It writes nothing to a target. Each part of it records one step <see cref="StepName"/> (keyed by the part), whose result is a document with its <c>findings</c>; a finding of severity <see cref="Error"/> keeps a control service from
/// activating the version until a person confirms it. A package declares it with <see cref="Declare"/>; the control service runs it when the package lists a task of this name, and reads the findings with <see cref="Findings"/>.
/// </summary>
public static class Preflight
{
    /// <summary>The task's name in every package that declares it.</summary>
    public const string TaskName = "preflight";

    /// <summary>The step each part of a preflight records its document under.</summary>
    public const string StepName = "preflight-findings";

    public const string Error = "error";
    public const string Warning = "warning";
    public const string Info = "info";

    /// <summary>The largest document recorded whole (the step's cap is 64 KiB); a larger one keeps its findings and drops its detail.</summary>
    public const int MaxDocumentBytes = 60 * 1024;

    /// <summary>
    /// The task as a manifest declares it: by hand only, the package's <paramref name="slots"/>, one run at a time, one attempt, and the step <see cref="StepName"/>. Its <paramref name="inputs"/> (none by default) each have a default, since a
    /// control service starts it with none.
    /// </summary>
    public static PackageTask Declare(IReadOnlyList<ConnectionSlot> slots, TimeSpan? timeout = null, IReadOnlyList<TaskInput>? inputs = null) =>
        new(TaskName, new TaskEntry(), new Schedule(), [TaskTrigger.ByHand()], inputs ?? [], slots, ProgressDeclaration.Empty,
            new TaskLimits(Timeout: timeout ?? TimeSpan.FromMinutes(30), Concurrency: 1, Attempts: 1), [], [], Steps: [new TaskStep(StepName)]);

    /// <summary>
    /// The document one part of a preflight records: the part's name, its findings and, when it fits, the <paramref name="detail"/> it was read from (a command's own document). A detail that would take the document past
    /// <see cref="MaxDocumentBytes"/> is left out, and the document says so; the findings are always kept.
    /// </summary>
    public static JsonObject Document(string part, IEnumerable<PreflightFinding> findings, JsonNode? detail = null)
    {
        var document = new JsonObject
        {
            ["part"] = part,
            ["findings"] = new JsonArray([.. findings.Select(f => (JsonNode?)new JsonObject { ["severity"] = f.Severity, ["message"] = f.Message, ["about"] = f.About })]),
        };
        if (detail is null) return document;
        var size = System.Text.Encoding.UTF8.GetByteCount(document.ToJsonString()) + System.Text.Encoding.UTF8.GetByteCount(detail.ToJsonString());
        if (size <= MaxDocumentBytes) document["detail"] = detail.DeepClone();
        else document["detail_left_out"] = $"The detail of {part} was {size} bytes, more than a step keeps; the findings above are complete.";
        return document;
    }

    /// <summary>
    /// The findings of a preflight's documents (the results of its <see cref="StepName"/> steps), in order. A document that has no findings list says nothing; a finding with a severity this reader does not know is read as an
    /// error, so a newer package's finding never passes for harmless.
    /// </summary>
    public static IReadOnlyList<PreflightFinding> Findings(IEnumerable<JsonNode?> documents)
    {
        var found = new List<PreflightFinding>();
        foreach (var document in documents)
            foreach (var item in (document?["findings"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var severity = (string?)item["severity"] is { } s && s is Error or Warning or Info ? s : Error;
                found.Add(new PreflightFinding(severity, (string?)item["message"] ?? "(a finding with no message)", (string?)item["about"]));
            }
        return found;
    }
}
