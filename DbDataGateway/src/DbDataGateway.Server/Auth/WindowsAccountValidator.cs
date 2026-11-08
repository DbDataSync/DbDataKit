using System.Runtime.InteropServices;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Serving;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

namespace DbDataGateway.Server.Auth;

/// <summary>Checks a Windows username/password (local or domain account) with the operating system.</summary>
public interface IWindowsLogon
{
    /// <summary>False when this platform cannot check Windows accounts at all.</summary>
    bool IsAvailable => true;

    /// <param name="domain">The domain, or null when <paramref name="user"/> is a UPN (<c>ada@corp.example</c>), which names its own.</param>
    bool Validate(string? domain, string user, string password);
}

/// <summary>LogonUser-based implementation (Windows only).</summary>
public sealed partial class NativeWindowsLogon : IWindowsLogon
{
    private const int LogonNetwork = 3;
    private const int ProviderDefault = 0;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public bool Validate(string? domain, string user, string password)
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
/// Windows sign-in resolved to the identity home (plan 13a P13, U16): the user name is the person's UPN (<c>ada@corp.example</c>), the password is checked by the operating system, and the account is the home user who holds that verified address.
/// Only with <c>Gateway:Auth:Windows</c>. A UPN that no home user holds is not this validator's (it may be a database login for pass-through); a home user's UPN with a password the operating system refuses is refused for good, and on a platform that
/// cannot check Windows accounts it is refused too. The data sources are the home groups' (<see cref="DataSourceOptions.Groups"/>), as for every home user: a Windows account needs no database login of its own.
/// </summary>
public sealed class WindowsAccountValidator(IOptionsMonitor<GatewayOptions> options, IWindowsLogon logon, HomeCallers callers, ILogger<WindowsAccountValidator> log)
    : IBasicCredentialValidator
{
    public async ValueTask<CredentialResult> ValidateAsync(string user, string password, CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.Auth.Windows || HomePrincipals.LooksLikeToken(password) || !IsUpn(user)) return CredentialResult.NotMine;

        var home = await callers.GetAsync(cancellationToken).ConfigureAwait(false);
        if (home.FindByAddress(user.Trim()) is not { } person) return CredentialResult.NotMine;

        if (!logon.IsAvailable)
        {
            log.LogWarning("{User} is a user of the identity home and signed in with a Windows password, which this platform cannot check.", user);
            return CredentialResult.Rejected;
        }
        bool valid = await Task.Run(() => logon.Validate(null, user.Trim(), password), cancellationToken).ConfigureAwait(false);
        return valid ? CredentialResult.Accepted(HomePrincipals.Create(home, person, "windows", options.CurrentValue)) : CredentialResult.Rejected;
    }

    /// <summary>A UPN is <c>name@domain</c>; <c>DOMAIN\name</c> and a bare name are not resolved to a home user (they have no address to look up).</summary>
    public static bool IsUpn(string name)
    {
        int at = name.IndexOf('@');
        return at > 0 && at < name.Length - 1 && name.IndexOf((char)92) < 0 && name.IndexOf('@', at + 1) < 0;
    }
}
