using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DbDataKit.Web.Tests;

/// <summary>
/// The only way into a fresh install, and what makes it possible for one to be closed by default
/// rather than open. These ran in Sync's suite until plan 13, X05 took the first-run invitation out of Sync (its users and invitations are the console's); the class is
/// shared and the console still runs it, so its behaviour is pinned here, for a product that is not Sync.
/// </summary>
public sealed class BootstrapInviteTests
{
    private static readonly AuthProduct Product = new("DbDataKit");

    private static BootstrapInvite Bootstrap(UserStore users, InviteStore invites, string root, bool networkAdminLoopback = false) =>
        new(users, invites,
            new AuthOptions
            {
                Product = Product,
                AdminGroup = "g",
                NetworkAdmin = networkAdminLoopback ? AdminNetworkTrust.Loopback : AdminNetworkTrust.Disabled,
            },
            new BootstrapInviteLocation(root),
            NullLogger<BootstrapInvite>.Instance);

    /// <summary>Fresh state, so "are there users" means what it says rather than what the rest of the
    /// suite has left behind.</summary>
    private (UserStore Users, InviteStore Invites, string Root) Fresh()
    {
        var root = Directory.CreateTempSubdirectory("dbdatasync-bootstrap-").FullName;
        var database = new StateDatabase(Path.Combine(root, "state.db"), new StateSchema([IdentitySchema.Accounts, IdentitySchema.Invites, IdentitySchema.AccountKeys]));
        return (new UserStore(database), new InviteStore(database), root);
    }

    [Fact]
    public async Task WithNoUsers_AnAdminInviteIsMinted()
    {
        var (users, invites, root) = Fresh();
        try
        {
            await Bootstrap(users, invites, root).StartAsync(CancellationToken.None);

            var invite = Assert.Single(invites.ListOutstanding());
            Assert.Equal(UserRole.Admin, invite.Role);
            // Nobody made it, which is what marks it as the bootstrap one.
            Assert.Null(invite.CreatedByUserId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Reprinted on every start while there is still no way in — the first line of a service's log is
    /// not somewhere anybody looks twice — and replaced rather than reused, because a code that
    /// outlives several restarts has been in more places than it should.
    /// </summary>
    [Fact]
    public async Task RestartingWithNoUsers_ReplacesItRatherThanAccumulating()
    {
        var (users, invites, root) = Fresh();
        try
        {
            await Bootstrap(users, invites, root).StartAsync(CancellationToken.None);
            await Bootstrap(users, invites, root).StartAsync(CancellationToken.None);

            Assert.Single(invites.ListOutstanding());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Once somebody exists, an unredeemed bootstrap invite is a way in that nobody needs and
    /// everybody with the console history has.</summary>
    [Fact]
    public async Task OnceAUserExists_ItIsRevoked()
    {
        var (users, invites, root) = Fresh();
        try
        {
            await Bootstrap(users, invites, root).StartAsync(CancellationToken.None);
            users.CreateUser("Somebody", null, UserRole.Admin);

            await Bootstrap(users, invites, root).StartAsync(CancellationToken.None);

            Assert.Empty(invites.ListOutstanding());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>An invitation an administrator made is theirs, and survives a restart that revokes the
    /// bootstrap one.</summary>
    [Fact]
    public async Task AnAdminsOwnInvite_SurvivesTheRevocation()
    {
        var (users, invites, root) = Fresh();
        try
        {
            var admin = users.CreateUser("Somebody", null, UserRole.Admin);
            invites.Create(UserRole.Viewer, forUserId: null, createdByUserId: admin.Id);

            await Bootstrap(users, invites, root).StartAsync(CancellationToken.None);

            Assert.Single(invites.ListOutstanding());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A deployment where loopback already grants Admin for free has nobody to invite.</summary>
    [Fact]
    public async Task WithNetworkAdminLoopback_NothingIsMinted()
    {
        var (users, invites, root) = Fresh();
        try
        {
            await Bootstrap(users, invites, root, networkAdminLoopback: true).StartAsync(CancellationToken.None);

            Assert.Empty(invites.ListOutstanding());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The one way in for a process with no console at all — a Windows service — so the
    /// invite code has to reach disk, not just the log.</summary>
    [Fact]
    public async Task WithNoUsers_FirstRunFileIsWritten()
    {
        var (users, invites, root) = Fresh();
        try
        {
            await Bootstrap(users, invites, root).StartAsync(CancellationToken.None);

            var path = Path.Combine(root, BootstrapInvite.FileName);
            Assert.True(File.Exists(path));
            var invite = Assert.Single(invites.ListOutstanding());
            Assert.Contains($"/invite#", File.ReadAllText(path));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Once somebody exists, the file goes with the invite it named — left behind, it would
    /// be a standing way in nobody needs any more.</summary>
    [Fact]
    public async Task OnceAUserExists_FirstRunFileIsRemoved()
    {
        var (users, invites, root) = Fresh();
        try
        {
            await Bootstrap(users, invites, root).StartAsync(CancellationToken.None);
            var path = Path.Combine(root, BootstrapInvite.FileName);
            Assert.True(File.Exists(path));

            users.CreateUser("Somebody", null, UserRole.Admin);
            await Bootstrap(users, invites, root).StartAsync(CancellationToken.None);

            Assert.False(File.Exists(path));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
