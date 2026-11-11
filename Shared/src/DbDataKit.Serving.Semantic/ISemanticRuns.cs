using System.Text.Json.Nodes;

namespace DbDataKit.Serving.Semantic;

/// <summary>
/// What a host keeps of every statement the semantic module answers (plan 14a SL23, D21): who asked, of which model and which published version of it, what was asked, how it ended and how long it took. Never the rows.
/// A record is written whether the statement succeeded, was refused or failed, and a failure to write one never fails the statement.
/// </summary>
public interface ISemanticRuns
{
    void Record(SemanticRun run);
}

/// <summary>
/// One statement as kept. <c>Caller</c> is the caller's key (D35: the email or UPN, else the name, lower case); <c>ModelVersion</c> the published version or null; <c>Kind</c> is query, explain, members or refused (the text did not become a request);
/// <c>Text</c> what the caller wrote; <c>Request</c> the request document that ran (parameters bound), so a replay is the same request; <c>Status</c> the engine's status or refused, limit, cancelled, failed;
/// <c>ErrorCode</c> the first code of a refusal or the engine's reason code; <c>Plan</c> the scans of an explain, one per line.
/// </summary>
public sealed record SemanticRun(string Caller, string Model, int? ModelVersion, string Kind, string Text, JsonObject? Request, string? RequestHash, string Status, string? ErrorCode, string? Message,
    string? Warnings, string? Plan, DateTimeOffset StartedAt, long DurationMs, long Rows, long? ReplayOf = null);
