using System.Runtime.InteropServices;
using DbDataProxy.Server.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

namespace DbDataProxy.Server.Auth;

/// <summary>Checks a Windows username/password (local or domain account) with the operating system.</summary>
public interface IWindowsLogon
{
    bool Validate(string domain, string user, string password);
}

/// <summary>LogonUser-based implementation (Windows only).</summary>
public sealed partial class NativeWindowsLogon : IWindowsLogon
{
    private const int LogonNetwork = 3;
    private const int ProviderDefault = 0;

    public bool Validate(string domain, string user, string password)
    {
        if (!OperatingSystem.IsWindows()) return false;
        // A network logon needs no "log on locally" right and does not create an interactive session.
        bool ok = LogonUser(user, domain, password, LogonNetwork, ProviderDefault, out var token);
        token.Dispose();
        return ok;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "LogonUserW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LogonUser(string user, string? domain, string password, int logonType, int provider, out SafeAccessTokenHandle token);
}

/// <summary>
/// Authenticates configured users whose <see cref="UserOptions.AuthType"/> is Windows against the machine's
/// (or domain's) account database. The configured user then maps to data sources whose connection strings carry
/// the database credentials, so Windows accounts never need database logins of their own.
/// </summary>
public sealed class WindowsAccountValidator(IOptionsMonitor<ProxyOptions> options, IWindowsLogon logon, ILogger<WindowsAccountValidator> log)
    : IBasicCredentialValidator
{
    public async ValueTask<CredentialResult> ValidateAsync(string user, string password, CancellationToken cancellationToken)
    {
        var entry = options.CurrentValue.Users.FirstOrDefault(u =>
            u.AuthType == UserAuthType.Windows && string.Equals(u.Name, user, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return CredentialResult.NotMine;

        if (!OperatingSystem.IsWindows())
        {
            log.LogWarning("User {User} is configured for Windows authentication, which is only available on Windows.", user);
            return CredentialResult.Rejected;
        }

        var (domain, account) = Split(user);
        bool valid = await Task.Run(() => logon.Validate(domain, account, password), cancellationToken).ConfigureAwait(false);
        return valid
            ? CredentialResult.Accepted(new ProxyPrincipal(entry.Name, "windows", entry.DataSources, entry.DefaultDataSource))
            : CredentialResult.Rejected;
    }

    /// <summary>"DOMAIN\user" and "user@domain" are split; a bare name (or ".\user") is a local account.</summary>
    public static (string Domain, string User) Split(string name)
    {
        int slash = name.IndexOf((char)92);
        if (slash > 0) return (name[..slash], name[(slash + 1)..]);
        int at = name.IndexOf('@');
        if (at > 0) return (name[(at + 1)..], name[..at]);
        return (".", name);
    }
}
