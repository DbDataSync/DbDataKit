using System.Net.Sockets;
using System.Text;

namespace DbDataGateway.Server.Hosting;

/// <summary>
/// What a failed <c>server run</c> leaves for the administrator: a service has no console, and a configuration error used to go to its standard error only, so the Service Control Manager's
/// "Cannot start service" was all one saw. The cause (the exception chain, the account the process runs as, the data folder) is written to <c>start-failure.txt</c> in the data folder and,
/// for a Windows service, to the Application event log (source <c>.NET Runtime</c>, event 1000, which exists on every machine). A start that succeeds removes the file.
/// </summary>
public static class StartFailure
{
    public const string FileName = "start-failure.txt";

    /// <summary>The text of the record: when, who, where and why, with a hint for the causes a Windows workstation has and a server seldom does.</summary>
    public static string Describe(Exception error, string? dataDirectory, DateTimeOffset now, string account)
    {
        var text = new StringBuilder();
        text.AppendLine($"dbdatagateway {GatewayHost.Version} did not start at {now:yyyy-MM-dd HH:mm:ss zzz}.");
        text.AppendLine($"Account: {account}");
        text.AppendLine($"Data folder: {dataDirectory ?? "(not resolved)"}");
        for (var e = error; e is not null; e = e.InnerException) text.AppendLine($"{e.GetType().FullName}: {e.Message}");
        if (Find<SocketException>(error) is { SocketErrorCode: SocketError.AccessDenied or SocketError.AddressAlreadyInUse })
            text.AppendLine("A listening port could not be bound. Windows reserves ranges of ports (Hyper-V, WSL, Docker): see `netsh int ipv4 show excludedportrange protocol=tcp`, " +
                            "and `netstat -ano` for a process that holds it; move the gateway's ports in its server.yaml (listen, management) out of them.");
        text.AppendLine();
        text.AppendLine(error.ToString());
        return text.ToString();
    }

    /// <summary>Writes the record; never throws (a failure to record must not hide the failure itself).</summary>
    public static void Record(Exception error, string? dataDirectory)
    {
        string account;
        try { account = OperatingSystem.IsWindows() ? System.Security.Principal.WindowsIdentity.GetCurrent().Name : Environment.UserName; }
        catch (Exception) { account = Environment.UserName; }
        var text = Describe(error, dataDirectory, DateTimeOffset.Now, account);
        if (dataDirectory is not null)
        {
            // only into a folder that is there (the install made it): a command run by hand on a machine without one does not create it
            try { if (Directory.Exists(dataDirectory)) File.WriteAllText(Path.Combine(dataDirectory, FileName), text); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
        {
            try { System.Diagnostics.EventLog.WriteEntry(".NET Runtime", text.Length > 30000 ? text[..30000] : text, System.Diagnostics.EventLogEntryType.Error, 1000); }
            catch (Exception) { }
        }
    }

    /// <summary>Removes the record of an earlier failed start.</summary>
    public static void Clear(string? dataDirectory)
    {
        if (dataDirectory is null) return;
        try { File.Delete(Path.Combine(dataDirectory, FileName)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static T? Find<T>(Exception error) where T : Exception
    {
        for (var e = error; e is not null; e = e.InnerException) if (e is T found) return found;
        return error is AggregateException a ? a.InnerExceptions.Select(Find<T>).FirstOrDefault(f => f is not null) : null;
    }
}
