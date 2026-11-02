namespace DbDataKit.Apps;

/// <summary>Where an app's code runs (plan 26 section 2): in a sandboxed document of the host (no cookies, no storage), or on an origin of its own (a subdomain or custom domain of its instance).</summary>
public enum AppOrigin { Sandbox, Own }

/// <summary>Whether the host draws its header and navigation around the app.</summary>
public enum AppChrome { Shell, None }

[Flags]
public enum DataOperations { None = 0, Read = 1, Write = 2 }

/// <summary>The entities of one Store model an app uses, and what it may do with each. The app's scoped token is the intersection of this and what the signed-in person may do.</summary>
public sealed record AppModelUse(string Model, IReadOnlyDictionary<string, DataOperations> Entities);

/// <summary>An app's manifest (<c>app/1</c>). <see cref="Entry"/> is the code app's entry point; <see cref="Views"/> are declarative view specs; an app has at least one of them.</summary>
public sealed record AppManifest(
    string Name,
    string? Title,
    string? Description,
    string Version,
    string? Entry,
    IReadOnlyList<string> Views,
    AppOrigin Origin,
    AppChrome Chrome,
    IReadOnlyList<AppModelUse> Models,
    IReadOnlyList<string> SemanticModels,
    IReadOnlyList<string> Roles)
{
    public const string FormatId = "app/1";
}
