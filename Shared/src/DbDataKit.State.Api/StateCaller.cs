using DbDataKit.Web.Trust;

namespace DbDataKit.State.Api;

/// <summary>
/// Who is calling a state endpoint and what they hold: a console's user through a token, a person signed in to the console's own web app, or the operating-system user on the local channel. The roles decide what the call may do; who it is goes into the audit.
/// </summary>
public sealed class StateCaller
{
    private readonly Func<ServerRole, bool> _holds;
    private readonly Func<string, string?, string, DateTimeOffset, AuditEntry> _entry;

    public StateCaller(string id, Func<ServerRole, bool> holds, Func<string, string?, string, DateTimeOffset, AuditEntry> entry)
    {
        Id = id;
        _holds = holds;
        _entry = entry;
    }

    /// <summary>The user the call is made for, for the response and the log.</summary>
    public string Id { get; }

    public bool Holds(ServerRole role) => _holds(role);

    public AuditEntry Audit(string command, string? target, string outcome, DateTimeOffset at) => _entry(command, target, outcome, at);

    /// <summary>A call with a console's token (plan 13, W12): the lower of the user's role and the console's ceiling.</summary>
    public static StateCaller FromToken(ValidatedToken token) =>
        new(token.SubjectId, role => ManagementAuthorization.Authorize(token, role).Allowed, (command, target, outcome, at) => ManagementIdentity.Entry(token, command, target, outcome, at));

    /// <summary>The local channel: the operating system decided who could connect, so the caller holds every role.</summary>
    public static StateCaller Local(string operatingSystemUser) =>
        new("local:" + operatingSystemUser, _ => true, (command, target, outcome, at) => new AuditEntry(at, "local", "local channel", operatingSystemUser, operatingSystemUser, false, command, target, outcome, ""));

    /// <summary>A person signed in to the product's own web app (the console): <paramref name="holds"/> says which roles their session has.</summary>
    public static StateCaller FromSession(string userId, string? userName, Func<ServerRole, bool> holds) =>
        new(userId, holds, (command, target, outcome, at) => new AuditEntry(at, "session", "web session", userId, userName, false, command, target, outcome, ""));
}
