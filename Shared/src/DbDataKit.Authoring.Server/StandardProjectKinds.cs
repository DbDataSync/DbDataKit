using DbDataKit.Authoring;
using DbDataKit.State;

namespace DbDataKit.Authoring.Server;

/// <summary>The kinds this build of the Workspace engine knows (plan 33 SC20): each row adds its kind here and its name to <see cref="WorkspaceModule.ProjectKinds"/>; a test keeps the two lists equal.</summary>
public static class StandardProjectKinds
{
    public static ProjectKinds Create(BuildTool? build = null) => new([new AppProjectKind(), new BuildProjectKind(build ?? new BuildTool()), new StoreProjectKind()]);
}
