using DbDataKit.Cli;
using DbDataKit.Web.Trust;
using Xunit;

namespace DbDataKit.Web.Tests;

public class ManagementAuthorizationTests
{
    private const ServerRole Everything = ServerRole.Viewer | ServerRole.Operator | ServerRole.Author | ServerRole.Approver | ServerRole.Admin;

    private static ValidatedToken Token(ServerRole roles, ServerRole ceiling = Everything, IReadOnlyDictionary<string, ServerRole>? projects = null) =>
        new(new PairedClient("console-a", "Operations", new byte[32], ceiling), "user-7", "Ada", false, roles, projects ?? new Dictionary<string, ServerRole>(),
            "token-0001", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5));

    private static CommandSpec Command(EffectClass effect) => new("x y", effect, "", true);

    public static IEnumerable<object[]> Matrix()
    {
        // role held x effect class -> allowed. A role is a set: Operator, Author and Approver can read, Admin can operate and read, and none of Author, Approver, Admin implies another.
        (EffectClass Effect, ServerRole Needs)[] effects =
        [
            (EffectClass.OfflineOnly, ServerRole.Viewer), (EffectClass.TargetReadOnly, ServerRole.Viewer), (EffectClass.RepoFilesOnly, ServerRole.Author),
            (EffectClass.TargetDataWrites, ServerRole.Operator), (EffectClass.TrackingTablesOnly, ServerRole.Admin), (EffectClass.TargetWrites, ServerRole.Approver),
        ];
        foreach (var held in new[] { ServerRole.None, ServerRole.Viewer, ServerRole.Operator, ServerRole.Author, ServerRole.Approver, ServerRole.Admin })
            foreach (var (effect, needs) in effects)
                yield return [held, effect, ServerRoles.Allows(held, needs)];
    }

    [Theory, MemberData(nameof(Matrix))]
    public void A_role_is_allowed_exactly_the_effect_classes_it_covers(ServerRole held, EffectClass effect, bool expected)
    {
        var decision = ManagementAuthorization.Authorize(Token(held), Command(effect));
        Assert.Equal(expected, decision.Allowed);
        Assert.Equal(expected, decision.Reason is null);
    }

    [Fact]
    public void The_matrix_is_the_one_in_the_plan()
    {
        Assert.Equal(ServerRole.Viewer, ManagementAuthorization.RoleFor(EffectClass.TargetReadOnly));
        Assert.Equal(ServerRole.Author, ManagementAuthorization.RoleFor(EffectClass.RepoFilesOnly));
        Assert.Equal(ServerRole.Operator, ManagementAuthorization.RoleFor(EffectClass.TargetDataWrites));
        Assert.Equal(ServerRole.Approver, ManagementAuthorization.RoleFor(EffectClass.TargetWrites));
        Assert.Equal(ServerRole.Admin, ManagementAuthorization.RoleFor(EffectClass.TrackingTablesOnly));
    }

    [Fact]
    public void Every_effect_class_has_a_role()
    {
        foreach (var effect in Enum.GetValues<EffectClass>())
            Assert.NotEqual(ServerRole.None, ManagementAuthorization.RoleFor(effect));
        Assert.Throws<ArgumentOutOfRangeException>(() => ManagementAuthorization.RoleFor((EffectClass)99));
    }

    [Fact]
    public void Author_Approver_and_Admin_do_not_imply_each_other()
    {
        Assert.False(ManagementAuthorization.Authorize(Token(ServerRole.Admin), Command(EffectClass.RepoFilesOnly)).Allowed);
        Assert.False(ManagementAuthorization.Authorize(Token(ServerRole.Author), Command(EffectClass.TargetWrites)).Allowed);
        Assert.False(ManagementAuthorization.Authorize(Token(ServerRole.Approver), Command(EffectClass.TrackingTablesOnly)).Allowed);
        Assert.True(ManagementAuthorization.Authorize(Token(ServerRole.Admin), Command(EffectClass.TargetDataWrites)).Allowed);
    }

    [Fact]
    public void The_call_holds_the_lower_of_the_users_roles_and_the_consoles_ceiling()
    {
        // the validator refuses a claim above the ceiling, so this is the second line: a token built some other way is still bounded
        var token = Token(ServerRole.Admin | ServerRole.Approver, ceiling: ServerRole.Viewer | ServerRole.Operator);
        Assert.Equal(ServerRoles.Close(ServerRole.Operator), ManagementAuthorization.Effective(token));
        Assert.False(ManagementAuthorization.Authorize(token, Command(EffectClass.TrackingTablesOnly)).Allowed);
        Assert.False(ManagementAuthorization.Authorize(token, Command(EffectClass.TargetWrites)).Allowed);
        Assert.True(ManagementAuthorization.Authorize(token, Command(EffectClass.TargetDataWrites)).Allowed);
        Assert.True(ManagementAuthorization.Authorize(token, Command(EffectClass.TargetReadOnly)).Allowed);
    }

    [Fact]
    public void A_role_in_a_project_counts_only_in_that_project_and_only_up_to_the_ceiling()
    {
        var projects = new Dictionary<string, ServerRole> { ["sales"] = ServerRole.Author, ["finance"] = ServerRole.Approver };
        var token = Token(ServerRole.Viewer, ceiling: ServerRole.Viewer | ServerRole.Author, projects);
        Assert.True(ManagementAuthorization.Authorize(token, Command(EffectClass.RepoFilesOnly), "sales").Allowed);
        Assert.False(ManagementAuthorization.Authorize(token, Command(EffectClass.RepoFilesOnly), "other").Allowed);
        Assert.False(ManagementAuthorization.Authorize(token, Command(EffectClass.RepoFilesOnly)).Allowed);
        Assert.False(ManagementAuthorization.Authorize(token, Command(EffectClass.TargetWrites), "finance").Allowed);      // above this console's ceiling
    }

    [Fact]
    public void A_call_that_needs_no_role_is_refused_and_the_reason_names_what_was_missing()
    {
        Assert.False(ManagementAuthorization.Authorize(Token(Everything), ServerRole.None).Allowed);
        var denied = ManagementAuthorization.Authorize(Token(ServerRole.Viewer), ServerRole.Admin);
        Assert.Contains("admin", denied.Reason);
        Assert.Contains("viewer", denied.Reason);
    }
}
