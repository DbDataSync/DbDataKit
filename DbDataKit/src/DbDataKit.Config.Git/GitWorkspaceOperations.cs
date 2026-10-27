using LibGit2Sharp;

namespace DbDataKit.Config.Git;

/// <summary>A workspace operation git refused; the message says what and why.</summary>
public sealed class GitOperationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One file a draft changed, with its patch (cut at the limit the caller gave).</summary>
public sealed record GitFileChange(string Path, string Status, string Patch);

/// <summary>What merging a branch did: the new tip when it merged, or the paths that conflicted when it did not (and nothing changed).</summary>
public sealed record GitMergeResult(bool Merged, string? Sha, bool FastForward, IReadOnlyList<string> Conflicts);

/// <summary>
/// What a person does with a draft in a worktree (plan 13, W08): see what changed since it started, commit it, read its history, push its branch, and merge it into the branch the repository's own checkout is on. A merge that conflicts changes
/// nothing and names the files, so the conflict is shown and never resolved silently.
/// </summary>
public static class GitWorkspaceOperations
{
    /// <summary>Every file that differs from <paramref name="baseSha"/>, committed or not, with its patch.</summary>
    public static IReadOnlyList<GitFileChange> Changes(string worktree, string baseSha, int maxPatchChars = 64 * 1024)
    {
        try
        {
            using var repo = new Repository(worktree);
            var start = repo.Lookup<Commit>(baseSha) ?? throw new GitOperationException("The commit this draft started at is not in the repository.");
            Commands.Stage(repo, "*");    // so a new file is a change too; the next commit would stage it anyway
            var patch = repo.Diff.Compare<Patch>(start.Tree, DiffTargets.Index);
            return [.. patch.Select(c => new GitFileChange(c.Path.Replace('\\', '/'), c.Status.ToString().ToLowerInvariant(), c.Patch.Length > maxPatchChars ? c.Patch[..maxPatchChars] : c.Patch))];
        }
        catch (LibGit2SharpException ex) { throw new GitOperationException($"The changes could not be read: {ex.Message}", ex); }
    }

    /// <summary>Whether the worktree holds changes that no commit has taken (staged, edited or new files): a merge would leave them behind.</summary>
    public static bool HasUncommitted(string worktree)
    {
        try
        {
            using var repo = new Repository(worktree);
            return repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true }).IsDirty;
        }
        catch (LibGit2SharpException ex) { throw new GitOperationException($"The changes could not be read: {ex.Message}", ex); }
    }

    /// <summary>Commits everything changed in the worktree. The new commit's sha, or null when nothing changed.</summary>
    public static string? Commit(string worktree, string message, GitAuthor author)
    {
        try
        {
            using var repo = new Repository(worktree);
            Commands.Stage(repo, "*");
            var signature = new Signature(author.Name, author.Email, DateTimeOffset.Now);
            try { return repo.Commit(message, signature, signature, new CommitOptions { AllowEmptyCommit = false }).Sha; }
            catch (EmptyCommitException) { return null; }
        }
        catch (LibGit2SharpException ex) { throw new GitOperationException($"The commit failed: {ex.Message}", ex); }
    }

    /// <summary>The commits made since <paramref name="baseSha"/>, newest first.</summary>
    public static IReadOnlyList<CommitInfo> History(string worktree, string baseSha)
    {
        using var repo = new Repository(worktree);
        var filter = new CommitFilter { IncludeReachableFrom = repo.Head.Tip, ExcludeReachableFrom = baseSha };
        return [.. repo.Commits.QueryBy(filter).Select(c => new CommitInfo(c.Sha, c.MessageShort, c.Author.Name, c.Author.Email, c.Author.When.ToUniversalTime()))];
    }

    /// <summary>Pushes <paramref name="branch"/> to the remote <c>origin</c>. The address is used as it is (credentials in it, or none), as for a clone.</summary>
    public static void Push(string worktree, string branch)
    {
        try
        {
            using var repo = new Repository(worktree);
            var remote = repo.Network.Remotes["origin"] ?? throw new GitOperationException("The repository has no remote called origin.");
            repo.Network.Push(remote, $"refs/heads/{branch}:refs/heads/{branch}", new PushOptions());
        }
        catch (LibGit2SharpException ex) { throw new GitOperationException($"The push failed: {GitRemote.Redact(ex.Message)}", ex); }
    }

    /// <summary>
    /// Merges <paramref name="branch"/> into the branch the repository in <paramref name="repositoryRoot"/> has checked out. The checkout must be clean. A fast-forward moves the branch; otherwise a merge commit is made in
    /// <paramref name="author"/>'s name. When files conflict, the checkout is put back exactly as it was and they are named.
    /// </summary>
    public static GitMergeResult Merge(string repositoryRoot, string branch, GitAuthor author)
    {
        try
        {
            using var repo = new Repository(repositoryRoot);
            if (repo.RetrieveStatus(new StatusOptions { IncludeUntracked = false }).IsDirty)
                throw new GitOperationException("The project's checkout has changes that are not committed, so nothing is merged into it.");
            var theirs = repo.Branches[branch] ?? throw new GitOperationException($"There is no branch '{branch}'.");
            var before = repo.Head.Tip;
            var signature = new Signature(author.Name, author.Email, DateTimeOffset.Now);
            var result = repo.Merge(theirs, signature, new MergeOptions { FastForwardStrategy = FastForwardStrategy.Default, FileConflictStrategy = CheckoutFileConflictStrategy.Normal });
            switch (result.Status)
            {
                case MergeStatus.Conflicts:
                    var conflicts = repo.Index.Conflicts.Select(c => (c.Ours ?? c.Theirs ?? c.Ancestor).Path.Replace('\\', '/')).Distinct().Order(StringComparer.Ordinal).ToList();
                    repo.Reset(ResetMode.Hard, before);
                    return new GitMergeResult(false, null, false, conflicts);
                default:
                    return new GitMergeResult(true, repo.Head.Tip.Sha, result.Status == MergeStatus.FastForward, []);
            }
        }
        catch (LibGit2SharpException ex) { throw new GitOperationException($"The merge failed: {ex.Message}", ex); }
    }
}
