using DbDataKit.Config.Git;
using DbDataKit.Web.Trust;

namespace DbDataProxy.Server.Management;

/// <summary>
/// Who is making a management call (plan 13, T05 and T06): a person a console vouched for, through a token, or the operating-system user on this machine, through the local channel. What they hold decides
/// what they may do; who they are goes into the audit and, for a change to the configuration, onto the commit.
/// </summary>
public sealed class ManagementCaller
{
    private readonly ValidatedToken? _token;
    private readonly string _localUser = "";

    public ManagementCaller(ValidatedToken token) => _token = token;

    private ManagementCaller(string localUser) => _localUser = localUser;

    /// <summary>The operating system decided who could connect to the local channel, so this caller holds every role.</summary>
    public static ManagementCaller Local(string operatingSystemUser) => new(operatingSystemUser);

    public bool Holds(ServerRole role) => _token is null || ManagementAuthorization.Authorize(_token, role).Allowed;

    public GitAuthor Author
    {
        get
        {
            var (name, email) = _token is null ? ManagementIdentity.LocalAuthor(_localUser) : ManagementIdentity.CommitAuthor(_token);
            return new GitAuthor(name, email);
        }
    }

    public AuditEntry Audit(string command, string? target, string outcome, DateTimeOffset at) =>
        _token is not null
            ? ManagementIdentity.Entry(_token, command, target, outcome, at)
            : new AuditEntry(at, "local", "local channel", _localUser, _localUser, false, command, target, outcome, "");
}
