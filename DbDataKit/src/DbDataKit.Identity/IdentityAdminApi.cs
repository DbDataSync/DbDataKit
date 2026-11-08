using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Identity;

/// <summary>What an admin call answered: an HTTP status and a JSON body.</summary>
public sealed record IdentityAdminResult(int Status, JsonNode Body)
{
    public static IdentityAdminResult Ok(JsonNode body) => new(200, body);
    public static IdentityAdminResult NoContent() => new(204, new JsonObject());
    public static IdentityAdminResult Problem(int status, string error) => new(status, new JsonObject { ["error"] = error });
}

/// <summary>
/// The identity home's admin API (plan 13a U21; plan 16, "Sign-in decisions" item 5; 13c D30): people, groups, role assignments, app clients, external group mappings, personal access tokens and service accounts, on the
/// management API of whatever hosts the home (the serving layer's management listener, <c>dbdatakit identity run</c>'s), under <see cref="Prefix"/>. A console pairs with that host like any server and shows the screens through its
/// relay; every call needs the admin role the console's token carries. Transport-agnostic: a host passes the method, the path below the prefix, the body and who is calling. The commands (<see cref="IdentityVerbs"/>) do the same on the
/// state directly when no console answers.
/// <list type="bullet">
/// <item><c>GET people</c>; <c>POST people</c> <c>{"email","role"}</c> (an invitation: its link is answered once); <c>DELETE people/{id}</c> (disabled, its sessions and tokens ended; kept, so the audit still names it); <c>POST people/{id}/enable</c>.</item>
/// <item><c>GET groups</c> (with members); <c>POST groups</c> <c>{"name"}</c>; <c>DELETE groups/{id}</c>; <c>POST groups/{id}/members</c> <c>{"user"}</c>; <c>DELETE groups/{id}/members/{user}</c>.</item>
/// <item><c>GET assignments</c>; <c>POST assignments</c> <c>{"user"|"group", "server" [,"project"] | "app", "roles"}</c> (again replaces); <c>DELETE assignments/{id}</c>.</item>
/// <item><c>GET clients</c>; <c>POST clients</c> <c>{"name","redirect_uris","logout_redirect_uris","public"}</c> (a confidential client's secret is answered once); <c>DELETE clients/{id}</c>.</item>
/// <item><c>GET group-mappings</c>; <c>POST group-mappings</c> <c>{"source","group","server" [,"project"] | "app","roles"}</c>; <c>DELETE group-mappings/{id}</c>.</item>
/// <item><c>GET attribute-mappings</c>; <c>POST attribute-mappings</c> <c>{"source","group","attribute","value"}</c> (a group gives its members the value; the same again changes nothing; several mappings of one attribute give several values); <c>DELETE attribute-mappings/{id}</c> (plan 22a GP04).</item>
/// <item><c>GET tokens[?user=]</c> (never a secret); <c>DELETE tokens/{id}</c> (anyone's).</item>
/// <item><c>GET service-accounts</c>; <c>POST service-accounts</c> <c>{"name","description"}</c>; <c>DELETE service-accounts/{name}</c>; <c>POST service-accounts/{name}/tokens</c> <c>{"name","days"|"no_expiry"}</c> (answered once).</item>
/// </list>
/// </summary>
public sealed class IdentityAdminApi(StateDatabase state, string publicUrl, AccessTokenPolicy? tokenPolicy = null)
{
    public const string Prefix = "/api/v1/identity";

    private readonly IdentityUserStore _users = new(state);
    private readonly HomeDirectory _directory = new(state);
    private readonly OidcClients _clients = new(state);
    private AccessTokens Tokens => new(state, _users, tokenPolicy);

    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The path below <see cref="Prefix"/> (<c>people</c>, <c>assignments/abc</c>), with or without a query.</param>
    /// <param name="body">The request's JSON body, or null.</param>
    /// <param name="isAdmin">Whether the caller holds the admin role on this host. Every call needs it.</param>
    /// <param name="actor">Who is calling, for what they make (a token's or a service account's maker).</param>
    public IdentityAdminResult Handle(string method, string path, JsonNode? body, bool isAdmin, string actor)
    {
        if (!isAdmin) return IdentityAdminResult.Problem(403, "Administering the identity home needs the admin role.");
        var query = path.Contains('?') ? System.Web.HttpUtility.ParseQueryString(path[(path.IndexOf('?') + 1)..]) : new System.Collections.Specialized.NameValueCollection();
        var parts = path.Split('?')[0].Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        method = method.ToUpperInvariant();
        try
        {
            return (method, parts) switch
            {
                ("GET", ["people"]) => IdentityAdminResult.Ok(People()),
                ("POST", ["people"]) => Invite(body),
                ("DELETE", ["people", var id]) => Disable(id),
                ("POST", ["people", var id, "enable"]) => Enable(id),

                ("GET", ["groups"]) => IdentityAdminResult.Ok(Groups()),
                ("POST", ["groups"]) => CreateGroup(body),
                ("DELETE", ["groups", var id]) => _directory.RemoveGroup(id) ? IdentityAdminResult.NoContent() : NotFound("group"),
                ("POST", ["groups", var id, "members"]) => AddMember(id, body),
                ("DELETE", ["groups", var id, "members", var user]) => RemoveMember(id, user),

                ("GET", ["assignments"]) => IdentityAdminResult.Ok(Assignments()),
                ("POST", ["assignments"]) => Assign(body),
                ("DELETE", ["assignments", var id]) => _directory.RemoveAssignment(id) ? IdentityAdminResult.NoContent() : NotFound("assignment"),

                ("GET", ["clients"]) => IdentityAdminResult.Ok(Clients()),
                ("POST", ["clients"]) => RegisterClient(body),
                ("DELETE", ["clients", var id]) => _clients.Remove(id) ? IdentityAdminResult.NoContent() : NotFound("client"),

                ("GET", ["group-mappings"]) => IdentityAdminResult.Ok(Mappings()),
                ("POST", ["group-mappings"]) => Map(body),
                ("DELETE", ["group-mappings", var id]) => _directory.RemoveExternalMapping(id) ? IdentityAdminResult.NoContent() : NotFound("group mapping"),

                ("GET", ["attribute-mappings"]) => IdentityAdminResult.Ok(AttributeMappings()),
                ("POST", ["attribute-mappings"]) => MapAttribute(body),
                ("DELETE", ["attribute-mappings", var id]) => _directory.RemoveAttributeMapping(id) ? IdentityAdminResult.NoContent() : NotFound("attribute mapping"),

                ("GET", ["tokens"]) => IdentityAdminResult.Ok(TokenList(query["user"])),
                ("DELETE", ["tokens", var id]) => Tokens.Revoke(id) ? IdentityAdminResult.NoContent() : NotFound("token that is not already revoked"),

                ("GET", ["service-accounts"]) => IdentityAdminResult.Ok(ServiceAccounts()),
                ("POST", ["service-accounts"]) => CreateServiceAccount(body, actor),
                ("DELETE", ["service-accounts", var name]) => Tokens.RemoveServiceAccount(name) ? IdentityAdminResult.NoContent() : NotFound("service account"),
                ("POST", ["service-accounts", var name, "tokens"]) => CreateServiceToken(name, body, actor),

                _ => IdentityAdminResult.Problem(404, "There is no such call."),
            };
        }
        catch (Exception ex) when (ex is AccessTokenException or AccountConflictException or ArgumentException)
        {
            return IdentityAdminResult.Problem(400, ex.Message);
        }
    }

    private static IdentityAdminResult NotFound(string what) => IdentityAdminResult.Problem(404, $"There is no such {what}.");

    private static string? Text(JsonNode? body, string name) => body?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static bool Flag(JsonNode? body, string name) => body?[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);

    private static JsonArray RoleNames(ServerRole roles) => Strings(ServerRoles.Names(roles));

    /// <summary>The roles a body names (<c>"roles": ["operator"]</c> or <c>"operator,author"</c>); none is refused here, since removing is its own call.</summary>
    private static ServerRole Roles(JsonNode? body)
    {
        var names = body?["roles"] switch
        {
            JsonArray a => a.Select(n => n?.GetValue<string>() ?? "").ToArray(),
            JsonValue v when v.TryGetValue<string>(out var s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => [],
        };
        if (names.Length == 0 || !ServerRoles.TryParse(names, out var roles) || roles == ServerRole.None)
            throw new ArgumentException("Name the roles: viewer, operator, author, approver or admin (to take roles away, remove the assignment).");
        return roles;
    }

    // ---- people ----

    private JsonObject People()
    {
        var services = Tokens.ServiceAccounts().Select(a => a.UserId).ToHashSet(StringComparer.Ordinal);
        return new JsonObject
        {
            ["people"] = new JsonArray([.. _users.List().Where(u => !services.Contains(u.Id)).Select(u => (JsonNode)new JsonObject
            {
                ["id"] = u.Id, ["name"] = u.DisplayName, ["email"] = u.Email, ["email_verified"] = u.EmailVerified, ["enabled"] = u.Enabled,
                ["ways_in"] = Strings(_users.CredentialsOf(u.Id).Select(c => c.Method).Distinct()),
                ["groups"] = Strings(_directory.GroupsOf(u.Id).Select(g => g.Name)),
            })]),
        };
    }

    private IdentityAdminResult Invite(JsonNode? body)
    {
        if (Text(body, "email") is not { } email) return IdentityAdminResult.Problem(400, "An invitation is for an address: give \"email\".");
        if (!Enum.TryParse<UserRole>(Text(body, "role") ?? nameof(UserRole.Viewer), ignoreCase: true, out var role)) return IdentityAdminResult.Problem(400, "The role is admin or viewer.");
        var minted = new IdentityInviteStore(state).Create(role, null, createdByUserId: null, email: email);
        // the code goes in the fragment, which a browser never sends to a server and an access log never records
        return IdentityAdminResult.Ok(new JsonObject
        {
            ["url"] = $"{publicUrl.TrimEnd('/')}{IdentityHome.InvitePath}#{minted.Code}", ["expires"] = minted.Invite.ExpiresAtUtc.UtcDateTime.ToString("O"), ["role"] = role.ToString(),
        });
    }

    private IdentityAdminResult Disable(string id)
    {
        if (_users.Get(id) is null) return NotFound("person");
        _users.SetEnabled(id, false);
        new IdentitySessionStore(state).DeleteAllFor(id);
        foreach (var token in Tokens.List(id).Where(t => t.RevokedAtUtc is null)) Tokens.Revoke(token.Id);
        return IdentityAdminResult.NoContent();
    }

    private IdentityAdminResult Enable(string id)
    {
        if (_users.Get(id) is null) return NotFound("person");
        _users.SetEnabled(id, true);
        return IdentityAdminResult.NoContent();
    }

    // ---- groups ----

    private JsonObject Groups() => new()
    {
        ["groups"] = new JsonArray([.. _directory.Groups().Select(g => (JsonNode)new JsonObject { ["id"] = g.Id, ["name"] = g.Name, ["members"] = Strings(_directory.MembersOf(g.Id)) })]),
    };

    private IdentityAdminResult CreateGroup(JsonNode? body)
    {
        if (Text(body, "name") is not { } name) return IdentityAdminResult.Problem(400, "A group needs a name.");
        if (_directory.Groups().Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))) return IdentityAdminResult.Problem(409, $"There is already a group '{name}'.");
        var group = _directory.CreateGroup(name);
        return IdentityAdminResult.Ok(new JsonObject { ["id"] = group.Id, ["name"] = group.Name });
    }

    private IdentityAdminResult AddMember(string groupId, JsonNode? body)
    {
        if (_directory.Groups().All(g => g.Id != groupId)) return NotFound("group");
        if (Text(body, "user") is not { } user || _users.Get(user) is null) return IdentityAdminResult.Problem(400, "Name the person to add: \"user\" is their id.");
        if (_directory.MembersOf(groupId).Contains(user)) return IdentityAdminResult.NoContent();
        _directory.AddMember(groupId, user);
        return IdentityAdminResult.NoContent();
    }

    private IdentityAdminResult RemoveMember(string groupId, string user)
    {
        if (!_directory.MembersOf(groupId).Contains(user)) return NotFound("member of that group");
        _directory.RemoveMember(groupId, user);
        return IdentityAdminResult.NoContent();
    }

    // ---- role assignments ----

    private JsonObject Assignments() => new()
    {
        ["assignments"] = new JsonArray([.. _directory.Assignments().Select(a => (JsonNode)new JsonObject
        {
            ["id"] = a.Id, ["subject_kind"] = a.Subject.Kind, ["subject"] = a.Subject.Id, ["subject_name"] = SubjectName(a.Subject),
            ["scope"] = a.Scope, ["target"] = a.Target, ["project"] = a.Project, ["roles"] = RoleNames(a.Roles),
        })]),
    };

    private string SubjectName(AssignmentSubject subject) => subject.Kind == "user"
        ? _users.Get(subject.Id)?.DisplayName ?? subject.Id
        : _directory.Groups().FirstOrDefault(g => g.Id == subject.Id)?.Name ?? subject.Id;

    private IdentityAdminResult Assign(JsonNode? body)
    {
        AssignmentSubject subject;
        if (Text(body, "user") is { } user)
        {
            if (_users.Get(user) is null) return IdentityAdminResult.Problem(400, $"There is no person with the id '{user}'.");
            subject = AssignmentSubject.User(user);
        }
        else if (Text(body, "group") is { } group)
        {
            if (_directory.Groups().All(g => g.Id != group)) return IdentityAdminResult.Problem(400, $"There is no group with the id '{group}'.");
            subject = AssignmentSubject.Group(group);
        }
        else return IdentityAdminResult.Problem(400, "Name who gets the roles: \"user\" or \"group\" (an id).");

        var roles = Roles(body);
        if (Text(body, "app") is { } app) _directory.AssignApp(subject, app, roles);
        else if (Text(body, "server") is { } server) _directory.Assign(subject, server, roles, Text(body, "project"));
        else return IdentityAdminResult.Problem(400, "Name where: \"server\" (with \"project\" for a Build project) or \"app\".");
        return IdentityAdminResult.Ok(Assignments());
    }

    // ---- clients ----

    private JsonObject Clients() => new()
    {
        ["clients"] = new JsonArray([.. _clients.List().Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.ClientId, ["name"] = c.Name, ["redirect_uris"] = Strings(c.RedirectUris), ["logout_redirect_uris"] = Strings(c.PostLogoutRedirectUris ?? []), ["confidential"] = c.Confidential,
        })]),
    };

    private IdentityAdminResult RegisterClient(JsonNode? body)
    {
        static string[] List(JsonNode? node) => node switch
        {
            JsonArray a => [.. a.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0)],
            JsonValue v when v.TryGetValue<string>(out var s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => [],
        };
        if (Text(body, "name") is not { } name) return IdentityAdminResult.Problem(400, "A client needs a name.");
        var redirects = List(body?["redirect_uris"]);
        var (client, secret) = _clients.Register(name, redirects, confidential: !Flag(body, "public"), List(body?["logout_redirect_uris"]));
        return IdentityAdminResult.Ok(new JsonObject { ["id"] = client.ClientId, ["name"] = client.Name, ["confidential"] = client.Confidential, ["secret"] = secret });
    }

    // ---- external group mappings ----

    private JsonObject Mappings() => new()
    {
        ["group_mappings"] = new JsonArray([.. _directory.ExternalMappings().Select(m => (JsonNode)new JsonObject
        {
            ["id"] = m.Id, ["source"] = m.Source, ["group"] = m.Group, ["scope"] = m.Scope, ["target"] = m.Target, ["project"] = m.Project, ["roles"] = RoleNames(m.Roles),
        })]),
    };

    private IdentityAdminResult Map(JsonNode? body)
    {
        if (Text(body, "source") is not { } source) return IdentityAdminResult.Problem(400, "Name the source: entra or windows.");
        if (Text(body, "group") is not { } group) return IdentityAdminResult.Problem(400, "Name the external group (an Entra group id, a Windows group).");
        var roles = Roles(body);
        if (Text(body, "app") is { } app) _directory.MapExternalGroupToApp(source, group, app, roles);
        else if (Text(body, "server") is { } server) _directory.MapExternalGroup(source, group, server, roles, Text(body, "project"));
        else return IdentityAdminResult.Problem(400, "Name where: \"server\" (with \"project\" for a Build project) or \"app\".");
        return IdentityAdminResult.Ok(Mappings());
    }

    private JsonObject AttributeMappings() => new()
    {
        ["attribute_mappings"] = new JsonArray([.. _directory.AttributeMappings().Select(m => (JsonNode)new JsonObject
        {
            ["id"] = m.Id, ["source"] = m.Source, ["group"] = m.Group, ["attribute"] = m.Attribute, ["value"] = m.Value,
        })]),
    };

    private IdentityAdminResult MapAttribute(JsonNode? body)
    {
        if (Text(body, "source") is not { } source) return IdentityAdminResult.Problem(400, "Name the source: entra or windows.");
        if (Text(body, "group") is not { } group) return IdentityAdminResult.Problem(400, "Name the external group (an Entra group id, a Windows group).");
        if (Text(body, "attribute") is not { } attribute) return IdentityAdminResult.Problem(400, "Name the attribute (lower case letters, digits and underscores).");
        if (Text(body, "value") is not { } value) return IdentityAdminResult.Problem(400, "Give the value the group gives the attribute.");
        _directory.MapExternalGroupToAttribute(source, group, attribute, value);
        return IdentityAdminResult.Ok(AttributeMappings());
    }

    // ---- tokens and service accounts ----

    private JsonObject TokenList(string? user)
    {
        var now = DateTimeOffset.UtcNow;
        return new JsonObject
        {
            ["tokens"] = new JsonArray([.. Tokens.List(user).Select(t => (JsonNode)new JsonObject
            {
                ["id"] = t.Id, ["user"] = t.UserId, ["user_name"] = _users.Get(t.UserId)?.DisplayName, ["name"] = t.Name, ["created"] = t.CreatedAtUtc.UtcDateTime.ToString("O"),
                ["expires"] = t.ExpiresAtUtc?.UtcDateTime.ToString("O"), ["revoked"] = t.RevokedAtUtc?.UtcDateTime.ToString("O"), ["last_used"] = t.LastUsedAtUtc?.UtcDateTime.ToString("O"), ["active"] = t.IsActiveAt(now),
            })]),
        };
    }

    private JsonObject ServiceAccounts()
    {
        var now = DateTimeOffset.UtcNow;
        var tokens = Tokens;
        return new JsonObject
        {
            ["service_accounts"] = new JsonArray([.. tokens.ServiceAccounts().Select(a => (JsonNode)new JsonObject
            {
                ["id"] = a.UserId, ["name"] = a.Name, ["description"] = a.Description, ["created_by"] = a.CreatedBy, ["active_tokens"] = tokens.List(a.UserId).Count(t => t.IsActiveAt(now)),
            })]),
        };
    }

    private IdentityAdminResult CreateServiceAccount(JsonNode? body, string actor)
    {
        if (Text(body, "name") is not { } name) return IdentityAdminResult.Problem(400, "A service account needs a name.");
        var account = Tokens.CreateServiceAccount(name, Text(body, "description"), actor);
        return IdentityAdminResult.Ok(new JsonObject { ["id"] = account.UserId, ["name"] = account.Name });
    }

    private IdentityAdminResult CreateServiceToken(string accountName, JsonNode? body, string actor)
    {
        var tokens = Tokens;
        if (tokens.FindServiceAccount(accountName) is not { } account) return NotFound("service account");
        if (Text(body, "name") is not { } name) return IdentityAdminResult.Problem(400, "A token needs a name.");
        int? days = body?["days"] is JsonValue v && v.TryGetValue<int>(out var d) ? d : null;
        var made = tokens.Create(account.UserId, name, actor, days is { } n ? TimeSpan.FromDays(n) : null, noExpiry: Flag(body, "no_expiry"));
        return IdentityAdminResult.Ok(new JsonObject
        {
            ["id"] = made.Record.Id, ["token"] = made.Secret, ["user_name"] = account.Name, ["expires"] = made.Record.ExpiresAtUtc?.UtcDateTime.ToString("O"),
        });
    }
}
