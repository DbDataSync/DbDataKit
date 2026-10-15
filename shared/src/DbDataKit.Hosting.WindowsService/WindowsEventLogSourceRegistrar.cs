using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DbDataKit.Hosting.WindowsService;

/// <summary>
/// Registers an Application-log event source by writing the registry keys directly (needs admin), pointing at the
/// framework's generic message DLL, so no extra package is needed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsEventLogSourceRegistrar : IEventLogSourceRegistrar
{
    private const string Root = @"SYSTEM\CurrentControlSet\Services\EventLog\Application";
    private const string MessageFile = @"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll";

    public bool Exists(string source)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{Root}\{Validate(source)}");
        return key is not null;
    }

    public void Register(string source)
    {
        using var key = Registry.LocalMachine.CreateSubKey($@"{Root}\{Validate(source)}", writable: true);
        key.SetValue("EventMessageFile", MessageFile, RegistryValueKind.ExpandString);
        key.SetValue("TypesSupported", 7, RegistryValueKind.DWord);
    }

    public void Unregister(string source)
    {
        Registry.LocalMachine.DeleteSubKeyTree($@"{Root}\{Validate(source)}", throwOnMissingSubKey: false);
    }

    private static string Validate(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.IndexOfAny(['\\', '/']) >= 0)
        {
            throw new ArgumentException("Invalid event source name.", nameof(source));
        }

        return source;
    }
}
