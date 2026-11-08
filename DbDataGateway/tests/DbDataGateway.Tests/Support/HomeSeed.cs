using DbDataKit.Identity;
using DbDataKit.State;

namespace DbDataGateway.Tests.Support;

/// <summary>
/// The identity home's people for the tests (plan 13a P13): Gateway has no users of its own, so a test that signs in makes a home user (a service account, or a person with a verified address) in a home group, a personal access token for it, and
/// names that group in the data source files (<see cref="Open"/>). Made in the state of the running host, after it started.
/// </summary>
public static class HomeSeed
{
    /// <summary>The group the tests' users are in and their data sources name.</summary>
    public const string Group = "test-users";

    /// <summary>Names <see cref="Group"/> on each of these data sources that the settings define, in the in-memory settings a test builds the host from; <paramref name="defaultSource"/> is the one a client gets when it names none.</summary>
    public static void Open(IDictionary<string, string?> settings, string[] sources, string? defaultSource = null, string group = Group)
    {
        // a source the test did not configure (an engine it found missing) is skipped: a data source with a group and nothing else would be a configuration error
        foreach (var source in sources.Where(s => settings.ContainsKey($"Gateway:DataSources:{s}:Provider"))) settings[$"Gateway:DataSources:{source}:Groups:0"] = group;
        if (defaultSource is not null) settings[$"Gateway:DataSources:{defaultSource}:Default"] = "true";
    }

    /// <summary>A service account of the home with the groups, and a personal access token for it (the user name is the account's name).</summary>
    public static string ServiceAccount(StateDatabase state, string name, params string[] groups)
    {
        var tokens = new AccessTokens(state, new IdentityUserStore(state));
        var account = tokens.CreateServiceAccount(name, null, "test");
        Join(state, account.UserId, groups.Length == 0 ? [Group] : groups);
        return tokens.Create(account.UserId, "test", "test").Secret;
    }

    /// <summary>A person of the home with a verified address, in the groups, and a personal access token for them (the user name is the address).</summary>
    public static (UserRecord User, string Token) Person(StateDatabase state, string displayName, string address, params string[] groups)
    {
        var users = new IdentityUserStore(state);
        var user = users.CreateUser(displayName, address, UserRole.Viewer, emailVerified: true);
        Join(state, user.Id, groups.Length == 0 ? [Group] : groups);
        return (user, new AccessTokens(state, users).Create(user.Id, "test", "test").Secret);
    }

    public static void Join(StateDatabase state, string userId, params string[] groups)
    {
        var directory = new HomeDirectory(state);
        foreach (var name in groups)
        {
            var group = directory.Groups().FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) ?? directory.CreateGroup(name);
            directory.AddMember(group.Id, userId);
        }
    }
}
