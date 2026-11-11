using System.Text.RegularExpressions;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

/// <summary>
/// The <c>setup</c> module (plan 23a FQ21), per install: which items of the setup checklist an administrator chose to skip. Whether an item is <b>finished</b> is never stored: the screen reads it from what the install has (a connection
/// exists, a server is paired), so a click cannot say a thing is done that is not.
/// </summary>
public static class SetupModule
{
    public const string Name = "setup";

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Install,
        [
            """
            -- An item of the setup checklist that an administrator skipped. item is the checklist's own id (sign-in, connections, ...); a skipped item is hidden from the banner and stays on the checklist.
            CREATE TABLE {table:item} (
                item       {{key}} PRIMARY KEY,
                skipped_at {{time}} NOT NULL,
                skipped_by {{key}} NULL
            );
            """,
        ]);
}

/// <summary>Reads and writes the skipped items of the checklist.</summary>
public sealed partial class SetupItems(StateDatabase database)
{
    private const string Module = SetupModule.Name;

    [GeneratedRegex("^[a-z][a-z0-9-]{0,40}$")]
    private static partial Regex ItemPattern { get; }

    /// <summary>The ids of the checklist, in the order it shows them. The screen has the words; the ids are the contract.</summary>
    public static IReadOnlyList<string> Known { get; } = ["sign-in", "servers", "connections", "git-remotes", "delivery-channels", "build-project"];

    public static bool IsKnown(string item) => ItemPattern.IsMatch(item) && Known.Contains(item);

    public IReadOnlyDictionary<string, DateTimeOffset> Skipped() =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT item, skipped_at FROM {table:item} ORDER BY item;");
            using var reader = cmd.ExecuteReader();
            var found = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            while (reader.Read()) found[reader.GetString(0)] = reader.Time(1);
            return (IReadOnlyDictionary<string, DateTimeOffset>)found;
        });

    /// <summary>Skips an item (again is fine: the first time stays).</summary>
    public void Skip(string item, string? by)
    {
        if (!IsKnown(item)) throw new ArgumentException($"'{item}' is not an item of the checklist ({string.Join(", ", Known)}).", nameof(item));
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, database.Dialect.Upsert("{table:item}", "item, skipped_at, skipped_by", "$item, $now, $by", "item", "item = EXCLUDED.item"));
            cmd.Bind(database, "item", item).Bind(database, "now", database.Time.GetUtcNow()).Bind(database, "by", by, System.Data.DbType.String);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>Puts a skipped item back on the banner. True when it had been skipped.</summary>
    public bool Unskip(string item) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "DELETE FROM {table:item} WHERE item = $item;");
            cmd.Bind(database, "item", item);
            return cmd.ExecuteNonQuery() == 1;
        });
}

/// <summary>The setup checklist's skipped items (plan 23a FQ21). What is finished is read by the screen from the install's other pages' own data; here only the skips are kept. Administrators only (the fallback policy).</summary>
[ApiController]
[Route("api/setup")]
public sealed class SetupController(SetupItems items, CurrentUser currentUser) : ControllerBase
{
    /// <summary><c>{ items: [{ item, skipped, skipped_at }] }</c> for every item of the checklist, in its order.</summary>
    [HttpGet("items")]
    [Authorize(Policies.Viewer)]
    public IActionResult List()
    {
        var skipped = items.Skipped();
        return Ok(new
        {
            items = SetupItems.Known.Select(i => new { item = i, skipped = skipped.ContainsKey(i), skipped_at = skipped.TryGetValue(i, out var at) ? (DateTimeOffset?)at : null }).ToList(),
        });
    }

    /// <summary>Skips the item (<c>{ "skipped": true }</c>) or puts it back (<c>{ "skipped": false }</c>).</summary>
    [HttpPut("items/{item}")]
    public IActionResult Put(string item, [FromBody] SkipRequest request)
    {
        if (!SetupItems.IsKnown(item)) return NotFound(new { error = $"'{item}' is not an item of the checklist ({string.Join(", ", SetupItems.Known)})." });
        if (request.Skipped) items.Skip(item, currentUser.Id); else items.Unskip(item);
        return NoContent();
    }
}

public sealed record SkipRequest(bool Skipped);
