using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Config.Git;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Authoring;

/// <summary>A project a server serves, as authoring needs to know it.</summary>
/// <param name="Id">The id the server serves it under; the token's per-project scope names it.</param>
/// <param name="Root">The checkout the project is served from (what a merge changes and a workspace starts at).</param>
/// <param name="RemoteUrl">The address of the git remote the project follows, or null when it lives only in this server.</param>
public sealed record AuthoringProject(string Id, string Root, string? RemoteUrl)
{
    public bool IsRemote => RemoteUrl is not null;
}

/// <summary>What a server tells the authoring calls: which projects it serves and how it reaches a project's remote. Read again on every call, so a project added or removed is served or gone at once.</summary>
public interface IAuthoringProjects
{
    AuthoringProject? Find(string id);

    /// <summary>The login for the project's remote, or null for one that needs none.</summary>
    GitCredential? CredentialFor(AuthoringProject project);
}

/// <param name="MailDomain">The domain of the address a commit carries for a person who has none of their own (<c>ada@dbdatabuild.local</c>).</param>
/// <param name="Ending">Told before a workspace's folder goes (abandoned or merged): a process that has it open (a warm session) lets go first.</param>
/// <param name="Changed">Told after something happened to a workspace (see <see cref="WorkspaceChange"/>): a server that keeps a record of them writes it here. What it throws is the host's to catch; the git work is done by then.</param>
public sealed record WorkspaceApiOptions(string MailDomain, Action<string>? Ending = null, Action<WorkspaceChange>? Changed = null);

/// <summary>Something that happened to a workspace, as a host that keeps a history is told.</summary>
/// <param name="Kind"><c>created</c>, <c>committed</c>, <c>pushed</c>, <c>merged</c> or <c>abandoned</c>.</param>
/// <param name="Workspace">The workspace it happened to (for <c>abandoned</c> and <c>merged</c>, as it was).</param>
/// <param name="ActorId">Who did it, as the token carries them.</param>
/// <param name="ActorName">Their name as people read it.</param>
/// <param name="Detail">A sentence: the branch made, the commit message, the commit a merge made. Never file content.</param>
public sealed record WorkspaceChange(string Kind, Workspace Workspace, string ActorId, string ActorName, string? Detail);

/// <summary>
/// The workspace calls of a server's management API (plan 13, W02 and W08; plan 22a, GP30), the same for every kind of project:
/// <c>GET|POST /api/v1/projects/{project}/workspaces</c> and <c>DELETE .../workspaces/{id}</c> (listing is a viewer's; making one and abandoning one needs the author role, and only the owner abandons theirs),
/// <c>GET .../changes</c> and <c>.../history</c> (a viewer's), <c>POST .../commit</c> and <c>.../push</c> (the owner, author role) and <c>POST .../merge</c> (the approver role, because it changes what the scheduler and the deploys read).
/// A project with a git remote is pushed to (a branch, for a pull request opened where the remote lives) and never merged inside the server, since the server's copy of a remote is reset to the remote's branch; a project without one is
/// merged inside the server, in the author's name. A merge that conflicts changes nothing and answers 409 with the files. A merged workspace is abandoned.
/// </summary>
public static class WorkspaceApi
{
    public static object Describe(Workspace w, string? caller) => new { id = w.Id, name = w.Name, owner = w.OwnerName, mine = w.Owner == caller, branch = w.Branch, @base = w.Base, created = w.Created };

    public static IResult Refused(AuthorizationDecision decision) => Results.Json(new { error = $"This needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);

    public static void Map(IEndpointRouteBuilder app, IAuthoringProjects projects, Workspaces workspaces, WorkspaceApiOptions options)
    {
        static GitAuthor AuthorOf(ValidatedToken token, string domain) => new(token.SubjectName ?? token.SubjectId, $"{token.SubjectId}@{domain}");

        // the project, the workspace, and whether this caller may do this to it; the failure is the answer
        IResult? Resolve(string project, string id, HttpContext context, ServerRole needed, bool ownerOnly, out AuthoringProject hosted, out Workspace workspace)
        {
            hosted = null!;
            workspace = null!;
            if (projects.Find(project) is not { } found) return Results.NotFound();
            hosted = found;
            var token = context.ManagementToken();
            var decision = ManagementAuthorization.Authorize(token, needed, hosted.Id);
            if (!decision.Allowed) return Refused(decision);
            if (workspaces.Find(hosted.Id, id) is not { } w) return Results.NotFound();
            workspace = w;
            return ownerOnly && w.Owner != token.SubjectId ? Results.Json(new { error = "A workspace is its owner's." }, statusCode: StatusCodes.Status403Forbidden) : null;
        }

        static IResult Failed(GitOperationException ex) => Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);

        app.MapGet("/api/v1/projects/{project}/workspaces", (string project, HttpContext context) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            var token = context.ManagementToken();
            var decision = ManagementAuthorization.Authorize(token, ServerRole.Viewer, hosted.Id);
            return decision.Allowed ? Results.Json(new { workspaces = workspaces.List(hosted.Id).Select(w => Describe(w, token.SubjectId)) }) : Refused(decision);
        });

        app.MapPost("/api/v1/projects/{project}/workspaces", async (string project, HttpContext context) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            var token = context.ManagementToken();
            var decision = ManagementAuthorization.Authorize(token, ServerRole.Author, hosted.Id);
            if (!decision.Allowed) return Refused(decision);
            string? name;
            try { name = (await JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted))?["name"]?.GetValue<string>(); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { name = null; }
            if (name is null) return Results.Json(new { error = "The body is a JSON object with a \"name\"." }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var made = workspaces.Create(hosted.Id, hosted.Root, token.SubjectId, token.SubjectName ?? token.SubjectId, name);
                options.Changed?.Invoke(new WorkspaceChange("created", made, token.SubjectId, token.SubjectName ?? token.SubjectId, made.Branch));
                return Results.Json(Describe(made, token.SubjectId), statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest); }
            catch (GitWorktreeException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
        });

        app.MapDelete("/api/v1/projects/{project}/workspaces/{id}", (string project, string id, HttpContext context) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            var token = context.ManagementToken();
            var decision = ManagementAuthorization.Authorize(token, ServerRole.Author, hosted.Id);
            if (!decision.Allowed) return Refused(decision);
            if (workspaces.Find(hosted.Id, id) is not { } workspace) return Results.NotFound();
            if (workspace.Owner != token.SubjectId) return Results.Json(new { error = "A workspace is abandoned by its owner." }, statusCode: StatusCodes.Status403Forbidden);
            options.Ending?.Invoke(workspaces.RootOf(workspace));    // its process has the folder open
            try { workspaces.Abandon(workspace, hosted.Root); }
            catch (GitWorktreeException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
            options.Changed?.Invoke(new WorkspaceChange("abandoned", workspace, token.SubjectId, token.SubjectName ?? token.SubjectId, workspace.Branch));
            return Results.NoContent();
        });

        app.MapGet("/api/v1/projects/{project}/workspaces/{id}/changes", (string project, string id, HttpContext context) =>
        {
            if (Resolve(project, id, context, ServerRole.Viewer, false, out _, out var workspace) is { } refused) return refused;
            try
            {
                var root = workspaces.RootOf(workspace);
                var changes = GitWorkspaceOperations.Changes(root, workspace.Base).Select(c => new { path = c.Path, status = c.Status, patch = c.Patch, before = c.Before, after = c.After, sides = c.Sides }).ToList();
                // whether a commit has not taken some of them yet: a merge takes what is committed
                return Results.Json(new { changes, uncommitted = GitWorkspaceOperations.HasUncommitted(root) });
            }
            catch (GitOperationException ex) { return Failed(ex); }
        });

        app.MapGet("/api/v1/projects/{project}/workspaces/{id}/history", (string project, string id, HttpContext context) =>
        {
            if (Resolve(project, id, context, ServerRole.Viewer, false, out _, out var workspace) is { } refused) return refused;
            return Results.Json(new { commits = GitWorkspaceOperations.History(workspaces.RootOf(workspace), workspace.Base).Select(c => new { sha = c.Sha, message = c.Message, author = c.AuthorName, when = c.WhenUtc }) });
        });

        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/commit", async (string project, string id, HttpContext context) =>
        {
            if (Resolve(project, id, context, ServerRole.Author, true, out _, out var workspace) is { } refused) return refused;
            string? message;
            try { message = (await JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted))?["message"]?.GetValue<string>(); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { message = null; }
            if (string.IsNullOrWhiteSpace(message)) return Results.Json(new { error = "The body is a JSON object with a \"message\"." }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var token = context.ManagementToken();
                var sha = GitWorkspaceOperations.Commit(workspaces.RootOf(workspace), message, AuthorOf(token, options.MailDomain));
                if (sha is not null) options.Changed?.Invoke(new WorkspaceChange("committed", workspace, token.SubjectId, token.SubjectName ?? token.SubjectId, message));
                return Results.Json(new { committed = sha is not null, sha });
            }
            catch (GitOperationException ex) { return Failed(ex); }
        });

        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/push", (string project, string id, HttpContext context) =>
        {
            if (Resolve(project, id, context, ServerRole.Author, true, out var hosted, out var workspace) is { } refused) return refused;
            if (!hosted.IsRemote) return Results.Json(new { error = "This project has no git remote to push to; merge it inside the server instead." }, statusCode: StatusCodes.Status409Conflict);
            try
            {
                var root = workspaces.RootOf(workspace);
                GitWorkspaceOperations.Push(root, workspace.Branch, projects.CredentialFor(hosted));
                var pusher = context.ManagementToken();
                options.Changed?.Invoke(new WorkspaceChange("pushed", workspace, pusher.SubjectId, pusher.SubjectName ?? pusher.SubjectId, workspace.Branch));
                // where the person opens the pull request: a link built from the remote's address (GitHub, Azure DevOps, GitLab), null for any other remote
                return Results.Json(new { pushed = true, branch = workspace.Branch, pull_request_url = GitPullRequestLink.For(hosted.RemoteUrl, workspace.Branch) });
            }
            catch (GitOperationException ex) { return Failed(ex); }
            catch (GitRemoteException ex) { return Failed(new GitOperationException(ex.Message, ex)); }
        });

        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/merge", (string project, string id, HttpContext context) =>
        {
            if (Resolve(project, id, context, ServerRole.Approver, false, out var hosted, out var workspace) is { } refused) return refused;
            if (hosted.IsRemote) return Results.Json(new { error = "This project follows a git remote, so its changes go there: push the workspace and merge it where the remote lives." }, statusCode: StatusCodes.Status409Conflict);
            try
            {
                var approver = context.ManagementToken();
                var merged = GitWorkspaceOperations.Merge(hosted.Root, workspace.Branch, AuthorOf(approver, options.MailDomain));
                if (!merged.Merged) return Results.Json(new { error = "The workspace conflicts with the project's branch; nothing was merged.", conflicts = merged.Conflicts }, statusCode: StatusCodes.Status409Conflict);
                options.Ending?.Invoke(workspaces.RootOf(workspace));
                workspaces.Abandon(workspace, hosted.Root);
                options.Changed?.Invoke(new WorkspaceChange("merged", workspace, approver.SubjectId, approver.SubjectName ?? approver.SubjectId, merged.Sha));
                return Results.Json(new { merged = true, sha = merged.Sha, fast_forward = merged.FastForward });
            }
            catch (GitOperationException ex) { return Failed(ex); }
            catch (GitWorktreeException ex) { return Failed(new GitOperationException(ex.Message, ex)); }
        });
    }
}
