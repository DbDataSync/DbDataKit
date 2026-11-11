namespace DbDataKit.Apps;

public enum PortalMenu { Top, Side }

public enum PortalWidth { Narrow, Medium, Wide, Full }

/// <summary>An entry of a portal's menu: an app instance mounted in the portal, a link, a group of entries, or a divider. <c>Role</c> is the role that shows it.</summary>
public enum NavKind { App, Link, Group, Divider }

public sealed record PortalIdentity(string? Title, string? Logo, string? Favicon, string? SignInText, string? FooterText, string? SupportLink);

/// <summary>Design tokens for light and dark. They become the CSS variables the suite's controls read (<c>--sui-*</c>), so every app that uses the shared controls follows the portal. A value is a colour, a size or a font list, never a reference to anything outside the page.</summary>
public sealed record PortalTheme(IReadOnlyDictionary<string, string> Light, IReadOnlyDictionary<string, string> Dark);

public sealed record PortalLayout(PortalMenu Menu, bool Footer, PortalWidth Width, IReadOnlyList<string> Header);

public sealed record PortalNav(NavKind Kind, string? Label, string? Icon, string? App, string? Href, string? Role, IReadOnlyList<PortalNav> Children);

/// <summary>An app instance the portal mounts, at a path under the portal's own address.</summary>
public sealed record PortalMount(string App, string Path);

/// <summary>The portal's own addresses besides its subpath: a subdomain of the host, and custom domains.</summary>
public sealed record PortalAddresses(string? Subdomain, IReadOnlyList<string> Domains);

/// <summary>
/// What a portal declares (plan 26 section 4a): identity, theme, layout, navigation, home, mounts, slots and addresses. A declared portal is data the host's shell renders and contains no script of its own;
/// a portal that asks <c>trust: trusted</c> may carry code, which runs only with an administrator's approval of that exact version.
/// </summary>
public sealed record PortalSpec(
    PortalIdentity Identity,
    PortalTheme Theme,
    PortalLayout Layout,
    IReadOnlyList<PortalNav> Navigation,
    IReadOnlyList<string> Home,
    IReadOnlyList<PortalMount> Mounts,
    IReadOnlyDictionary<string, string> Slots,
    PortalAddresses Addresses);
