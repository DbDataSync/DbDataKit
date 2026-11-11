using DbDataKit.Config.Yaml;

namespace DbDataKit.Connections.Abstractions.Tests;

/// <summary>
/// Plan 22a GP18 fold (owner decision): the delivery channels are connections. <c>kind: smtp</c> (host, port, tls mode, sender, a login, a trusted certificate, a limit) and the webhook of a <c>kind: http</c> (<c>webhook:</c> section, <c>endpoint_secret</c>, the
/// <c>signed_webhook</c> sign-in) are additive keys of <c>connection/2</c>: strict, secrets as references, and a file that uses them is refused by a program that does not keep outside kinds.
/// </summary>
public sealed class ConnectionDeliveryKindsTests
{
    private const string Smtp = "schema: connection/2\nkind: smtp\nname: mail\nsmtp:\n  host: smtp.example.com\n  sender: \"DbData <noreply@example.com>\"\nsign_in: { method: basic, user: robot, password_secret: secret:dbdatakit:server:connection:mail:password }\n";
    private const string Slack = "schema: connection/2\nkind: http\nname: slack-ops\nendpoint: https://hooks.slack.com/services/{token}\nendpoint_secrets:\n  token: secret:dbdatakit:server:connection:slack-ops:token\nwebhook: { format: slack }\n";
    private const string Head = "schema: connection/2\nkind: http\nname: w\n";

    private static ConnectionLoadResult Any(string yaml, string file = "connections/x.yml") => ConnectionDefinitionLoader.Load(yaml, file, outsideKinds: true);

    private static string Errors(ConnectionLoadResult r) => string.Join("\n", r.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error));

    [Fact]
    public void A_smtp_connection_loads_with_its_section_and_its_login()
    {
        var r = Any(Smtp);
        Assert.True(r.Ok, Errors(r));
        var d = r.Definition!;
        Assert.Equal(ConnectionKinds.Smtp, d.KindName);
        Assert.True(d.IsOutside);
        Assert.Equal("smtp.example.com", d.Smtp!.Host);
        Assert.Equal("starttls", d.Smtp.Tls);
        Assert.Equal(30, d.Smtp.TimeoutSeconds);
        Assert.Equal("basic", d.SignIn!.Method);
    }

    [Fact]
    public void A_program_that_keeps_database_connections_refuses_a_smtp_file_naming_it()
    {
        var r = ConnectionDefinitionLoader.Load(Smtp, "connections/mail.yml");
        Assert.False(r.Ok);
        Assert.Contains(r.Diagnostics, d => d.Code == ConnectionDefinitionLoader.KindNotKept && d.Message.Contains("connections/mail.yml", StringComparison.Ordinal));
    }

    [Fact]
    public void A_connection_1_file_cannot_say_smtp()
    {
        var r = Any(Smtp.Replace("connection/2", "connection/1", StringComparison.Ordinal));
        Assert.False(r.Ok);
        Assert.Contains(r.Diagnostics, d => d.Code == ConnectionDefinitionLoader.SectionNotInThisFormat);
    }

    [Theory]
    [InlineData("smtp:\n  host: localhost\n  tls: none\n  sender: a@example.com\n", true)]
    [InlineData("smtp:\n  host: 127.0.0.1\n  tls: none\n  sender: a@example.com\n", true)]
    [InlineData("smtp:\n  host: '[::1]'\n  tls: none\n  sender: a@example.com\n", true)]
    [InlineData("smtp:\n  host: relay.example.com\n  tls: none\n  sender: a@example.com\n", false)]
    [InlineData("smtp:\n  host: relay.example.com\n  tls: none\n  allow_plain_text: true\n  sender: a@example.com\n", true)]
    [InlineData("smtp:\n  host: relay.example.com\n  tls: implicit\n  allow_plain_text: true\n  sender: a@example.com\n", false)]
    [InlineData("smtp:\n  host: relay.example.com\n  tls: sometimes\n  sender: a@example.com\n", false)]
    public void Tls_none_is_refused_unless_the_host_is_this_machine_or_it_is_said_on_purpose(string section, bool ok)
    {
        var r = Any($"schema: connection/2\nkind: smtp\nname: m\n{section}");
        Assert.Equal(ok, r.Ok);
        if (!ok) Assert.NotEmpty(Errors(r));
    }

    [Theory]
    [InlineData("smtp:\n  host: smtp.example.com\n", "sender")]
    [InlineData("smtp:\n  host: smtp.example.com\n  sender: not-an-address\n", "smtp.sender")]
    [InlineData("smtp:\n  host: smtp.example.com\n  sender: a@example.com\n  password: hunter2\n", "password")]
    [InlineData("smtp:\n  host: smtp.example.com\n  sender: a@example.com\nendpoint: https://x.example.com\n", "does not belong")]
    [InlineData("smtp:\n  host: smtp.example.com\n  sender: a@example.com\nsign_in: { method: api_key, key_secret: env:K }\n", "basic")]
    [InlineData("smtp:\n  host: smtp.example.com\n  sender: a@example.com\nsign_in: { method: basic, user: u, password_secret: hunter2 }\n", "secret reference")]
    public void A_smtp_connection_is_strict(string body, string message)
    {
        var r = Any("schema: connection/2\nkind: smtp\nname: m\n" + body);
        Assert.False(r.Ok);
        Assert.Contains(message, string.Join("\n", r.Diagnostics.Select(d => d.Message + " " + d.Fix)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_smtp_section_is_required_and_belongs_to_the_kind()
    {
        var none = Any("schema: connection/2\nkind: smtp\nname: m\n");
        Assert.Contains("needs an `smtp` section", Errors(none), StringComparison.Ordinal);
        var onHttp = Any("schema: connection/2\nkind: http\nname: m\nendpoint: https://a.example.com\nsmtp: { host: h, sender: a@example.com }\n");
        Assert.False(onHttp.Ok);
        var onDatabase = ConnectionDefinitionLoader.Load("schema: connection/2\nname: w\nengine: postgres\naddress: { host: h }\nsmtp: { host: h, sender: a@example.com }\n");
        Assert.Contains(onDatabase.Diagnostics, d => d.Code == ConnectionDefinitionLoader.BadOutside);
    }

    [Fact]
    public void A_webhook_connection_keeps_a_template_and_the_references_of_its_secret_parts()
    {
        var r = Any(Slack);
        Assert.True(r.Ok, Errors(r));
        Assert.Equal("https://hooks.slack.com/services/{token}", r.Definition!.Endpoint);
        Assert.StartsWith("secret:", r.Definition.EndpointSecrets["token"], StringComparison.Ordinal);
        Assert.Equal("slack", r.Definition.Webhook!.Format);
        var teams = Any(Head + "endpoint: https://prod-12.westus.logic.azure.com:443/workflows/{flow}/triggers/manual/paths/invoke?api-version=2016-06-01&sig={sig}\nendpoint_secrets: { flow: env:FLOW, sig: env:SIG }\nwebhook: { format: teams }\n");
        Assert.True(teams.Ok, Errors(teams));
    }

    [Theory]
    [InlineData("https://hooks.slack.com/services/T000/B000/CANARYTOKEN123", "Slack webhook")]
    [InlineData("https://discord.com/api/webhooks/1234567890/CANARYTOKEN123", "Discord webhook")]
    [InlineData("https://prod-12.westus.logic.azure.com:443/workflows/{flow}/triggers/manual/paths/invoke?api-version=2016-06-01&sig=CANARYTOKEN123", "`sig`")]
    [InlineData("https://contoso.webhook.office.com/webhookb2/CANARYTOKEN123@tenant/IncomingWebhook/x/y", "Teams webhook")]
    [InlineData("https://api.example.com/v1/hooks/Zk3Jd9Qx7Lm2Pa8Rt5Vw1Yb4Nc6", "looks like a secret")]
    [InlineData("https://api.example.com/v1?token=CANARYTOKEN123", "`token`")]
    [InlineData("https://api.example.com/v1?key=CANARYTOKEN123", "`key`")]
    [InlineData("https://api.example.com/v1?secret=CANARYTOKEN123", "`secret`")]
    [InlineData("https://api.example.com/v1?api_key=CANARYTOKEN123", "`api_key`")]
    [InlineData("https://api.example.com/v1?apikey=CANARYTOKEN123", "`apikey`")]
    [InlineData("https://api.example.com/v1?code=CANARYTOKEN123", "`code`")]
    [InlineData("https://api.example.com/v1?signature=CANARYTOKEN123", "`signature`")]
    [InlineData("https://user:pass@api.example.com/v1", "user name or password")]
    [InlineData("ftp://api.example.com/v1", "http or https")]
    public void A_secret_in_plain_sight_is_refused_naming_the_part_without_repeating_it(string endpoint, string part)
    {
        var r = Any(Head + $"endpoint: \"{endpoint}\"\n");
        Assert.False(r.Ok);
        var words = string.Join("\n", r.Diagnostics.Select(d => d.Message + " " + d.Fix));
        Assert.Contains(part, words, StringComparison.Ordinal);
        Assert.DoesNotContain("CANARYTOKEN123", string.Join("\n", r.Diagnostics.Select(d => d.ToString() + d.Message + d.Fix)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://api.example.com/v1/accounts/list")]
    [InlineData("https://api.example.com/v1?page=2&api-version=2016-06-01")]
    [InlineData("https://api.example.com/v1/this-is-a-long-readable-slug-without-digits")]
    [InlineData("https://hooks.slack.com/services/{token}")]
    [InlineData("https://hooks.example.com/{a}/x?sig={sig}")]
    public void An_address_with_no_secret_in_it_is_accepted(string endpoint)
    {
        var yaml = Head + $"endpoint: \"{endpoint}\"\n" + (endpoint.Contains("{token}", StringComparison.Ordinal) ? "endpoint_secrets: { token: env:T }\n" : endpoint.Contains("{a}", StringComparison.Ordinal) ? "endpoint_secrets: { a: env:A, sig: env:S }\n" : "");
        var r = Any(yaml);
        Assert.True(r.Ok, Errors(r));
    }

    [Theory]
    [InlineData("endpoint: \"https://hooks.slack.com/services/{token}\"\n", "not bound")]
    [InlineData("endpoint: \"https://hooks.slack.com/services/{token}\"\nendpoint_secrets: { token: env:T, other: env:O }\n", "has no `{other}`")]
    [InlineData("endpoint: \"https://{region}.example.com/hook/{t}\"\nendpoint_secrets: { region: env:R, t: env:T }\n", "scheme, host or port")]
    [InlineData("endpoint: \"https://example.com:{port}/hook\"\nendpoint_secrets: { port: env:P }\n", "scheme, host or port")]
    [InlineData("endpoint: \"https://hooks.slack.com/services/{token}\"\nendpoint_secrets: { token: hunter2 }\n", "secret reference")]
    [InlineData("endpoint_secrets: { token: env:T }\n", "needs an `endpoint`")]
    [InlineData("endpoint: secret:dbdatakit:server:connection:w:url\nendpoint_secrets: { token: env:T }\n", "no placeholder to bind")]
    public void Every_placeholder_is_named_bound_and_used_and_never_in_the_origin(string body, string message)
    {
        var r = Any(Head + body);
        Assert.False(r.Ok);
        Assert.Contains(message, string.Join("\n", r.Diagnostics.Select(d => d.Message + " " + d.Fix)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_whole_address_secret_is_possible_and_marked_hidden()
    {
        var r = Any(Head + "endpoint: secret:dbdatakit:server:connection:w:url\nwebhook: { format: slack }\n");
        Assert.True(r.Ok, Errors(r));
        Assert.Contains(r.Diagnostics, d => d.Code == ConnectionDefinitionLoader.AddressHidden && d.Severity == ConfigSeverity.Warning);
        Assert.Equal(EndpointTemplate.Hidden, EndpointTemplate.Display(r.Definition!.Endpoint));
        Assert.Equal("https://hooks.slack.com/services/{token}", EndpointTemplate.Display("https://hooks.slack.com/services/{token}"));
    }

    [Fact]
    public void A_webhook_format_that_is_not_the_one_its_host_takes_is_warned_about()
    {
        var r = Any(Slack.Replace("format: slack", "format: teams", StringComparison.Ordinal));
        Assert.Contains(r.Diagnostics, d => d.Code == ConnectionDefinitionLoader.WebhookFormatMismatch && d.Severity == ConfigSeverity.Warning);
        Assert.DoesNotContain(Any(Slack).Diagnostics, d => d.Code == ConnectionDefinitionLoader.WebhookFormatMismatch);
    }

    [Theory]
    [InlineData("webhook: { format: slack }\nsign_in: { method: api_key, key_secret: env:K }\n", "takes no sign-in")]
    [InlineData("webhook: { format: signed }\n", "signed_webhook")]
    [InlineData("webhook: { format: fax }\n", "format")]
    [InlineData("webhook: { format: slack, allow_private: [ 10.0.0.0/40 ] }\n", "not a range")]
    [InlineData("webhook: { format: slack, allow_private: [ 'a b' ] }\n", "no spaces")]
    [InlineData("sign_in: { method: signed_webhook, key_secret: env:K }\n", "webhook")]
    public void A_webhook_is_strict(string body, string message)
    {
        var r = Any(Head + "endpoint: https://hook.example.com/in\n" + body);
        Assert.False(r.Ok);
        Assert.Contains(message, string.Join("\n", r.Diagnostics.Select(d => d.Message + " " + d.Fix)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_signed_webhook_needs_its_secret_and_takes_a_header_name_and_the_private_allow_list()
    {
        var ok = Any(Head + "endpoint: https://erp.internal.example/hook/{id}\nendpoint_secrets: { id: env:ID }\nwebhook: { format: signed, allow_private: [ erp.internal.example, 10.1.0.0/16 ], limit_per_minute: 20 }\nsign_in: { method: signed_webhook, key_secret: env:SIGNING, name: X-My-Signature }\n");
        Assert.True(ok.Ok, Errors(ok));
        Assert.Equal(["erp.internal.example", "10.1.0.0/16"], ok.Definition!.Webhook!.AllowPrivate);
        Assert.Equal(20, ok.Definition.Webhook.LimitPerMinute);
        var noKey = Any(Head + "endpoint: https://erp.example.com/hook\nwebhook: { format: signed }\nsign_in: { method: signed_webhook }\n");
        Assert.Contains("needs `key_secret`", Errors(noKey), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_key_of_the_new_sections_is_refused_with_a_suggestion()
    {
        var r = Any("schema: connection/2\nkind: smtp\nname: m\nsmtp: { host: h, sender: a@example.com, tlss: starttls }\n");
        Assert.False(r.Ok);
        Assert.Contains("tlss", Errors(r), StringComparison.Ordinal);
    }

    [Fact]
    public void The_registry_splits_a_pasted_address_into_a_template_and_its_secret_parts()
    {
        var slack = WebhookPatterns.Detect("https://hooks.slack.com/services/T000/B000/XXXXXXXX")!;
        Assert.Equal(("Slack", "slack", "https://hooks.slack.com/services/{token}"), (slack.Service, slack.Format, slack.Template));
        Assert.Equal("T000/B000/XXXXXXXX", slack.Parts["token"]);

        var discord = WebhookPatterns.Detect("https://discord.com/api/webhooks/123/abcDEF/slack")!;
        Assert.Equal(("Discord", "slack", "https://discord.com/api/webhooks/{token}/slack"), (discord.Service, discord.Format, discord.Template));
        Assert.Equal("123/abcDEF", discord.Parts["token"]);
        Assert.Null(WebhookPatterns.Detect("https://discord.com/api/webhooks/123/abcDEF")!.Format);

        var teams = WebhookPatterns.Detect("https://prod-12.westus.logic.azure.com:443/workflows/0a1b2c3d4e5f/triggers/manual/paths/invoke?api-version=2016-06-01&sp=%2Ftriggers&sig=SIG_value-9")!;
        Assert.Equal("teams", teams.Format);
        Assert.Equal("https://prod-12.westus.logic.azure.com:443/workflows/{flow}/triggers/manual/paths/invoke?api-version=2016-06-01&sp=%2Ftriggers&sig={sig}", teams.Template);
        Assert.Equal(["flow", "sig"], teams.Parts.Keys.Order());

        var legacy = WebhookPatterns.Detect("https://contoso.webhook.office.com/webhookb2/aaa@bbb/IncomingWebhook/ccc/ddd")!;
        Assert.Equal(("Teams (connector)", "https://contoso.webhook.office.com/webhookb2/{token}"), (legacy.Service, legacy.Template));
        Assert.Equal("aaa@bbb/IncomingWebhook/ccc/ddd", legacy.Parts["token"]);

        Assert.Null(WebhookPatterns.Detect("https://api.example.com/hook/abc"));
        Assert.Null(WebhookPatterns.Detect("not a url"));
    }

    [Theory]
    [InlineData("https://hooks.slack.com/services/T000/B000/XXXXXXXX")]
    [InlineData("https://discord.com/api/webhooks/123/abcDEF/slack")]
    [InlineData("https://prod-12.westus.logic.azure.com:443/workflows/0a1b2c3d4e5f/triggers/manual/paths/invoke?api-version=2016-06-01&sig=SIG_value-9")]
    [InlineData("https://contoso.webhook.office.com/webhookb2/aaa@bbb/IncomingWebhook/ccc/ddd")]
    public void A_detected_template_composes_back_to_the_address_and_the_template_itself_loads(string url)
    {
        var found = WebhookPatterns.Detect(url)!;
        Assert.Equal(url, EndpointTemplate.Compose(found.Template, n => found.Parts[n]));
        var bindings = string.Join(", ", found.Parts.Keys.Select(k => $"{k}: env:{k.ToUpperInvariant()}"));
        var r = Any(Head + $"endpoint: \"{found.Template}\"\nendpoint_secrets: {{ {bindings} }}\nwebhook: {{ format: {found.Format ?? "signed"} }}\n" + (found.Format is null ? "sign_in: { method: signed_webhook, key_secret: env:S }\n" : ""));
        Assert.True(r.Ok, Errors(r));
    }
}
