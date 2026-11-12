using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Serving;
using DbDataKit.State;
using DbDataKit.Views;
using DbDataKit.Views.Export;
using DbDataKit.Views.Hosting;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Server.Serving;

/// <summary>The outcome of a publish of a dashboard or a report: a status and the JSON a caller is given, in the shape of the management API.</summary>
public sealed record ViewsPublishOutcome(int Status, JsonObject Body);

/// <summary>
/// Publishing a dashboard or a report to the group (plan 32, DBR11b): the kinds <c>dashboard</c> and <c>report</c> in the registry of a <see cref="DefinitionPublisher"/> over the Gateway's state, the document's <c>depends_on</c> written from the
/// versions of the published semantic models the channel serves (a model set up by hand in <c>server.yaml</c> is not a published definition, so a document cannot depend on one), and every refusal in the registry's own words. One class for the
/// management API (<c>PUT /api/v1/dashboards/{name}</c>) and the command line (<c>dbdatagateway views publish</c>), neither of which needs the module to be on: what is published is served by every instance that runs it.
/// </summary>
public sealed class GatewayViewsPublishing(DefinitionStore store, StateDatabase state)
{
    private readonly DefinitionPublisher publisher = new(state, ViewDefinitions.Register(new DefinitionKinds(), ViewPublishingSources.From(store)), null, "serving");

    public DefinitionPublisher Publisher => publisher;

    public DefinitionStore Store => store;

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
/// The views module as this Gateway runs it (plan 32, DBR11b): one <see cref="ViewsModule"/> over the Gateway's serving core, whose own listener the Gateway does not use (<see cref="GatewayViewsModule"/> mounts its routes on the Gateway's listener, with the
/// Gateway's certificate), the publications every instance's definition follower hands the served versions to, and what turns a person into a caller: a request's token on the module's listener, a console's signed token on the management API.
/// </summary>
public sealed class GatewayViews
{
    public GatewayViews(DefinitionStore store, GatewayViewsPublishing publishing, IOptionsMonitor<GatewayOptions> options, HomeCallers homeCallers, StateDatabase state, ILoggerFactory loggers, IReportExporter? exporter = null)
    {
        Publishing = publishing;
        exporter ??= ExporterOf(options.CurrentValue.Views.Export, loggers);
        CanExport = exporter is not null;
        this.homeCallers = homeCallers;
        this.options = options;
        Publications = new ViewsPublications(store, publishing.Publisher);
        Module = new ViewsModule(new ViewsOptions
        {
            Listen = null,
            Follow = false,
            UserViews = new UserViewStore(state),
            CacheTtl = TimeSpan.FromSeconds(options.CurrentValue.Views.CacheSeconds),
            MaxExportRows = options.CurrentValue.Views.MaxExportRows,
            Exporter = exporter,
            PageAssets = options.CurrentValue.Views.Export.PageAssets,
            CustomApps = new GatewayCustomViewApps(() => new DbDataKit.Apps.AppInstanceStore(store),
                () => options.CurrentValue.Modules.Contains(GatewayModules.Apps) ? DbDataGateway.Server.Hosting.GatewayAppsModule.PublicUrlOf(options.CurrentValue) : null),
            Resolver = new ResolverOptions { FrameOrigins = new LiveOrigins(() => options.CurrentValue.Views.FrameOrigins) },
        }, Publications, new Callers(this), new StateViewsAudit(new AuditLog(state, DbDataGateway.Server.State.GatewayState.ServiceId)), loggers);
    }

    /// <summary>The origins of <c>views.frameOrigins</c> as the file says now: the resolver asks on every request, so an edit of <c>server.yaml</c> applies without a restart.</summary>
    private sealed class LiveOrigins(Func<IReadOnlyCollection<string>> read) : IReadOnlyCollection<string>
    {
        public int Count => read().Count;

        public IEnumerator<string> GetEnumerator() => read().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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
    private readonly IOptionsMonitor<GatewayOptions> options;

    public GatewayViewsPublishing Publishing { get; }

    public ViewsPublications Publications { get; }

    public ViewsModule Module { get; }

    /// <summary>Where people reach the module's listener (what <see cref="DbDataGateway.Server.Hosting.GatewayViewsModule"/> bound), for the status.</summary>
    public string? Address { get; set; }

    /// <summary>The caller of a person the identity home knows (a token of the home, or the user id a console vouched for), or null.</summary>
    public async ValueTask<ViewsPrincipal?> HomePrincipalAsync(UserRecord? user, string method, CancellationToken cancellationToken)
    {
        if (user is null) return null;
        var home = await homeCallers.GetAsync(cancellationToken).ConfigureAwait(false);
        return Principal(HomePrincipals.Create(home, user, method, options.CurrentValue));
    }

    /// <summary>Whether this instance has an exporter (<c>views.export</c> is on, or a host gave one): without it a subscription cannot be drawn and the subscriptions task does nothing.</summary>
    public bool CanExport { get; }

    /// <summary>
    /// The person a subscription is drawn as (plan 32 DBR18): the identity home's enabled user of that subject id with the groups the home gives them now, <b>never the Gateway's own identity</b>. Null when the home does not know the subject, or the user is disabled
    /// (the subscription then stops as lost access). A person who is only the console's has no home user and is lost access too.
    /// </summary>
    public async ValueTask<ViewsPrincipal?> SubscriberPrincipalAsync(string subject, CancellationToken cancellationToken)
    {
        var home = await homeCallers.GetAsync(cancellationToken).ConfigureAwait(false);
        return home.UserOf(subject) is { } user ? Principal(HomePrincipals.Create(home, user, "subscription", options.CurrentValue)) : null;
    }

    /// <summary>The home user of a subject id as the subscription service addresses them (enabled ones only), or null.</summary>
    public async ValueTask<UserRecord?> SubscriberAsync(string subject, CancellationToken cancellationToken) =>
        (await homeCallers.GetAsync(cancellationToken).ConfigureAwait(false)).UserOf(subject);

    /// <summary>
    /// The caller behind a console's signed token: a person who signs in through the identity home is that home user (their groups decide which data sources they may read, as for any client of the Gateway); a person who is only the console's has
    /// no home user and no data source, so every widget of theirs is refused in words rather than answered from someone else's access.
    /// </summary>
    public async ValueTask<ViewsPrincipal> ConsolePrincipalAsync(ValidatedToken token, CancellationToken cancellationToken)
    {
        var home = await homeCallers.GetAsync(cancellationToken).ConfigureAwait(false);
        if (home.UserOf(token.SubjectId) is { } user) return Principal(HomePrincipals.Create(home, user, "console", options.CurrentValue));
        return Principal(new GatewayPrincipal(token.SubjectName ?? token.SubjectId, "console", []));
    }

    /// <summary>A person as the module knows them: the Gateway's caller, and the identity home's groups (a person's own documents are shared to a group of the home; the caller has no roles).</summary>
    private static ViewsPrincipal Principal(GatewayPrincipal principal) => new(new GatewayCaller(principal), Groups: principal.HomeGroups);

    private sealed class Callers(GatewayViews views) : IViewsCallers
    {
        public async ValueTask<ViewsPrincipal?> AuthenticateAsync(string? authorization, string? clientAddress, CancellationToken cancellationToken)
        {
            var home = await views.homeCallers.GetAsync(cancellationToken).ConfigureAwait(false);
            return await views.HomePrincipalAsync(home.Identify(authorization), "home-token", cancellationToken).ConfigureAwait(false);
        }
    }
}
