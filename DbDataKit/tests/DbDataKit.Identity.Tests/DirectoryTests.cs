using DbDataKit.Web.Trust;

namespace DbDataKit.Identity.Tests;

public sealed class DirectoryTests : IDisposable
{
    private readonly HomeFixture _home = new();
    public void Dispose() => _home.Dispose();

    [Fact]
    public void A_user_holds_their_own_roles_and_their_groups_added_together()
    {
        var ada = _home.Users.CreateUser("Ada", null, DbDataKit.State.UserRole.Viewer);
        var ops = _home.Directory.CreateGroup("operators");
        _home.Directory.AddMember(ops.Id, ada.Id);
        _home.Directory.Assign(AssignmentSubject.User(ada.Id), "build-1", ServerRole.Author);
        _home.Directory.Assign(AssignmentSubject.Group(ops.Id), "build-1", ServerRole.Operator);

        var held = _home.Directory.RolesOn(ada.Id, "build-1");

        Assert.True(held.Roles.HasFlag(ServerRole.Author));
        Assert.True(held.Roles.HasFlag(ServerRole.Operator));
        Assert.True(held.Roles.HasFlag(ServerRole.Viewer));      // implied by both
        Assert.False(held.Roles.HasFlag(ServerRole.Admin));
    }

    [Fact]
    public void Roles_belong_to_the_server_they_were_given_on()
    {
        var ada = _home.Users.CreateUser("Ada", null, DbDataKit.State.UserRole.Viewer);
        _home.Directory.Assign(AssignmentSubject.User(ada.Id), "build-1", ServerRole.Admin);

        Assert.Equal(ServerRole.None, _home.Directory.RolesOn(ada.Id, "build-2").Roles);
        Assert.True(_home.Directory.RolesOn(ada.Id, "build-1").Roles.HasFlag(ServerRole.Operator));   // an admin operates
    }

    [Fact]
    public void A_project_role_is_the_projects_and_not_the_servers()
    {
        var ada = _home.Users.CreateUser("Ada", null, DbDataKit.State.UserRole.Viewer);
        _home.Directory.Assign(AssignmentSubject.User(ada.Id), "build-1", ServerRole.Viewer);
        _home.Directory.Assign(AssignmentSubject.User(ada.Id), "build-1", ServerRole.Approver, project: "retail");

        var held = _home.Directory.RolesOn(ada.Id, "build-1");

        Assert.Equal(ServerRole.Viewer, held.Roles);
        Assert.True(held.Projects["retail"].HasFlag(ServerRole.Approver));
        Assert.DoesNotContain("finance", held.Projects.Keys);
    }

    [Fact]
    public void Assigning_again_replaces_and_none_removes()
    {
        var ada = _home.Users.CreateUser("Ada", null, DbDataKit.State.UserRole.Viewer);
        var subject = AssignmentSubject.User(ada.Id);
        _home.Directory.Assign(subject, "build-1", ServerRole.Admin);
        _home.Directory.Assign(subject, "build-1", ServerRole.Viewer);
        Assert.Equal(ServerRole.Viewer, _home.Directory.RolesOn(ada.Id, "build-1").Roles);

        _home.Directory.Assign(subject, "build-1", ServerRole.None);
        Assert.Equal(ServerRole.None, _home.Directory.RolesOn(ada.Id, "build-1").Roles);
    }

    [Fact]
    public void A_member_removed_from_a_group_loses_what_the_group_gave()
    {
        var ada = _home.Users.CreateUser("Ada", null, DbDataKit.State.UserRole.Viewer);
        var ops = _home.Directory.CreateGroup("operators");
        _home.Directory.AddMember(ops.Id, ada.Id);
        _home.Directory.Assign(AssignmentSubject.Group(ops.Id), "build-1", ServerRole.Operator);
        _home.Directory.RemoveMember(ops.Id, ada.Id);

        Assert.Equal(ServerRole.None, _home.Directory.RolesOn(ada.Id, "build-1").Roles);
        Assert.Empty(_home.Directory.GroupsOf(ada.Id));
    }

    [Fact]
    public void An_app_role_is_held_in_that_app_only()
    {
        var ada = _home.Users.CreateUser("Ada", null, DbDataKit.State.UserRole.Viewer);
        _home.Directory.AssignApp(AssignmentSubject.User(ada.Id), "budget", ServerRole.Operator);

        Assert.True(_home.Directory.RolesIn(ada.Id, "budget").HasFlag(ServerRole.Operator));
        Assert.Equal(ServerRole.None, _home.Directory.RolesIn(ada.Id, "other-app"));
        Assert.Equal(ServerRole.None, _home.Directory.RolesOn(ada.Id, "budget").Roles);   // an app is not a server
    }
}
