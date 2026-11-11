using DbDataKit.Config.Yaml;

namespace DbDataKit.Connections.Abstractions.Tests;

/// <summary>
/// Plan 22a GP18 fold (owner decision): the delivery channels are connections. <c>kind: smtp</c> (host, port, tls mode, sender, a login, a trusted certificate, a limit) and the webhook of a <c>kind: http</c> (<c>webhook:</c> section, <c>endpoint_secret</c>, the
/// <c>signed_webhook</c> sign-in) are additive keys of <c>connection/2</c>: strict, secrets as references, and a file that uses them is refused by a program that does not keep outside kinds.
/// </summary>
public sealed class ConnectionDeliveryKindsTests
{
    private const string Smtp = "schema: connection/2\nkind: smtp\nname: mail\nsmtp:\n  host: smtp.example.com\n  sender: \"DbData <noreply@example.com>\"\nsign_in: { method: basic, user: robot, password_secret: secret:dbdatakit:server:connection:mail:password }\n";
    private const string Slack = "schema: connection/2\nkind: http\nname: slack-ops\nendpoint_secret: secret:dbdatakit:server:connection:slack-ops:endpoint\nwebhook: { format: slack }\n";

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
    public void A_webhook_connection_keeps_its_address_as_a_reference()
    {
        var r = Any(Slack);
        Assert.True(r.Ok, Errors(r));
        Assert.Null(r.Definition!.Endpoint);
        Assert.StartsWith("secret:", r.Definition.EndpointSecret, StringComparison.Ordinal);
        Assert.Equal("slack", r.Definition.Webhook!.Format);
    }

    [Fact]
    public void A_webhook_address_written_out_is_refused_without_being_echoed()
    {
        const string url = "https://hooks.slack.com/services/T000/B000/CANARYTOKEN123";
        var asEndpointSecret = Any($"schema: connection/2\nkind: http\nname: s\nendpoint_secret: {url}\nwebhook: {{ format: slack }}\n");
        Assert.False(asEndpointSecret.Ok);
        Assert.Contains(asEndpointSecret.Diagnostics, d => d.Code == ConnectionDefinitionLoader.BadSecretRef);
        Assert.DoesNotContain("CANARYTOKEN123", string.Join("\n", asEndpointSecret.Diagnostics.Select(d => d.ToString() + d.Message + d.Fix)), StringComparison.Ordinal);
        // a token-bearing path is also not an `endpoint` of a webhook that has a query; and endpoint_secret needs the webhook section
        Assert.False(Any("schema: connection/2\nkind: http\nname: s\nendpoint_secret: env:URL\n").Ok);
        Assert.False(Any("schema: connection/2\nkind: http\nname: s\nendpoint: https://a.example.com\nendpoint_secret: env:URL\nwebhook: { format: slack }\n").Ok);
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
        var r = Any("schema: connection/2\nkind: http\nname: w\nendpoint_secret: env:URL\n" + body);
        Assert.False(r.Ok);
        Assert.Contains(message, string.Join("\n", r.Diagnostics.Select(d => d.Message + " " + d.Fix)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_signed_webhook_needs_its_secret_and_takes_a_header_name_and_the_private_allow_list()
    {
        var ok = Any("schema: connection/2\nkind: http\nname: erp\nendpoint_secret: env:URL\nwebhook: { format: signed, allow_private: [ erp.internal.example, 10.1.0.0/16 ], limit_per_minute: 20 }\nsign_in: { method: signed_webhook, key_secret: env:SIGNING, name: X-My-Signature }\n");
        Assert.True(ok.Ok, Errors(ok));
        Assert.Equal(["erp.internal.example", "10.1.0.0/16"], ok.Definition!.Webhook!.AllowPrivate);
        Assert.Equal(20, ok.Definition.Webhook.LimitPerMinute);
        var noKey = Any("schema: connection/2\nkind: http\nname: erp\nendpoint_secret: env:URL\nwebhook: { format: signed }\nsign_in: { method: signed_webhook }\n");
        Assert.Contains("needs `key_secret`", Errors(noKey), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_key_of_the_new_sections_is_refused_with_a_suggestion()
    {
        var r = Any("schema: connection/2\nkind: smtp\nname: m\nsmtp: { host: h, sender: a@example.com, tlss: starttls }\n");
        Assert.False(r.Ok);
        Assert.Contains("tlss", Errors(r), StringComparison.Ordinal);
    }
}
