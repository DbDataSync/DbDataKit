using System.Text.RegularExpressions;

namespace DbDataKit.Config.Git;

/// <summary>
/// The address of the page where a person opens a pull request (a merge request, for GitLab) for a pushed branch (plan 13, W12, W11 option C): built from the remote's address alone, with no token and no provider API. Known for
/// GitHub (<c>github.com</c>), Azure DevOps (<c>dev.azure.com</c>, <c>ssh.dev.azure.com</c>, <c>*.visualstudio.com</c>, <c>vs-ssh.visualstudio.com</c>) and GitLab (<c>gitlab.com</c>), in their https, <c>ssh://</c> and scp
/// (<c>git@host:path</c>) forms. Any other remote (a path on this machine, another host, a self-hosted server whose provider cannot be told from its name) has no link: null.
/// </summary>
public static partial class GitPullRequestLink
{
    /// <summary>The link for <paramref name="branch"/> pushed to <paramref name="remoteUrl"/>, or null when the remote is none of the three providers or its address is not understood.</summary>
    public static string? For(string? remoteUrl, string branch)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) || string.IsNullOrWhiteSpace(branch) || !Split(remoteUrl.Trim(), out var host, out var path)) return null;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0 && segments[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)) segments[^1] = segments[^1][..^4];
        if (segments.Length == 0 || segments.Any(s => s.Length == 0)) return null;
        var q = Uri.EscapeDataString(branch);
        host = host.ToLowerInvariant();

        if (host is "github.com" or "www.github.com")
            return segments.Length == 2 ? $"https://github.com/{Enc(segments[0])}/{Enc(segments[1])}/pull/new/{string.Join('/', branch.Split('/').Select(Uri.EscapeDataString))}" : null;

        if (host == "gitlab.com" || host == "www.gitlab.com")
            return segments.Length >= 2 ? $"https://gitlab.com/{string.Join('/', segments.Select(Enc))}/-/merge_requests/new?merge_request%5Bsource_branch%5D={q}" : null;

        // Azure DevOps: dev.azure.com/<org>/<project>/_git/<repo>, <org>.visualstudio.com/[DefaultCollection/]<project>/_git/<repo>, and the ssh forms v3/<org>/<project>/<repo>
        if (host is "dev.azure.com")
        {
            var i = Array.IndexOf(segments, "_git");
            return i == 2 && segments.Length == 4 ? Azure(segments[0], segments[1], segments[3], q) : null;
        }
        if (host is "ssh.dev.azure.com" or "vs-ssh.visualstudio.com")
            return segments.Length == 4 && segments[0] == "v3" ? Azure(segments[1], segments[2], segments[3], q) : null;
        if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
        {
            var organisation = host[..^".visualstudio.com".Length];
            var rest = segments.Length > 0 && segments[0].Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase) ? segments[1..] : segments;
            return rest.Length == 3 && rest[1] == "_git" ? Azure(organisation, rest[0], rest[2], q) : null;
        }
        return null;
    }

    private static string Enc(string segment) => Uri.EscapeDataString(Uri.UnescapeDataString(segment));

    private static string Azure(string organisation, string project, string repository, string branchQuery) =>
        $"https://dev.azure.com/{Enc(organisation)}/{Enc(project)}/_git/{Enc(repository)}/pullrequestcreate?sourceRef={branchQuery}";

    /// <summary>The host and path of a remote written as a URL (https, http, ssh, git) or in scp form (<c>user@host:path</c>); false for a path on this machine.</summary>
    private static bool Split(string remote, out string host, out string path)
    {
        host = path = "";
        if (remote.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) || uri.Scheme is "file" || string.IsNullOrEmpty(uri.Host)) return false;
            (host, path) = (uri.Host, uri.AbsolutePath);
            return true;
        }
        var scp = ScpForm().Match(remote);
        if (!scp.Success) return false;
        (host, path) = (scp.Groups["host"].Value, scp.Groups["path"].Value);
        return true;
    }

    // [user@]host:path, where the part before the colon has no slash (so a Windows path such as C:\x is not taken for one: its "host" would be one letter, refused below)
    [GeneratedRegex(@"^(?:[^@/\s]+@)?(?<host>[A-Za-z0-9][A-Za-z0-9.-]*\.[A-Za-z0-9.-]+):(?<path>[^\s]+)$")]
    private static partial Regex ScpForm();
}
