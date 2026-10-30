namespace DbDataSync.Api.Management;

/// <summary>
/// What an address of this server that is not the API says (plan 13, U11): the server no longer serves a web app, its screens are the console's. A person who opens the server's address, or a bookmark of one of its old screens, is told
/// that, and where the console is when this server was told (<see cref="ConfigurationKey"/>, or the variable <see cref="EnvironmentVariable"/>), and otherwise how to tell it. Never a blank 404.
/// </summary>
public static class ConsoleNotice
{
    public const string ConfigurationKey = "DbDataSync:Console:Url";
    public const string EnvironmentVariable = "DBDATAKIT_CONSOLE_URL";

    /// <summary>The console's address as configured, or null when none was given or it is not an http or https address.</summary>
    public static Uri? Address(string? configured)
    {
        var text = string.IsNullOrWhiteSpace(configured) ? Environment.GetEnvironmentVariable(EnvironmentVariable) : configured;
        return Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
    }

    public static string Message(Uri? console) => console is null
        ? "DbDataSync no longer serves a web app: its screens are in the dbdatakit console. Open your console and pair this server with it (dbdatasync server pair, then the Servers page). "
          + $"To have this address point there, set {ConfigurationKey} (or the environment variable {EnvironmentVariable}) to the console's address, for example https://console.example:8480."
        : $"DbDataSync no longer serves a web app: its screens are in the dbdatakit console, at {console.AbsoluteUri}. Open the console, find this server on its Servers page, and open it there. (This server's own address answers the API and the console's calls only.)";
}
