using DbDataKit.State;
using Microsoft.Extensions.Hosting;

namespace DbDataKit.Web.Auth;

/// <summary>
/// The only way into a fresh install, printed on the console and written to
/// <c>&lt;RepoRoot&gt;/FIRST-RUN.txt</c>.
/// <para>
/// This is what lets a freshly installed tool be **closed by default** rather than open. Without it
/// the choice is between starting open — which is how products get breached — and refusing to start,
/// which makes a tool somebody just installed unusable.
/// </para>
/// <para>
/// Reprinted (and rewritten) on every start while there are still no users, because the first line of
/// a service's log is not somewhere anybody looks twice — a service has no console at all — and
/// removed the moment the first user exists, so it does not sit on disk as a standing way in.
/// </para>
/// </summary>
/// <param name="Directory">Where <see cref="BootstrapInvite.FileName"/> is written.</param>
/// <param name="BaseUrl">The address people open the host at, so the link is a whole address (plan 23a FQ19); without one it is the path to open on the host.</param>
public sealed record BootstrapInviteLocation(string Directory, string? BaseUrl = null);

public sealed class BootstrapInvite(
    IUserAccounts users, IInviteStore invites, AuthOptions authOptions, BootstrapInviteLocation location,
    ILogger<BootstrapInvite> logger, SetupLinkSettings? setup = null)
    : IHostedService
{
    public const string FileName = "FIRST-RUN.txt";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Anyone from loopback already has Admin without redeeming anything — a bootstrap invite would
        // be a way in nobody needs.
        if (authOptions.NetworkAdmin == AdminNetworkTrust.Loopback)
            return Task.CompletedTask;

        var filePath = Path.Combine(location.Directory, FileName);

        if (users.Any())
        {
            // Somebody exists, so an unredeemed bootstrap invite is a way in that nobody needs and
            // everybody with the console history — or this file — has.
            invites.DeleteBootstrapInvites();
            TryDelete(filePath);
            return Task.CompletedTask;
        }

        // Replaced rather than reused: a code printed to a console that has since scrolled away is a
        // code nobody has, and one that outlives several restarts is one that has been in more places
        // than it should.
        var link = SetupLink.Make(invites, setup, location.BaseUrl);

        logger.LogWarning(
            "No users exist yet. Open this link once to create the first administrator. It ends {Ends:u}:\n" +
            "    {Link}\n" +
            "Run `{Command}` on this machine to make a new one if this scrolls away.",
            link.ExpiresAtUtc,
            link.Url,
            authOptions.Product.SetupLinkCommand);

        // Best effort: a service running as an account with no write access to RepoRoot still has the
        // log line above, so this never blocks startup over a permissions problem it can't fix anyway.
        try
        {
            File.WriteAllText(filePath, SetupLink.FileText(link, authOptions.Product.SetupLinkCommand));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write {FileName}.", FileName);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort, same reasoning as the write above.
        }
    }
}
