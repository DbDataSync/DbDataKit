namespace DbDataKit.Config.Git;

/// <summary>Where the push of a configuration repository stands (plan 22a GP19): what <c>health</c> shows.</summary>
/// <param name="Remote">The remote's address with no login (<see cref="GitRemote.Redact"/>).</param>
/// <param name="Pending">A commit is waiting to be pushed (a push is running, or the last one failed and will be tried again).</param>
/// <param name="LastPushedAt">When a push last worked.</param>
/// <param name="LastFailureAt">When the last try failed; null after a push worked.</param>
/// <param name="LastPushedSha">The commit the last good push carried.</param>
/// <param name="LastFailure">What the remote or the network said, with the token taken out; null after a push worked.</param>
public sealed record GitPushStatus(string Remote, bool Pending, DateTimeOffset? LastPushedAt, DateTimeOffset? LastFailureAt, string? LastFailure, string? LastPushedSha)
{
    /// <summary>True when the last try failed: the remote is behind the repository.</summary>
    public bool Failing => LastFailure is not null;
}

/// <summary>
/// Pushes a configuration repository to its remote after each commit, in the background (plan 22a GP19, the setting <c>git.remote</c> of a product): <see cref="Notify"/> after a commit asks for a push and returns at once, so <b>a commit never waits for the remote and
/// never fails because of it</b>; a push that fails is kept in <see cref="Status"/> (shown in <c>health</c>) and tried again after <c>retry</c> until it works, so a remote that was down catches up with everything committed meanwhile in one push. The credential is read from
/// the callback for each push (the product's secret store: <see cref="GitCredential"/>), is presented only through libgit2's callback, and is taken out of every failure message; it is never in a log, in the remote's address or in <c>.git/config</c>.
/// </summary>
public sealed class GitRemotePusher : IDisposable
{
    private readonly string _root;
    private readonly string _url;
    private readonly Func<GitCredential?> _credential;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retry;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _loop;
    private bool _pending;
    private bool _busy;
    private DateTimeOffset? _pushedAt;
    private DateTimeOffset? _failedAt;
    private string? _failure;
    private string? _pushedSha;

    /// <param name="repositoryRoot">The repository's folder.</param>
    /// <param name="remoteUrl">The remote's address (http, https, ssh or a path); an address with a login in it is refused: the login is the credential's.</param>
    /// <param name="credential">The login for an authenticated remote, read for each push; null for none.</param>
    /// <param name="retry">How long to wait after a failed push before the next try (default 30 seconds).</param>
    /// <param name="time">The clock (tests give their own).</param>
    public GitRemotePusher(string repositoryRoot, string remoteUrl, Func<GitCredential?>? credential = null, TimeSpan? retry = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteUrl);
        if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
            throw new GitRemoteException($"The git remote '{GitRemote.Redact(remoteUrl)}' has a login in its address; remove it and give the credential as a secret (git.remote_credential).");
        _root = repositoryRoot;
        _url = remoteUrl;
        _credential = credential ?? (() => null);
        _retry = retry ?? TimeSpan.FromSeconds(30);
        _time = time ?? TimeProvider.System;
    }

    public GitPushStatus Status
    {
        get
        {
            lock (_gate) return new GitPushStatus(GitRemote.Redact(_url), _pending || _busy, _pushedAt, _failedAt, _failure, _pushedSha);
        }
    }

    /// <summary>Asks for a push (call it after a commit). It returns at once; the push happens on a thread of its own.</summary>
    public void Notify()
    {
        lock (_gate)
        {
            _pending = true;
            if (_loop is { IsCompleted: false } || _stop.IsCancellationRequested) return;
            _loop = Task.Run(LoopAsync);
        }
    }

    /// <summary>Waits until nothing is pending and the last push worked, or <paramref name="timeout"/> passes (a remote that keeps failing is never idle); true when it is idle.</summary>
    public async Task<bool> WaitIdleAsync(TimeSpan timeout)
    {
        var end = _time.GetUtcNow() + timeout;
        while (_time.GetUtcNow() < end)
        {
            var status = Status;
            if (!status.Pending && !status.Failing) return true;
            await Task.Delay(20).ConfigureAwait(false);
        }

        return false;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (!_pending) return;
                _pending = false;
                _busy = true;
            }

            var pushed = TryPush();
            lock (_gate)
            {
                _busy = false;
                if (!pushed) _pending = true;      // not pushed: still owed; the next try pushes everything committed since
            }

            if (pushed) continue;
            try { await Task.Delay(_retry, _time, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One push of the checked-out branch; false (and the failure recorded) when the remote refused it or could not be reached.</summary>
    public bool TryPush()
    {
        GitCredential? credential = null;
        try
        {
            credential = _credential();
            string branch;
            string sha;
            using (var repo = new LibGit2Sharp.Repository(_root))
            {
                if (repo.Head.Tip is null) return true;                    // nothing committed yet: nothing to push
                branch = repo.Head.FriendlyName;
                sha = repo.Head.Tip.Sha;
                var existing = repo.Network.Remotes["origin"];
                if (existing is null) repo.Network.Remotes.Add("origin", _url);
                else if (!string.Equals(existing.Url, _url, StringComparison.Ordinal)) repo.Network.Remotes.Update("origin", r => r.Url = _url);
            }

            GitWorkspaceOperations.Push(_root, branch, credential);
            lock (_gate)
            {
                _pushedAt = _time.GetUtcNow();
                _pushedSha = sha;
                _failure = null;
                _failedAt = null;
            }

            return true;
        }
        catch (Exception ex) when (ex is GitOperationException or GitRemoteException or LibGit2Sharp.LibGit2SharpException or IOException)
        {
            var text = GitRemote.Redact(ex.Message);
            if (credential is not null && credential.Token.Length > 0) text = text.Replace(credential.Token, "***", StringComparison.Ordinal);
            lock (_gate)
            {
                _failedAt = _time.GetUtcNow();
                _failure = text;
            }

            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* the loop ends on cancellation */ }
        _stop.Dispose();
    }
}
