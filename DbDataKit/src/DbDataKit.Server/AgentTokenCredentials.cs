using DbDataKit.State;
using DbDataKit.Web.Auth;

namespace DbDataKit.Server;

/// <summary>
/// An outside agent's way into the console (plan 27a AG29): the person's id as the user name and a token made on the Agents page as the password (HTTP Basic), as <c>ui mcp --report-to</c> sends them. The token is the person's: the
/// same user, with the same id and role, so the inbox shows the step to that person and only that person can answer it. What such a request may reach is limited to the outside endpoints (<see cref="Allows"/>).
/// </summary>
public sealed class AgentTokenCredentials(AgentStore store, IUserAccounts users) : IPasswordCredentials
{
    /// <summary>The paths a request signed in with a token may use: the outside agent's ask, its answer and the record it posts. Everything else of the console is for a person signed in.</summary>
    public const string AllowedPrefix = "/api/agents/outside/";

    public static bool Allows(PathString path) => path.StartsWithSegments(AllowedPrefix.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    public UserRecord? Verify(string userName, string password)
    {
        if (store.VerifyToken(password) is not { } person || !string.Equals(person, userName.Trim(), StringComparison.Ordinal)) return null;
        var user = users.Get(person);
        return user is { Enabled: true } ? user : null;
    }
}
