using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Serving;
using DbDataKit.State;
using DbDataKit.Views;
using DbDataKit.Views.Export;
using DbDataKit.Views.Hosting;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Serving;

/// <summary>The outcome of a publish of a dashboard or a report: a status and the JSON a caller is given, in the shape of the management API.</summary>
public sealed record ViewsPublishOutcome(int Status, JsonObject Body);

/// <summary>
/// Publishing a dashboard or a report to the group (plan 32, DBR11b): the kinds <c>dashboard</c> and <c>report</c> in the registry of a <see cref="DefinitionPublisher"/> over the Proxy's state, the document's <c>depends_on</c> written from the
/// versions of the published semantic models the channel serves (a model set up by hand in <c>server.yaml</c> is not a published definition, so a document cannot depend on one), and every refusal in the registry's own words. One class for the
/// management API (<c>PUT /api/v1/dashboards/{name}</c>) and the command line (<c>dbdataproxy views publish</c>), neither of which needs the module to be on: what is published is served by every instance that runs it.
/// </summary>
public sealed class ProxyViewsPublishing(DefinitionStore store, StateDatabase state)
{
    private readonly DefinitionPublisher publisher = new(state, ViewDefinitions.Register(new DefinitionKinds(), ViewPublishingSources.From(store)), null, "serving");

    public DefinitionPublisher Publisher => publisher;

    public const string ModelKind = ViewDefinitions.ModelKind;

    /// <summary>The model versions the production channel serves, by the name a document writes (the published definition is lower case).</summary>
    private int? ModelVersion(string model) => store.Current(ModelKind, model.ToLowerInvariant())?.Version;

    public static bool ValidName(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$");

    /// <summary>Publishes <paramref name="text"/> as the dashboard or report <paramref name="name"/> and serves it on the production channel.</summary>
    /// <param name="expectedVersion">The registry's highest version number the caller saw (0 for a first publish); null does not check.</param>
    public ViewsPublishOutcome Publish(string kind, string name, string text, string by, int? expectedVersion = null, string? note = null)
    {
        if (!ViewDefinitions.Kinds.Contains(kind)) return Refused(404, $"There is no kind '{kind}'; the kinds are dashboard and report.");
        if (!ValidName(name)) return Refused(400, "A document's name is letters, digits, dots, dashes and underscores (up to 64), starting with a letter or digit.");
        if (string.IsNullOrWhiteSpace(text)) return Refused(400, "There is no document: the body is {\"text\": \"<the file's yaml or json>\", \"expected_version\": <number, or absent>}.");
        try
        {
            var depends = ViewDefinitions.DependsOn(kind, name, text, ModelVersion);
            var before = store.Current(kind, name)?.Version;
            var result = publisher.Publish(new PublishRequest(kind, name, text, by, DefinitionStore.Production, PublishMode.Serve, depends, note, ExpectedVersion: expectedVersion));
            return new ViewsPublishOutcome(200, new JsonObject
            {
                ["kind"] = kind, ["name"] = name, ["version"] = result.Version.Version.ToString(CultureInfo.InvariantCulture), ["created"] = before is null, ["unchanged"] = !result.Changed,
                ["depends_on"] = new JsonArray([.. depends.Select(d => (JsonNode)d)]),
            });
        }
        catch (PublishRefusedException ex)
        {
            // a version the channel's models cannot carry is not a malformed request, but a state of the group
            var status = ex.Reason is PublishRefusal.DependencyNotActive or PublishRefusal.Format ? 409 : 400;
            return Refused(status, ex.Message + Hint(ex.Message));
        }
        catch (DefinitionRejectedException ex)
        {
            return Refused(400, ex.Message + Hint(ex.Message));
        }
        catch (DefinitionConflictException ex)
        {
            return new ViewsPublishOutcome(409, new JsonObject { ["error"] = ex.Message, ["code"] = "version_conflict", ["message"] = ex.Message, ["current_version"] = store.Versions(kind, name).Select(v => v.Version).DefaultIfEmpty(0).Max() });
        }
    }

    private static string Hint(string message) =>
        message.Contains("DBD-VIEW-060", StringComparison.Ordinal) && message.Contains("no version", StringComparison.OrdinalIgnoreCase)
            ? " (A document depends on a semantic model published to the group, as `dbdatabuild` or the semantic-models API does; a model set up in server.yaml is not one.)"
            : "";

    private static ViewsPublishOutcome Refused(int status, string message) =>
        new(status, new JsonObject { ["error"] = message, ["code"] = status == 400 ? "invalid" : status == 404 ? "not_found" : "refused", ["message"] = message });

    /// <summary>What the channel serves of each kind: name, title-less head and version, for the command line and the status.</summary>
    public IReadOnlyList<DefinitionHead> List(string? kind = null) => ViewDefinitions.List(publisher, kind);
}

/// <summary>
/// The views module as this Proxy runs it (plan 32, DBR11b): one <see cref="ViewsModule"/> over the Proxy's serving core, whose own listener the Proxy does not use (<see cref="ProxyViewsModule"/> mounts its routes on the Proxy's listener, with the
/// Proxy's certificate), the publications every instance's definition follower hands the served versions to, and what turns a person into a caller: a request's token on the module's listener, a console's signed token on the management API.
/// </summary>
public sealed class ProxyViews
{
    public ProxyViews(DefinitionStore store, ProxyViewsPublishing publishing, IOptionsMonitor<ProxyOptions> options, HomeCallers homeCallers, StateDatabase state, ILoggerFactory loggers)
    {
        Publishing = publishing;
        this.homeCallers = homeCallers;
        this.options = options;
        Publications = new ViewsPublications(store, publishing.Publisher);
        Module = new ViewsModule(new ViewsOptions
        {
            Listen = null,
            Follow = false,
            CacheTtl = TimeSpan.FromSeconds(options.CurrentValue.Views.CacheSeconds),
            Exporter = ExporterOf(options.CurrentValue.Views.Export, loggers),
            PageAssets = options.CurrentValue.Views.Export.PageAssets,
        }, Publications, new Callers(this), new StateViewsAudit(new AuditLog(state, DbDataProxy.Server.State.ProxyState.ServiceId)), loggers);
    }

    /// <summary>The PDF exporter of <c>views.export</c> (plan 32 DBR15), or null when the section gives no <c>Mode</c>: export is off unless a host turns it on.</summary>
    private static ChromiumExporter? ExporterOf(ViewsExportOptions e, ILoggerFactory loggers) =>
        string.IsNullOrEmpty(e.Mode) ? null : new ChromiumExporter(new ExportSettings
        {
            Mode = e.Mode == "system" ? BrowserMode.System : BrowserMode.Pinned,
            Path = e.Path, Pin = e.Pin, PageAssets = e.PageAssets, Deadline = TimeSpan.FromSeconds(e.DeadlineSeconds), MaxPages = e.MaxPages, MaxConcurrent = e.MaxConcurrent, NoSandbox = e.NoSandbox,
            FontsDirectory = e.Fonts.Directory, FontsPin = e.Fonts.Pin, AllowedFonts = [.. e.Fonts.Allowed], RequiredFonts = [.. e.Fonts.Required],
        }, loggers.CreateLogger("Views.Export"));

    private readonly HomeCallers homeCallers;
    private readonly IOptionsMonitor<ProxyOptions> options;

    public ProxyViewsPublishing Publishing { get; }

    public ViewsPublications Publications { get; }

    public ViewsModule Module { get; }

    /// <summary>Where people reach the module's listener (what <see cref="DbDataProxy.Server.Hosting.ProxyViewsModule"/> bound), for the status.</summary>
    public string? Address { get; set; }

    /// <summary>The caller of a person the identity home knows (a token of the home, or the user id a console vouched for), or null.</summary>
    public async ValueTask<ViewsPrincipal?> HomePrincipalAsync(UserRecord? user, string method, CancellationToken cancellationToken)
    {
        if (user is null) return null;
        var home = await homeCallers.GetAsync(cancellationToken).ConfigureAwait(false);
        return new ViewsPrincipal(new ProxyCaller(HomePrincipals.Create(home, user, method, options.CurrentValue)));
    }

    /// <summary>
    /// The caller behind a console's signed token: a person who signs in through the identity home is that home user (their groups decide which data sources they may read, as for any client of the Proxy); a person who is only the console's has
    /// no home user and no data source, so every widget of theirs is refused in words rather than answered from someone else's access.
    /// </summary>
    public async ValueTask<ViewsPrincipal> ConsolePrincipalAsync(ValidatedToken token, CancellationToken cancellationToken)
    {
        var home = await homeCallers.GetAsync(cancellationToken).ConfigureAwait(false);
        if (home.UserOf(token.SubjectId) is { } user) return new ViewsPrincipal(new ProxyCaller(HomePrincipals.Create(home, user, "console", options.CurrentValue)));
        return new ViewsPrincipal(new ProxyCaller(new ProxyPrincipal(token.SubjectName ?? token.SubjectId, "console", [])));
    }

    private sealed class Callers(ProxyViews views) : IViewsCallers
    {
        public async ValueTask<ViewsPrincipal?> AuthenticateAsync(string? authorization, string? clientAddress, CancellationToken cancellationToken)
        {
            var home = await views.homeCallers.GetAsync(cancellationToken).ConfigureAwait(false);
            return await views.HomePrincipalAsync(home.Identify(authorization), "home-token", cancellationToken).ConfigureAwait(false);
        }
    }
}
