using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;
using DbDataKit.Serving;
using DbDataKit.State;

namespace DbDataKit.Views.Hosting;

/// <summary>
/// The calls of the person's own dashboards and reports (plan 32 DBR12; routes <c>/api/v1/my/dashboards</c> and <c>/api/v1/my/reports</c>, and the list <c>/api/v1/my/views</c>). The caller's identity is the owner of whatever they save: no call names another
/// owner except to **open or render** a document that owner shared with the caller (<c>?owner=</c>); changing, sharing, restoring, promoting and deleting are the owner's, on their own documents only. What a person may open is the store's decision
/// (<see cref="UserViewStore.Find"/>: their own, or shared with them by user, role or everyone, and the same 404 for a document that is not there and one that is not for them); **the rows a document shows are the serving core's**, as for every
/// render: the document runs as whoever opens it, with that person's roles and attributes. A document is checked as a publish would check it (<see cref="ViewsPublications.Draft"/>: the loader, the catalog check over the model versions the channel serves) when it is
/// saved and when it is rendered, and is never cached across people. An app's token has no personal documents (403): they belong to a person, not to what a person uses. Every call is audited (<c>view.my.*</c>).
/// </summary>
public sealed partial class ViewsApi
{
    /// <summary>The routes of a person's documents start with this.</summary>
    public const string MyPrefix = "/api/v1/my/";

    private const string NotKept = "This host keeps no personal dashboards or reports: its state database has no `user_views` module.";

    /// <summary>The caller as the store knows them: their id owns what they make; their name and groups are what others share with.</summary>
    private static UserViewViewer Viewer(ViewsPrincipal who) => new(who.Caller.Id, who.Caller.Name, who.Groups ?? who.Caller.Roles);

    private static string Me(ViewsPrincipal who) => who.Caller.Id.ToLowerInvariant();

    private static string Target(string owner, string name) => $"{owner.ToLowerInvariant()}/{name}";

    private ViewsAnswer? Gate(ViewsPrincipal who, string verb, string kind, string? name, string? traceId)
    {
        if (options.UserViews is null) return ViewsAnswer.Error(404, "not_found", NotKept);
        if (who.App is not null)
        {
            Record(who, verb, kind, name, null, AuditEvent.Refused, Detail(traceId, ("code", "denied"), ("reason", "app_scope")));
            Interlocked.Increment(ref refused);
            return ViewsAnswer.Error(403, "denied", $"The app '{who.App.Instance}' has no personal dashboards or reports: they belong to a person, who opens them in the console.");
        }
        return null;
    }

    private ViewsAnswer MyRefuse(ViewsPrincipal who, string verb, string kind, string? target, int? version, int status, string code, string message, string? traceId, params (string Key, object? Value)[] more)
    {
        Record(who, verb, kind, target, version, AuditEvent.Refused, Detail(traceId, [("code", code), .. more]));
        Interlocked.Increment(ref refused);
        return ViewsAnswer.Error(status, code, message);
    }

    private ViewsAnswer NotFound(ViewsPrincipal who, string verb, string kind, string owner, string name, string? traceId) =>
        MyRefuse(who, verb, kind, Target(owner, name), null, 404, "not_found", $"There is no {kind} '{name}' of {owner} that you may open.", traceId);

    private ViewsAnswer Conflict(ViewsPrincipal who, string verb, string kind, string owner, string name, UserViewConflictException e, string? traceId)
    {
        Record(who, verb, kind, Target(owner, name), null, AuditEvent.Refused, Detail(traceId, ("code", e.CurrentVersion is null ? "not_found" : "version_conflict")));
        Interlocked.Increment(ref refused);
        var body = new JsonObject { ["code"] = e.CurrentVersion is null ? "not_found" : "version_conflict", ["message"] = e.Message };
        if (e.CurrentVersion is { } v) body["current_version"] = v.ToString(CultureInfo.InvariantCulture);
        return new ViewsAnswer(e.CurrentVersion is null ? 404 : 409, body);
    }

    /// <summary>The document checked as a publish would check it and built for this caller to resolve, or null with the refusal (as an answer).</summary>
    private PublishedView? Build(string kind, string owner, string name, string text, int version, out string? refusal, out IReadOnlyList<ConfigDiagnostic> diagnostics) =>
        published.Draft(kind, Target(owner, name), text, out refusal, out diagnostics, version);

    private static string V(int version) => version.ToString(CultureInfo.InvariantCulture);

    // ---- list ----

    /// <summary>The caller's documents: <c>scope</c> is <c>mine</c>, <c>shared</c> (what others shared with them) or <c>all</c> (the default).</summary>
    public ViewsAnswer MyList(ViewsPrincipal who, string? scope = null, string? kind = null, string? traceId = null)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            if (Gate(who, ViewsAuditVerbs.MyList, kind ?? ViewDefinitions.Dashboard, null, traceId) is { } gated) return gated;
            if (scope is not (null or "mine" or "shared" or "all")) return MyRefuse(who, ViewsAuditVerbs.MyList, kind ?? ViewDefinitions.Dashboard, null, null, 400, "invalid", "`scope` is `mine`, `shared` or `all`.", traceId);
            if (kind is not (null or ViewDefinitions.Dashboard or ViewDefinitions.Report)) return MyRefuse(who, ViewsAuditVerbs.MyList, ViewDefinitions.Dashboard, null, null, 400, "invalid", "`kind` is `dashboard` or `report`.", traceId);
            var items = new JsonArray();
            foreach (var v in options.UserViews!.List(Viewer(who), kind, own: scope is null or "mine" or "all", shared: scope is null or "shared" or "all"))
                items.Add(new JsonObject
                {
                    ["kind"] = v.Kind, ["owner"] = v.Owner, ["owner_name"] = v.OwnerName, ["name"] = v.Name, ["title"] = v.Title, ["version"] = V(v.Version), ["updated_at"] = Stamp(v.UpdatedAt), ["own"] = v.Own,
                    ["shared_with"] = new JsonArray([.. v.SharedWith.Select(g => (JsonNode?)JsonValue.Create(g))]),
                });
            return Finish(who, ViewsAuditVerbs.MyList, kind ?? "any", null, null, AuditEvent.Ok, Detail(traceId, ("count", items.Count), ("scope", scope ?? "all")), new ViewsAnswer(200, new JsonObject { ["items"] = items }));
        }
    }

    // ---- open ----

    /// <summary>The document as the caller may open it: their own (<paramref name="owner"/> null or their name) or one its owner shared with them. The owner also gets who it is shared with.</summary>
    public ViewsAnswer MyOpen(ViewsPrincipal who, string kind, string name, string? owner = null, string? traceId = null)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            if (Gate(who, ViewsAuditVerbs.MyOpen, kind, name, traceId) is { } gated) return gated;
            owner ??= Me(who);
            var store = options.UserViews!;
            var view = store.Find(Viewer(who), owner, kind, name);
            if (view is null) return NotFound(who, ViewsAuditVerbs.MyOpen, kind, owner, name, traceId);
            var own = string.Equals(view.Owner, Me(who), StringComparison.Ordinal);
            var layouts = new JsonArray();
            JsonNode? document = null;
            string? problem = null;
            if (Build(kind, view.Owner, view.Name, view.Text, view.Version, out problem, out _) is { } built)
            {
                document = built.DocumentJson();
                foreach (var layout in built.Document.Layouts)
                    layouts.Add(new JsonObject { ["name"] = layout.Name, ["type"] = layout.Type.ToString().ToLowerInvariant(), ["default"] = layout.IsDefault });
            }
            var body = new JsonObject
            {
                ["kind"] = kind, ["owner"] = view.Owner, ["owner_name"] = view.OwnerName, ["name"] = view.Name, ["title"] = view.Title, ["version"] = V(view.Version), ["as_of"] = Stamp(view.UpdatedAt), ["updated_by"] = view.UpdatedBy,
                ["own"] = own, ["text"] = view.Text, ["document"] = document, ["layouts"] = layouts,
            };
            // a document the checks refuse now (a model it names was changed) is still opened, with the words, so its owner can mend it
            if (problem is not null) body["problem"] = problem;
            if (own) body["shared_with"] = new JsonArray([.. store.Shares(view.Owner, kind, name).Select(g => (JsonNode?)JsonValue.Create(g))]);
            return Finish(who, ViewsAuditVerbs.MyOpen, kind, Target(view.Owner, name), view.Version, AuditEvent.Ok, Detail(traceId, ("own", own)), new ViewsAnswer(200, body));
        }
    }

    // ---- save ----

    /// <summary>
    /// Saves the caller's document: <c>{text, expected_version?}</c>. A new name needs no version; an existing one needs the version that was read, and another is a 409 that says which is stored. The text is checked as a publish would check it
    /// (400 <c>invalid</c> with the loader's diagnostics placed in the text) and nothing is stored when it fails. Answers <c>{owner, name, version}</c>.
    /// </summary>
    public ViewsAnswer MySave(ViewsPrincipal who, string kind, string name, JsonObject? body, string? traceId = null)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            if (Gate(who, ViewsAuditVerbs.MySave, kind, name, traceId) is { } gated) return gated;
            var owner = Me(who);
            if (!UserViewStore.IsName(name)) return MyRefuse(who, ViewsAuditVerbs.MySave, kind, Target(owner, name), null, 400, "invalid", $"A document's name is lower case letters, digits, '_' and '-', starting with a letter, at most {UserViewStore.MaxNameLength} characters; `{name}` is not.", traceId);
            string text;
            int? expected = null;
            try
            {
                if (body is null) throw new ArgumentException("A save has `text` (the document's YAML or JSON) and, for a document that exists, `expected_version`.");
                foreach (var key in body.Select(p => p.Key))
                    if (key is not ("text" or "expected_version"))
                        throw new ArgumentException($"Unknown key `{key}`: a save has `text` and `expected_version`.");
                text = body["text"] is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : throw new ArgumentException("`text` must be the document's text.");
                if (body["expected_version"] is { } e)
                    expected = e is JsonValue ev && (ev.TryGetValue<int>(out var n) ? (int?)n : ev.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : null) is { } parsed
                        ? parsed : throw new ArgumentException("`expected_version` is the version that was read.");
            }
            catch (ArgumentException e)
            {
                return MyRefuse(who, ViewsAuditVerbs.MySave, kind, Target(owner, name), null, 400, "invalid", e.Message, traceId);
            }

            var built = Build(kind, owner, name, text, expected ?? 0, out var refusal, out var diagnostics);
            if (built is null)
            {
                Record(who, ViewsAuditVerbs.MySave, kind, Target(owner, name), null, AuditEvent.Refused, Detail(traceId, ("code", "invalid"), ("problems", diagnostics.Count)));
                Interlocked.Increment(ref refused);
                return new ViewsAnswer(400, new JsonObject { ["code"] = "invalid", ["message"] = refusal, ["diagnostics"] = new JsonArray(diagnostics.Select(Diagnostic).ToArray()) });
            }
            try
            {
                var saved = options.UserViews!.Save(owner, kind, name, built.Document.Title, text, who.Caller.Name, expected, who.Caller.Name);
                return Finish(who, ViewsAuditVerbs.MySave, kind, Target(owner, name), saved.Version, AuditEvent.Ok, Detail(traceId, ("created", expected is null)),
                    new ViewsAnswer(200, new JsonObject { ["owner"] = saved.Owner, ["name"] = saved.Name, ["version"] = V(saved.Version) }));
            }
            catch (UserViewConflictException e)
            {
                return Conflict(who, ViewsAuditVerbs.MySave, kind, owner, name, e, traceId);
            }
        }
    }

    // ---- delete ----

    public ViewsAnswer MyDelete(ViewsPrincipal who, string kind, string name, string? traceId = null)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            if (Gate(who, ViewsAuditVerbs.MyDelete, kind, name, traceId) is { } gated) return gated;
            var owner = Me(who);
            var existed = options.UserViews!.Delete(owner, kind, name);
            if (!existed) return NotFound(who, ViewsAuditVerbs.MyDelete, kind, owner, name, traceId);
            return Finish(who, ViewsAuditVerbs.MyDelete, kind, Target(owner, name), null, AuditEvent.Ok, Detail(traceId), new ViewsAnswer(200, new JsonObject { ["deleted"] = true }));
        }
    }

    // ---- render ----

    /// <summary>Resolves the document **as the caller** (<paramref name="owner"/>'s own, or one shared with the caller): the body is a render request's. The rows are the caller's, whoever made the document; nothing is cached.</summary>
    public async Task<ViewsAnswer> MyRenderAsync(ViewsPrincipal who, string kind, string name, string? owner, JsonObject? body, string? clientAddress = null, string? traceId = null, CancellationToken cancellationToken = default)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            if (Gate(who, ViewsAuditVerbs.MyRender, kind, name, traceId) is { } gated) return gated;
            owner ??= Me(who);
            var view = options.UserViews!.Find(Viewer(who), owner, kind, name);
            if (view is null) return NotFound(who, ViewsAuditVerbs.MyRender, kind, owner, name, traceId);
            if (Build(kind, view.Owner, view.Name, view.Text, view.Version, out var refusal, out _) is not { } built)
                return MyRefuse(who, ViewsAuditVerbs.MyRender, kind, Target(view.Owner, name), view.Version, 400, "invalid", $"The {kind} '{name}' of {view.Owner} cannot be drawn now: {refusal}", traceId);
            return await RenderViewAsync(who, kind, built, body, ViewsAuditVerbs.MyRender, clientAddress, traceId, cancellationToken, isPublished: false).ConfigureAwait(false);
        }
    }

    // ---- history and restore ----

    /// <summary>The owner's history, newest first: <c>{items: [{version, saved_by, saved_at, current}]}</c>; with <paramref name="version"/> that one version with its <c>text</c>.</summary>
    public ViewsAnswer MyHistory(ViewsPrincipal who, string kind, string name, string? version = null, string? traceId = null)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            if (Gate(who, ViewsAuditVerbs.MyHistory, kind, name, traceId) is { } gated) return gated;
            var owner = Me(who);
            var history = options.UserViews!.History(owner, kind, name);
            if (history.Count == 0) return NotFound(who, ViewsAuditVerbs.MyHistory, kind, owner, name, traceId);
            if (version is not null)
            {
                if (!int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || history.FirstOrDefault(v => v.Version == n) is not { } one)
                    return MyRefuse(who, ViewsAuditVerbs.MyHistory, kind, Target(owner, name), null, 404, "not_found", $"The {kind} '{name}' has no version {version}.", traceId);
                return Finish(who, ViewsAuditVerbs.MyHistory, kind, Target(owner, name), n, AuditEvent.Ok, Detail(traceId),
                    new ViewsAnswer(200, new JsonObject { ["version"] = V(one.Version), ["saved_by"] = one.SavedBy, ["saved_at"] = Stamp(one.SavedAt), ["current"] = one.Current, ["text"] = one.Text }));
            }
            var items = new JsonArray([.. history.Select(v => (JsonNode?)new JsonObject { ["version"] = V(v.Version), ["saved_by"] = v.SavedBy, ["saved_at"] = Stamp(v.SavedAt), ["current"] = v.Current })]);
            return Finish(who, ViewsAuditVerbs.MyHistory, kind, Target(owner, name), history[0].Version, AuditEvent.Ok, Detail(traceId, ("versions", history.Count)), new ViewsAnswer(200, new JsonObject { ["items"] = items }));
        }
    }

    private sealed class RestoreRefused(string message) : Exception(message);

    /// <summary>Makes an earlier version current again as a **new** version: <c>{version, expected_version}</c> (the version the caller sees as current: a stale one is a 409). The old text must still pass the checks (a model it names may have changed since).</summary>
    public ViewsAnswer MyRestore(ViewsPrincipal who, string kind, string name, JsonObject? body, string? traceId = null)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            if (Gate(who, ViewsAuditVerbs.MyRestore, kind, name, traceId) is { } gated) return gated;
            var owner = Me(who);
            int version, expected;
            try
            {
                int Number(string key) => body?[key] is JsonValue v && (v.TryGetValue<int>(out var n) ? (int?)n : v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : null) is { } parsed
                    ? parsed : throw new ArgumentException($"A restore has `version` (the one to bring back) and `expected_version` (the one you see as current); `{key}` is missing or not a number.");
                version = Number("version");
                expected = Number("expected_version");
            }
            catch (ArgumentException e)
            {
                return MyRefuse(who, ViewsAuditVerbs.MyRestore, kind, Target(owner, name), null, 400, "invalid", e.Message, traceId);
            }
            try
            {
                var restored = options.UserViews!.Restore(owner, kind, name, version, who.Caller.Name, expected, text =>
                    Build(kind, owner, name, text, version, out var refusal, out _)?.Document.Title ?? throw new RestoreRefused($"Version {version} no longer passes the checks: {refusal}"), who.Caller.Name);
                return Finish(who, ViewsAuditVerbs.MyRestore, kind, Target(owner, name), restored.Version, AuditEvent.Ok, Detail(traceId, ("restored", version)),
                    new ViewsAnswer(200, new JsonObject { ["owner"] = restored.Owner, ["name"] = restored.Name, ["version"] = V(restored.Version), ["restored"] = V(version) }));
            }
            catch (RestoreRefused e)
            {
                return MyRefuse(who, ViewsAuditVerbs.MyRestore, kind, Target(owner, name), null, 400, "invalid", e.Message, traceId);
            }
            catch (UserViewConflictException e)
            {
                return Conflict(who, ViewsAuditVerbs.MyRestore, kind, owner, name, e, traceId);
            }
        }
    }

    // ---- shares ----

    /// <summary>Who the document is shared with, as the owner sets it: <c>PUT {grantees: ["user:alice", "group:analyst", "*"]}</c> replaces the list (what is not named is unshared); <c>GET</c> (<paramref name="body"/> null) reads it.</summary>
    public ViewsAnswer MyShares(ViewsPrincipal who, string kind, string name, JsonObject? body, bool write, string? traceId = null)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            if (Gate(who, ViewsAuditVerbs.MyShare, kind, name, traceId) is { } gated) return gated;
            var owner = Me(who);
            var store = options.UserViews!;
            if (store.Get(owner, kind, name) is null) return NotFound(who, ViewsAuditVerbs.MyShare, kind, owner, name, traceId);
            var before = store.Shares(owner, kind, name);
            if (!write) return Finish(who, ViewsAuditVerbs.MyShare, kind, Target(owner, name), null, AuditEvent.Ok, Detail(traceId, ("read", true)), SharesAnswer(before));
            List<string> wanted;
            try
            {
                if (body?["grantees"] is not JsonArray list || body.Count != 1) throw new ArgumentException("A share list is {\"grantees\": [\"user:<name>\", \"group:<role>\", \"*\"]}.");
                wanted = [.. list.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) && UserViewStore.IsGrantee(s) ? s.ToLowerInvariant() : throw new ArgumentException("A grantee is user:<name>, group:<role> or *.")).Distinct()];
                wanted.RemoveAll(g => Viewer(who).Grantees.Contains(g, StringComparer.Ordinal) && g.StartsWith("user:", StringComparison.Ordinal));   // sharing with yourself is no share
            }
            catch (ArgumentException e)
            {
                return MyRefuse(who, ViewsAuditVerbs.MyShare, kind, Target(owner, name), null, 400, "invalid", e.Message, traceId);
            }
            try
            {
                foreach (var grantee in wanted.Except(before, StringComparer.Ordinal)) store.Share(owner, kind, name, grantee, who.Caller.Name);
                foreach (var grantee in before.Except(wanted, StringComparer.Ordinal)) store.Unshare(owner, kind, name, grantee);
            }
            catch (UserViewConflictException e)
            {
                return Conflict(who, ViewsAuditVerbs.MyShare, kind, owner, name, e, traceId);
            }
            var after = store.Shares(owner, kind, name);
            return Finish(who, ViewsAuditVerbs.MyShare, kind, Target(owner, name), null, AuditEvent.Ok,
                Detail(traceId, ("added", after.Except(before).ToList()), ("removed", before.Except(after).ToList())), SharesAnswer(after));
        }
    }

    private static ViewsAnswer SharesAnswer(IReadOnlyList<string> grantees) =>
        new(200, new JsonObject { ["grantees"] = new JsonArray([.. grantees.Select(g => (JsonNode?)JsonValue.Create(g))]) });

    // ---- promote ----

    /// <summary>The document as a file for an app's source or a Build project: <c>{path, text}</c>, the text one the views loader reads (<see cref="UserViewStore.Promote"/>). The owner's document is untouched.</summary>
    public ViewsAnswer MyPromote(ViewsPrincipal who, string kind, string name, string? traceId = null)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            if (Gate(who, ViewsAuditVerbs.MyPromote, kind, name, traceId) is { } gated) return gated;
            var owner = Me(who);
            try
            {
                var file = options.UserViews!.Promote(owner, kind, name);
                return Finish(who, ViewsAuditVerbs.MyPromote, kind, Target(owner, name), null, AuditEvent.Ok, Detail(traceId, ("path", file.Path)),
                    new ViewsAnswer(200, new JsonObject { ["path"] = file.Path, ["text"] = file.Text }));
            }
            catch (UserViewConflictException e)
            {
                return Conflict(who, ViewsAuditVerbs.MyPromote, kind, owner, name, e, traceId);
            }
            catch (InvalidOperationException e)
            {
                return MyRefuse(who, ViewsAuditVerbs.MyPromote, kind, Target(owner, name), null, 400, "invalid", e.Message, traceId);
            }
        }
    }
}
