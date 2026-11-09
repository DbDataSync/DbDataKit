using DbDataKit.Authoring;
using DbDataKit.Config.Git;

namespace DbDataBuild.Cli.Server;

/// <summary>Build's projects as the shared authoring calls see them (plan 22a, GP30): the project file says which there are and where their login is kept.</summary>
internal sealed class BuildAuthoringProjects(ProjectsFile projects) : IAuthoringProjects
{
    public AuthoringProject? Find(string id) => projects.Find(id) is { } hosted ? new AuthoringProject(hosted.Id, projects.RootOf(hosted), hosted.Git) : null;

    public GitCredential? CredentialFor(AuthoringProject project) => projects.Find(project.Id) is { } hosted ? projects.CredentialFor(hosted) : null;
}
