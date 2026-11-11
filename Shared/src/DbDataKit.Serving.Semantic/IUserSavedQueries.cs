using DbDataKit.Semantic;

namespace DbDataKit.Serving.Semantic;

/// <summary>
/// The saved queries people keep for themselves on a model (plan 19, J27), as a host stores them: a caller sees their own and those shared with them (by name, by group or with everyone), and calls them like the model's own
/// (<c>SELECT * FROM name(args)</c>). A name the model already uses is the model's. They run as the caller, so sharing a query shares no data.
/// </summary>
public interface IUserSavedQueries
{
    /// <summary>The saved query this caller calls <paramref name="name"/> on the model: their own first, then one shared with them; null when there is none.</summary>
    SavedQueryDef? Find(ServingCaller caller, string model, string name);

    /// <summary>The saved queries this caller can call on the model, with who owns each.</summary>
    IReadOnlyList<UserSavedQuery> List(ServingCaller caller, string model);
}

/// <summary>A user-owned saved query as listed to a caller.</summary>
public sealed record UserSavedQuery(string Name, string Owner, SavedQueryDef Query, string? OwnerName = null);

/// <summary>How a person's own semantic data is keyed (plan 14a D35): the email or UPN the host put in the caller's claim <c>email</c>, trimmed and lower case; a caller with none is keyed by their name in the same form.</summary>
public static class SemanticCallers
{
    public const string EmailClaim = "email";

    public static string KeyOf(ServingCaller caller) => Normalize(caller.Claims.TryGetValue(EmailClaim, out var email) && !string.IsNullOrWhiteSpace(email) ? email : caller.Name);

    public static string Normalize(string key) => key.Trim().ToLowerInvariant();
}
