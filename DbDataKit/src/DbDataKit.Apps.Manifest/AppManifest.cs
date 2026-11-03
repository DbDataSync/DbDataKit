namespace DbDataKit.Apps;

/// <summary>Where an app's code runs (plan 26 section 2): in a sandboxed document of the host (no cookies, no storage), or on an origin of its own (a subdomain or custom domain of its instance).</summary>
public enum AppOrigin { Sandbox, Own }

/// <summary>Whether the host draws its header and navigation around the app.</summary>
public enum AppChrome { Shell, None }

/// <summary>What an app is (plan 26 section 4a): an app, or a portal, the host other apps are mounted into.</summary>
public enum AppKind { App, Portal }

/// <summary>What the manifest asks for (plan 26 section 2b): sandboxed (the default), or trusted, which an administrator must approve for the exact version before it runs.</summary>
public enum AppTrust { Sandbox, Trusted }

/// <summary>The outside origins an app asks to reach, by what it uses them for. Everything else is refused by the browser. Each entry is an origin (<c>https://api.contoso.com</c>, or <c>wss://</c> for a connection, with an optional <c>*.</c> in front of the host); the manifest only asks, an administrator approves.</summary>
public sealed record AppEgress(
    IReadOnlyList<string> Connect,
    IReadOnlyList<string> Script,
    IReadOnlyList<string> Style,
    IReadOnlyList<string> Img,
    IReadOnlyList<string> Font,
    IReadOnlyList<string> Media,
    IReadOnlyList<string> Frame,
    IReadOnlyList<string> Form)
{
    public static readonly AppEgress None = new([], [], [], [], [], [], [], []);

    /// <summary>The directives and their lists, in the order the policy writes them.</summary>
    public IEnumerable<(string Key, string Directive, IReadOnlyList<string> Origins)> Directives()
    {
        yield return ("connect", "connect-src", Connect);
        yield return ("script", "script-src", Script);
        yield return ("style", "style-src", Style);
        yield return ("img", "img-src", Img);
        yield return ("font", "font-src", Font);
        yield return ("media", "media-src", Media);
        yield return ("frame", "frame-src", Frame);
        yield return ("form", "form-action", Form);
    }

    public IEnumerable<string> All => Directives().SelectMany(d => d.Origins).Distinct(StringComparer.Ordinal);
}

[Flags]
public enum DataOperations { None = 0, Read = 1, Write = 2 }

/// <summary>The entities of one Store model an app uses, and what it may do with each. The app's scoped token is the intersection of this and what the signed-in person may do.</summary>
public sealed record AppModelUse(string Model, IReadOnlyDictionary<string, DataOperations> Entities);

/// <summary>The app storage an app asks for (plan 26 section 2a): per person and per instance, within <see cref="QuotaMb"/> for the instance. An app that declares none gets none.</summary>
public sealed record AppStorageUse(bool User, bool Instance, int QuotaMb)
{
    public const int DefaultQuotaMb = 10;
    public const int MaximumQuotaMb = 1024;
    public static readonly AppStorageUse None = new(false, false, DefaultQuotaMb);
}

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
    IReadOnlyList<string> Roles,
    AppTrust Trust = AppTrust.Sandbox,
    AppEgress? Egress = null,
    AppKind Kind = AppKind.App,
    PortalSpec? Portal = null,
    AppStorageUse? Storage = null)
{
    public AppStorageUse StorageUse => Storage ?? AppStorageUse.None;

    public const string FormatId = "app/1";
}
