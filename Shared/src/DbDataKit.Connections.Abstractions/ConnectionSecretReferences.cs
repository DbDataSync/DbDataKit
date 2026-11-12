namespace DbDataKit.Connections;

/// <summary>Every secret reference a connection definition holds, as (the path in the file, the reference): a database login, an outside system's sign-in, the secret parts of an address. Never a value: a definition has none.</summary>
public static class ConnectionSecretReferences
{
    public static IReadOnlyList<(string Path, string Reference)> Of(ConnectionDefinition d)
    {
        var list = new List<(string, string)>();
        void Add(string path, string? reference)
        {
            if (!string.IsNullOrEmpty(reference)) list.Add((path, reference));
        }

        void Login(string at, LoginSpec? login)
        {
            if (login is null) return;
            Add(at + ".password_secret", login.PasswordSecret);
            Add(at + ".connection_string_secret", login.ConnectionStringSecret);
            Add(at + ".token_secret", login.TokenSecret);
        }

        Add("connection_string_secret", d.ConnectionStringSecret);
        Login("auth", d.Auth);
        Login("read_login", d.ReadLogin);
        Login("write_login", d.WriteLogin);
        if (d.Endpoint is { } endpoint && EndpointTemplate.IsHidden(endpoint)) Add("endpoint", endpoint);
        foreach (var (part, reference) in d.EndpointSecrets) Add("endpoint_secrets." + part, reference);
        if (d.SignIn is { } s)
        {
            Add("sign_in.key_secret", s.KeySecret);
            Add("sign_in.password_secret", s.PasswordSecret);
            Add("sign_in.client_secret_secret", s.ClientSecretSecret);
            Add("sign_in.certificate_secret", s.CertificateSecret);
            Add("sign_in.certificate_password_secret", s.CertificatePasswordSecret);
            Add("sign_in.refresh_token_secret", s.RefreshTokenSecret);
        }

        return list;
    }
}
