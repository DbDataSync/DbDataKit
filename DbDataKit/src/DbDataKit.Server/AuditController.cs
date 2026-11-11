using System.Text.Json;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

/// <summary>
/// What the console recorded (UI05; <see cref="ConsoleAuditTrail"/> says what that is): sign-ins, invitations and who redeemed them, people's roles, servers paired and forgotten, the console's other changes and every write a person
/// relayed to a server, newest first, paged with <c>beforeId</c>. Administrators only. This is the console's own audit; the entries the other services wrote to a shared state database are <c>GET api/activity/audit</c>.
/// </summary>
[ApiController]
[Route("api/audit")]
public sealed class AuditController(AuditLog log, IUserAccounts users) : ControllerBase
{
    /// <param name="actor">One person's id.</param>
    /// <param name="verb">The start of a verb: <c>relay.</c>, <c>server.</c>, <c>signin.</c>.</param>
    /// <param name="targetKind">What was acted on: <c>server</c>, <c>user</c>, <c>path</c>.</param>
    /// <param name="targetId">Which one of that kind.</param>
    /// <param name="outcome"><c>ok</c>, <c>refused</c> or <c>failed</c>.</param>
    /// <param name="from">Entries at or after this time.</param>
    /// <param name="to">Entries before this time.</param>
    /// <param name="beforeId">The last id of the previous page.</param>
    [HttpGet]
    [Authorize(Policies.Admin)]
    public IActionResult List([FromQuery] string? actor, [FromQuery] string? verb, [FromQuery] string? targetKind, [FromQuery] string? targetId, [FromQuery] string? outcome,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] long? beforeId, [FromQuery] int limit = 100)
    {
        if (limit is < 1 or > 500) return BadRequest(new { error = "limit is between 1 and 500." });
        if (outcome is not null && outcome is not (AuditEvent.Ok or AuditEvent.Refused or AuditEvent.Failed)) return BadRequest(new { error = "outcome is ok, refused or failed." });

        // the outcome narrows after the read, so a page is read until it holds a full limit or the audit ends (the audit has no index on it, and a page of refusals is small beside the whole)
        var found = new List<AuditRecord>();
        var cursor = beforeId;
        bool more;
        do
        {
            var page = log.Read(new AuditQuery(BeforeId: cursor, ActorId: actor, TargetKind: targetKind, TargetId: targetId, VerbPrefix: verb, ServiceId: ConsoleState.ServiceId, From: from, To: to, Limit: limit));
            more = page.Count == limit;
            if (page.Count > 0) cursor = page[^1].Id;
            found.AddRange(outcome is null ? page : page.Where(e => e.Entry.Outcome == outcome));
        }
        while (more && found.Count < limit);
        var shown = found.Take(limit).ToList();
        // a full page may have more after it: the next page starts after the last entry shown
        long? next = shown.Count == limit ? shown[^1].Id : null;

        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? NameOf(string id) => names.TryGetValue(id, out var known) ? known : names[id] = users.Get(id)?.DisplayName;
        return Ok(new
        {
            entries = shown.Select(e => new
            {
                id = e.Id,
                at = e.At,
                actor = e.Entry.ActorId,
                actorName = NameOf(e.Entry.ActorId),
                verb = e.Entry.Verb,
                outcome = e.Entry.Outcome,
                targetKind = e.Entry.TargetKind,
                targetId = e.Entry.TargetId,
                targetName = e.Entry.TargetKind == "user" && e.Entry.TargetId is { } t ? NameOf(t) : null,
                detail = e.Entry.Detail is { } d ? JsonSerializer.Deserialize<JsonElement>(d) : (JsonElement?)null,
            }),
            next,
        });
    }
}
