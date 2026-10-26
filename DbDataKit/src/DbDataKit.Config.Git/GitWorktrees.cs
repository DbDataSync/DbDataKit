using LibGit2Sharp;

namespace DbDataKit.Config.Git;

/// <summary>A worktree could not be made or removed; the message says why.</summary>
public sealed class GitWorktreeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Worktrees for authoring (plan 13, W02): a second checkout of the same repository on a branch of its own, so a draft is edited and committed without touching the checkout that scheduled runs and deploys
/// read from. The branch starts at the commit the repository's checkout is at, and the worktree is a folder the caller chooses.
/// </summary>
public static class GitWorktrees
{
    /// <summary>Makes <paramref name="branch"/> at the current commit of the repository in <paramref name="repositoryRoot"/> and checks it out in <paramref name="path"/>. Returns the commit it starts at.</summary>
    public static string Add(string repositoryRoot, string branch, string path)
    {
        try
        {
            using var repo = new Repository(repositoryRoot);
            if (repo.Head.Tip is not { } tip) throw new GitWorktreeException("The repository has no commit yet, so there is nothing to start a workspace from.");
            if (repo.Branches[branch] is not null) throw new GitWorktreeException($"The branch '{branch}' already exists.");
            if (Directory.Exists(path) || File.Exists(path)) throw new GitWorktreeException($"'{path}' already exists.");
            var created = repo.CreateBranch(branch, tip);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                repo.Worktrees.Add(created.CanonicalName, WorktreeName(branch), path, false);
            }
            catch
            {
                repo.Branches.Remove(created);
                throw;
            }
            return tip.Sha;
        }
        catch (LibGit2SharpException ex)
        {
            throw new GitWorktreeException($"The workspace could not be made: {ex.Message}", ex);
        }
    }

    /// <summary>Removes the worktree and its branch, with everything in it that was not merged. A worktree that is already gone is not an error.</summary>
    public static void Remove(string repositoryRoot, string branch, string path)
    {
        try
        {
            using var repo = new Repository(repositoryRoot);
            if (repo.Worktrees[WorktreeName(branch)] is { } worktree) repo.Worktrees.Prune(worktree, ifLocked: true);
            DeleteFolder(path);
            if (repo.Branches[branch] is { } b) repo.Branches.Remove(b);
        }
        catch (LibGit2SharpException ex)
        {
            throw new GitWorktreeException($"The workspace could not be removed: {ex.Message}", ex);
        }
    }

    /// <summary>The commit a branch of the repository is at; null when there is no such branch.</summary>
    public static string? TipOf(string repositoryRoot, string branch)
    {
        using var repo = new Repository(repositoryRoot);
        return repo.Branches[branch]?.Tip?.Sha;
    }

    private static string WorktreeName(string branch) => branch.Replace('/', '-');

    /// <summary>Git writes its object files read-only, which stops <see cref="Directory.Delete(string, bool)"/> on Windows.</summary>
    private static void DeleteFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, true);
    }
}
