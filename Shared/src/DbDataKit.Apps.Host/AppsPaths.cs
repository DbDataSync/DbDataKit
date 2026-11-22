namespace DbDataKit.Apps;

/// <summary>
/// The path the apps host is reached under by a browser: <c>/apps</c>, or the prefix the administrator gave the module (<c>apps.path</c>). The host's own routes are always <c>/apps/...</c>; the host that mounts it rewrites
/// a request under the prefix to them and says the prefix with <see cref="Use"/> for the length of the request, so every address the host writes into a page, a cookie or a redirect is the one the browser used.
/// </summary>
public static class AppsPaths
{
    public const string Default = "/apps";

    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>The prefix of the request being answered, <c>/apps</c> when none was set.</summary>
    public static string Prefix => Current.Value ?? Default;

    /// <summary>The prefix and a slash: <c>/apps/</c>.</summary>
    public static string BasePath => Prefix + "/";

    /// <summary>Sets the prefix for the request that is being answered (and what it awaits).</summary>
    public static void Use(string prefix) => Current.Value = prefix;

    /// <summary>The path the browser used for a path of the host's own routes: <c>/apps/notes/</c> becomes <c>/a/notes/</c> under the prefix <c>/a</c>; any other path is returned as it is.</summary>
    public static string ToPublic(string path) =>
        Prefix != Default && (path == Default || path.StartsWith(Default + "/", StringComparison.Ordinal)) ? Prefix + path[Default.Length..] : path;

    /// <summary>The host's own path for one the browser used under <paramref name="prefix"/>; null when the path is not under it.</summary>
    public static string? ToInternal(string prefix, string path)
    {
        if (prefix == Default) return path;
        if (path == prefix) return Default;
        return path.StartsWith(prefix + "/", StringComparison.Ordinal) ? Default + path[prefix.Length..] : null;
    }
}
