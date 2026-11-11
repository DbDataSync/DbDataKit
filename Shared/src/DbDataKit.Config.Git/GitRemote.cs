using LibGit2Sharp;

namespace DbDataKit.Config.Git;

/// <summary>What <see cref="GitRemote.CloneOrUpdate"/> did.</summary>
/// <param name="Head">The commit the working copy is now at.</param>
/// <param name="Cloned">True when the folder was empty or absent and the repository was cloned; false when an existing clone was brought up to date.</param>
/// <param name="Branch">The branch the working copy follows.</param>
public sealed record GitRemoteResult(string Head, bool Cloned, string Branch);

/// <summary>A remote repository could not be reached, or the branch is not there; the message says which, and never carries a credential.</summary>
public sealed class GitRemoteException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A server's own working copy of a remote repository (plan 13, R02, D19): cloned into a folder the server owns, and brought up to the remote's branch before a run. Nobody edits that folder (authoring happens in
/// workspaces of their own), so updating it discards anything local and is exactly the remote's branch, never a merge. A remote that needs a login gets a <see cref="GitCredential"/> (plan 13, W12), presented through libgit2's callback and kept nowhere in the working copy.
/// </summary>
public static class GitRemote
{
    /// <param name="url">The remote's address (a URL, or a path to a repository on this machine).</param>
    /// <param name="branch">The branch to follow; the remote's default branch when null.</param>
    /// <param name="directory">The working copy: cloned there when it is not a repository yet, which needs the folder to be empty or absent.</param>
    /// <param name="credential">The login for a remote that needs one; null for a remote that does not.</param>
    public static GitRemoteResult CloneOrUpdate(string url, string? branch, string directory, GitCredential? credential = null)
    {
        var session = new CredentialSession(credential);
        var existing = Repository.IsValid(directory);
        try
        {
            return existing ? Update(directory, branch, session) : Clone(url, branch, directory, session);
        }
        catch (Exception ex) when (ex is LibGit2SharpException || CredentialSession.IsRefusal(ex))
        {
            var reason = session.Explain(ex) ?? credential?.Scrub(Redact(ex.Message)) ?? Redact(ex.Message);
            throw new GitRemoteException($"The repository at '{Redact(url)}' could not be {(existing ? "updated" : "cloned")}: {reason}", ex);
        }
    }

    private static GitRemoteResult Clone(string url, string? branch, string directory, CredentialSession session)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new GitRemoteException($"'{directory}' is not empty and is not a repository, so nothing was cloned into it.");
        Repository.Clone(url, directory, new CloneOptions { BranchName = branch, IsBare = false, FetchOptions = { CredentialsProvider = session.Handler } });
        using var repo = new Repository(directory);
        return new GitRemoteResult(repo.Head.Tip.Sha, true, repo.Head.FriendlyName);
    }

    private static GitRemoteResult Update(string directory, string? branch, CredentialSession session)
    {
        using var repo = new Repository(directory);
        var remote = repo.Network.Remotes["origin"] ?? throw new GitRemoteException($"The repository in '{directory}' has no remote called origin.");
        Commands.Fetch(repo, remote.Name, remote.FetchRefSpecs.Select(r => r.Specification), new FetchOptions { CredentialsProvider = session.Handler }, null);
        var name = branch ?? repo.Head.FriendlyName;
        var tracked = repo.Branches[$"origin/{name}"] ?? throw new GitRemoteException($"The remote has no branch '{name}'.");
        var local = repo.Branches[name] ?? repo.CreateBranch(name, tracked.Tip);
        Commands.Checkout(repo, local);
        repo.Reset(ResetMode.Hard, tracked.Tip);
        return new GitRemoteResult(repo.Head.Tip.Sha, false, name);
    }

    /// <summary>The address without anything that looks like a password in it (<c>https://user:secret@host/x</c>).</summary>
    public static string Redact(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo) ? url.Replace(uri.UserInfo, "***") : url;
}
