
namespace DbDataKit.Web.Auth;

/// <summary>
/// WebAuthn's relying-party settings — the origin passkeys are bound to.
/// <para>
/// **This is the thing that will go wrong.** A passkey registered against <c>localhost</c> does not
/// work against <c>dbdatasync.corp.example</c>, and one registered against an IP address does not work
/// at all. It has to be configuration, it has to be checked at startup, and a mismatch has to say what
/// it is — because otherwise it fails inside a browser API with a message that names nothing.
/// </para>
/// </summary>
public sealed class PasskeyOptions
{
    /// <summary>The relying party id: a **domain**, with no scheme and no port. <c>localhost</c> for a
    /// local install; the hostname the console is reached at otherwise.
    /// <para>
    /// **Deliberately independent of <c>App:Url</c>, not derived from it.** Unlike <see cref="Origins"/>,
    /// this is cryptographically bound into every passkey at the moment it's created — a browser refuses
    /// to use a credential whose relying-party id doesn't match. If this silently tracked <c>App:Url</c>'s
    /// hostname, changing the console's URL would silently invalidate every already-registered passkey.
    /// See <c>architecture/planning/todo/passkey-relying-party-migration.md</c> for the fuller reasoning
    /// and for what a real relying-party migration would actually need.
    /// </para>
    /// </summary>
    public required string RelyingPartyId { get; init; }

    /// <summary>What a browser shows the user when it asks them to approve.</summary>
    public required string RelyingPartyName { get; init; }

    /// <summary>Explicit — lets an operator configure a relying-party id and still turn passkey
    /// enrollment/sign-in off without clearing it.</summary>
    public FeatureMode Mode { get; init; } = FeatureMode.Enabled;

    public bool Enabled => Mode == FeatureMode.Enabled;

    /// <summary>
    /// Full origins a passkey ceremony may come from, scheme and port included — this server's own
    /// allow-list, checked against the ceremony's <c>clientDataJSON.origin</c> (a browser enforces the
    /// relying-party id match on its own; this is this application's separate check).
    /// <para>
    /// **Always includes <c>App:Url</c>'s own origin implicitly** — the common case (one console,
    /// reached at one address) needs no configuration at all. <c>App:AlternateUrls</c> only ever adds to
    /// that, never replaces it, so there's exactly one thing to reason about: "the console's own address,
    /// plus whatever else is listed" — never "is the primary one already in this list or not".
    /// </para>
    /// </summary>
    public required IReadOnlySet<string> Origins { get; init; }

    /// <param name="configuration">Where the product's <c>Auth:Passkeys</c> section is read from.</param>
    /// <param name="product">Names the section and the default relying-party name.</param>
    /// <param name="url">The deployment's own URL, already resolved by the product — read from there rather than re-reading <c>IConfiguration</c>
    /// directly, so there is one answer to "what is this deployment's own URL", not two resolutions of the same setting that could disagree.</param>
    /// <param name="alternateUrls">Other addresses the same deployment is reached at.</param>
    /// <param name="originsWhenNothingConfigured">What a from-scratch development environment needs when nothing configures the URL at all (an https variant, and
    /// the Vite dev server's own port), used only when <paramref name="alternateUrls"/> is empty, so a real deployment's origin list never gets localhost-only entries.</param>
    public static PasskeyOptions FromConfiguration(
        IConfiguration configuration, AuthProduct product, string url, IReadOnlyList<string> alternateUrls, IReadOnlyList<string> originsWhenNothingConfigured)
    {
        var section = configuration.GetSection($"{product.Section}:Passkeys");
        var id = section["RelyingPartyId"];

        var origins = new List<string> { url };
        origins.AddRange(alternateUrls.Count > 0 ? alternateUrls : originsWhenNothingConfigured);

        return new PasskeyOptions
        {
            // localhost is the only default that can be right without being told, and it is the one a
            // freshly installed tool actually runs at.
            RelyingPartyId = string.IsNullOrWhiteSpace(id) ? "localhost" : id,
            RelyingPartyName = section["RelyingPartyName"] ?? product.Name,
            Mode = ConfigEnum.Parse(section["Mode"], FeatureMode.Enabled),
            Origins = origins.ToHashSet(StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// What is wrong with this configuration, or null. Checked at startup so a deployment that cannot
    /// work says so then, rather than the first time somebody tries to enrol a key.
    /// </summary>
    public string? Problem()
    {
        if (RelyingPartyId.Contains("://", StringComparison.Ordinal) || RelyingPartyId.Contains(':'))
        {
            return
                $"The passkey relying-party id '{RelyingPartyId}' looks like a URL. It has to be a bare " +
                "domain — no scheme, no port. Put the full URL in App:AlternateUrls instead.";
        }

        if (System.Net.IPAddress.TryParse(RelyingPartyId, out _))
        {
            return
                $"The passkey relying-party id '{RelyingPartyId}' is an IP address. WebAuthn requires a " +
                "domain name, so passkeys cannot work on a deployment reached only by IP.";
        }

        var mismatched = Origins
            .Where(o => Uri.TryCreate(o, UriKind.Absolute, out var uri)
                        && !uri.Host.Equals(RelyingPartyId, StringComparison.OrdinalIgnoreCase)
                        && !uri.Host.EndsWith("." + RelyingPartyId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return mismatched.Count == 0
            ? null
            : $"Passkey origin(s) {string.Join(", ", mismatched)} (from App:Url/App:AlternateUrls) are not " +
              $"under the relying-party id '{RelyingPartyId}'. A browser refuses a ceremony whose origin " +
              "does not match, and the message it gives names neither.";
    }
}
