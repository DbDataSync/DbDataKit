using System.Text.Json.Nodes;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataConsole.Server;

/// <param name="From">The server the connection is on: its id or name.</param>
/// <param name="Name">The connection (a Sync connection, a Proxy data source) to copy.</param>
/// <param name="To">The server to copy it to: its id or name.</param>
/// <param name="NewName">What the copy is called there; the same name when empty.</param>
/// <param name="Password">The password of a connection that logs in with one, for a Sync target, which stores it under the copy's own secret reference. It is sent on to that one server and kept nowhere here.</param>
public sealed record CopyConnectionRequest(string From, string Name, string To, string? NewName, string? Password);

/// <summary>
/// Copying a connection from one server to another (plan 13a, E05). The list is the browser's own (it asks each server through the relay, as the monitoring view does); a copy is made here, because it reads one server's file and writes it to another as the signed-in
/// person, and the file changes on the way (<see cref="ConnectionCopy"/>). Each server decides what the person may do from the token, and the target's own strict reading of the file is the validation: what it refuses comes back with its diagnostics and nothing is written.
/// </summary>
[ApiController]
[Route("api/connections")]
public sealed class ConnectionsController(ConsoleInstances instances, ConsoleRelay relay) : ControllerBase
{
    [HttpPost("copy")]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> Copy([FromBody] CopyConnectionRequest request, CancellationToken cancellationToken)
    {
        if (instances.Registry.Find(request.From ?? "") is not { } fromInstance || instances.Registry.Find(request.To ?? "") is not { } toInstance)
            return NotFound(new { error = "Both servers must be registered here." });
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest(new { error = "Name the connection to copy." });
        var newName = string.IsNullOrWhiteSpace(request.NewName) ? request.Name : request.NewName.Trim();
        if (fromInstance.InstanceId == toInstance.InstanceId && newName == request.Name)
            return BadRequest(new { error = "A copy on the same server needs another name." });

        var (from, to) = (await instances.StatusAsync(fromInstance, cancellationToken), await instances.StatusAsync(toInstance, cancellationToken));
        foreach (var (status, role) in new[] { (from, "copy from"), (to, "copy to") })
            if (!status.Reachable || ConnectionCopy.ExtensionKey(status.Product) is null)
                return BadRequest(new { error = $"{status.Instance.Name} cannot be used to {role}: {(status.Reachable ? $"connections are kept by Sync and Proxy servers, and it is {status.Product}" : status.Problem ?? "it is not answering")}." });
        if (request.Password is { Length: > 0 } && to.Product != ConnectionCopy.Sync)
            return BadRequest(new { error = $"{toInstance.Name} takes a password from its own secret store; a password cannot be sent to it." });

        // the source's file, as its server keeps it
        var read = from.Product == ConnectionCopy.Sync
            ? await relay.CallAsync(fromInstance, HttpMethod.Get, $"/api/connections/{Uri.EscapeDataString(request.Name)}/file", null, cancellationToken)
            : await relay.CallAsync(fromInstance, HttpMethod.Get, $"/api/v1/data-sources/{Uri.EscapeDataString(request.Name)}", null, cancellationToken);
        if (read.Status != 200 || read.Body?["text"]?.GetValue<string>() is not { } sourceText)
            return Failure(read.Status, read.Body, $"{fromInstance.Name} would not give the connection '{request.Name}'");

        var plan = ConnectionCopy.Plan(sourceText, from.Product!, to.Product!, newName);
        if (plan.Text is null) return BadRequest(new { error = plan.Problem });

        // the target creates it (no version: a name that is taken is a conflict, never overwritten) and validates it with its own reading of the file
        var write = to.Product == ConnectionCopy.Sync
            ? await relay.CallAsync(toInstance, HttpMethod.Put, $"/api/connections/{Uri.EscapeDataString(newName)}/file", new { text = plan.Text, expectedVersion = (string?)null, password = string.IsNullOrEmpty(request.Password) ? null : request.Password }, cancellationToken)
            : await relay.CallAsync(toInstance, HttpMethod.Put, $"/api/v1/data-sources/{Uri.EscapeDataString(newName)}", new { text = plan.Text, expected_version = (string?)null }, cancellationToken);
        if (write.Status != 200)
            return Failure(write.Status, write.Body, $"{toInstance.Name} did not accept the copy", plan.Notes);

        var notes = plan.Notes.ToList();
        if (to.Product == ConnectionCopy.Sync && !string.IsNullOrEmpty(request.Password)) notes.Add("The password you typed was stored on the target under the copy's own reference.");
        return Ok(new
        {
            from = fromInstance.Name, to = toInstance.Name, name = newName, notes,
            secrets = plan.Secrets, version = write.Body?["version"]?.GetValue<string>(),
            diagnostics = write.Body?["diagnostics"] ?? new JsonArray(),
        });
    }

    /// <summary>A server's refusal, as it gave it: its status when it is a refusal (400, 403, 409), its sentence and its diagnostics, so the screen can show each fault with its line.</summary>
    private ObjectResult Failure(int status, JsonNode? body, string what, IEnumerable<string>? notes = null)
    {
        var reason = body?["error"]?.GetValue<string>() ?? (status == 502 ? "no answer" : $"status {status}");
        return StatusCode(status is >= 400 and < 600 ? status : 502, new { error = $"{what}: {reason}", diagnostics = body?["diagnostics"], notes = notes ?? [], currentVersion = body?["currentVersion"] ?? body?["current_version"] });
    }
}
