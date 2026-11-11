using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace DbDataKit.Config.Git;

/// <summary>
/// The login a server presents to an authenticated git remote over HTTPS (plan 13, W12): a user name and an access token sent as the password, which GitHub, Azure DevOps and GitLab all accept. It reaches git through libgit2's
/// credentials callback, in this process only: never on a command line, in an environment variable, in the remote's address or in the repository's <c>.git/config</c>. <see cref="ToString"/> never shows the token.
/// </summary>
/// <param name="Username">The user name sent with the token. Providers differ on what they accept, so the default <see cref="DefaultUsername"/> is one all of them take with an access token.</param>
/// <param name="Token">The access token (the password).</param>
/// <param name="Name">What the credential is called where it is kept (the secret's name); used in messages, never secret.</param>
public sealed record GitCredential(string Username, string Token, string Name)
{
    /// <summary>The user name used when the secret holds only a token.</summary>
    public const string DefaultUsername = "oauth2";

    /// <summary>
    /// Reads a secret's value: <c>&lt;token&gt;</c> alone, or <c>&lt;user&gt;:&lt;token&gt;</c> (the user name is what is before the first colon). Throws <see cref="FormatException"/> for an empty value.
    /// </summary>
    public static GitCredential Parse(string value, string name)
    {
        var text = value.Trim();
        if (text.Length == 0) throw new FormatException($"The git credential '{name}' is empty.");
        var colon = text.IndexOf(':');
        return colon > 0 && colon < text.Length - 1
            ? new GitCredential(text[..colon], text[(colon + 1)..], name)
            : new GitCredential(DefaultUsername, text, name);
    }

    public override string ToString() => $"git credential '{Name}' (user '{Username}')";

    /// <summary>Takes the token (and the whole value) out of a message.</summary>
    internal string Scrub(string text) => string.IsNullOrEmpty(Token) ? text : text.Replace(Token, "***", StringComparison.Ordinal);
}

/// <summary>Presents a <see cref="GitCredential"/> once and notices when the remote asks again, which is the remote refusing it.</summary>
internal sealed class CredentialSession(GitCredential? credential)
{
    private sealed class RefusedSignal : Exception;

    public bool Refused { get; private set; }

    public GitCredential? Credential => credential;

    /// <summary>The callback for a fetch or push, or null when no credential is set (a remote that needs one then says so).</summary>
    public CredentialsHandler? Handler => credential is null ? null : (_, _, _) =>
    {
        if (_presented) { Refused = true; throw new RefusedSignal(); }
        _presented = true;
        return new UsernamePasswordCredentials { Username = credential.Username, Password = credential.Token };
    };

    private bool _presented;

    /// <summary>The sentence for a failed network call, in plain words and without the token; null when this was not an authentication failure.</summary>
    public string? Explain(Exception ex)
    {
        var text = ex.Message;
        var asked = ex is RefusedSignal || Refused || text.Contains("401", StringComparison.Ordinal) || text.Contains("403", StringComparison.Ordinal) || text.Contains("authentication", StringComparison.OrdinalIgnoreCase);
        if (!asked) return null;
        if (credential is null) return "The remote asks for a login and no git credential is set for this repository.";
        return $"The remote refused the {credential}: the token is wrong, has expired or been revoked, or is not allowed to do this (it needs write access to push).";
    }

    /// <summary>Whether the exception is the one the callback throws to stop retrying a refused token.</summary>
    public static bool IsRefusal(Exception ex) => ex is RefusedSignal;
}
