namespace DbDataKit.Hosting.Local;

/// <summary>Where a server's local channel is and who may reach it.</summary>
/// <param name="Name">The server's name (<c>dbdataproxy</c>, <c>dbdatasync</c>): one channel per name per machine.</param>
public sealed record LocalChannelOptions(string Name)
{
    /// <summary>
    /// Unix only: the folder that holds the socket, created for the server's user alone (mode 0700, or 0750 with <see cref="GroupAccess"/>). Default: <c>/run/dbdata/&lt;name&gt;</c> for root,
    /// otherwise <c>$XDG_RUNTIME_DIR/dbdata/&lt;name&gt;</c>, otherwise <c>/tmp/dbdata-&lt;user&gt;/&lt;name&gt;</c>.
    /// </summary>
    public string? Directory { get; init; }

    /// <summary>
    /// Unix only: also let the server's own group in (the socket is 0660 and the folder 0750; the socket belongs to the process's group, so run the service with the group you mean).
    /// Off by default: only the server's user and root.
    /// </summary>
    public bool GroupAccess { get; init; }

    /// <summary>Windows only: who may connect to the pipe. Default: SYSTEM and Administrators (the proof of ownership the pairing commands rely on).</summary>
    public IReadOnlyList<string> WindowsIdentities { get; init; } = ["SYSTEM", "Administrators"];

    /// <summary>The longest message either side will accept.</summary>
    public int MaxMessageBytes { get; init; } = 1024 * 1024;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) || Name.Contains(".."))
            throw new ArgumentException($"'{Name}' is not a channel name (letters, digits, '-', '_' and '.').", nameof(Name));
        if (MaxMessageBytes is < 1 or > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaxMessageBytes));
    }

    /// <summary>The pipe name on Windows, or the socket path on Unix.</summary>
    public string Endpoint
    {
        get
        {
            Validate();
            if (OperatingSystem.IsWindows()) return "dbdata-" + Name;
            return Path.Combine(SocketDirectory, "channel.sock");
        }
    }

    internal string SocketDirectory
    {
        get
        {
            if (Directory is not null) return Path.GetFullPath(Directory);
            var user = Environment.UserName;
            if (user == "root") return Path.Combine("/run/dbdata", Name);
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return !string.IsNullOrWhiteSpace(runtime) ? Path.Combine(runtime, "dbdata", Name) : Path.Combine("/tmp", $"dbdata-{user}", Name);
        }
    }
}
