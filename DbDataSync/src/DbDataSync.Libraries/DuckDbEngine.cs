using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DbDataSync.Libraries;

/// <summary>
/// The DuckDB engine (libduckdb) for the machine this runs on, taken from the repository's <c>duckdb.pin</c> (embedded in this assembly):
/// the package <c>DuckDB.NET.Data</c> carries only managed bindings, so the library that installs it also needs the engine next to it.
/// Both the downloaded archive and the library inside it are checked against the hashes in the pin, so a changed byte anywhere fails.
/// <para>
/// The archive comes from the pin's release address, or, for a machine without internet access, from a directory that holds the
/// archive (or the bare library) under the pin's file names: <see cref="SourceVariable"/>.
/// </para>
/// </summary>
public static class DuckDbEngine
{
    /// <summary>The package whose install needs the engine.</summary>
    public const string PackageId = "DuckDB.NET.Data";

    /// <summary>Names a directory (or an http(s) address) to take the archive from instead of the pin's release address.</summary>
    public const string SourceVariable = "DBDATASYNC_DUCKDB_ENGINE_SOURCE";

    public static bool Needs(IEnumerable<PackageRef> packages) =>
        packages.Any(p => string.Equals(p.Id, PackageId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The runtime identifier DuckDB's pin is keyed by for this machine, or null when no engine is pinned for it.</summary>
    public static string? CurrentRid()
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        if (OperatingSystem.IsWindows()) return arch == Architecture.X64 ? "win-x64" : null;
        if (OperatingSystem.IsMacOS()) return arch switch { Architecture.Arm64 => "osx-arm64", Architecture.X64 => "osx-x64", _ => null };
        if (OperatingSystem.IsLinux())
        {
            // The pin holds the glibc builds; Alpine and other musl systems are not covered.
            if (File.Exists("/etc/alpine-release")) return null;
            return arch switch { Architecture.X64 => "linux-x64", Architecture.Arm64 => "linux-arm64", _ => null };
        }
        return null;
    }

    /// <summary>Puts the engine for this machine into <paramref name="libDir"/>, unless it is already there and matches the pin.</summary>
    public static async Task EnsureAsync(string libDir, CancellationToken cancellationToken = default) =>
        await EnsureAsync(libDir, ReadEmbeddedPin(), CurrentRid(), Environment.GetEnvironmentVariable(SourceVariable), cancellationToken);

    /// <summary>The same with the pin, the platform and the source given (for tests).</summary>
    public static async Task EnsureAsync(
        string libDir, string pinText, string? rid, string? source, CancellationToken cancellationToken = default)
    {
        var pin = ParsePin(pinText);
        if (rid is null)
            throw new InvalidOperationException(
                $"No DuckDB engine is pinned for this platform ({RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}). " +
                "DuckDB 2.0 is pinned for Linux (glibc) x64 and arm64, Windows x64 and macOS x64 and arm64.");

        var key = rid.Replace('-', '_');
        var archive = Get(pin, "ARCHIVE_" + key);
        var archiveSha = Get(pin, "ARCHIVE_SHA256_" + key);
        var library = Get(pin, "LIB_" + key);
        var librarySha = Get(pin, "LIB_SHA256_" + key);
        var target = Path.Combine(libDir, library);

        if (File.Exists(target) && await HashAsync(target, cancellationToken) == librarySha)
            return;

        Directory.CreateDirectory(libDir);
        var temp = Path.Combine(Path.GetTempPath(), "dbdatasync-duckdb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var archivePath = Path.Combine(temp, archive);
            await FetchAsync(Get(pin, "BASE"), source, archive, archivePath, cancellationToken);
            if (await HashAsync(archivePath, cancellationToken) != archiveSha)
                throw new InvalidOperationException($"The DuckDB archive {archive} does not match the checksum in duckdb.pin (expected {archiveSha}).");

            var extracted = Path.Combine(temp, library);
            await ExtractAsync(archivePath, library, extracted, cancellationToken);
            if (await HashAsync(extracted, cancellationToken) != librarySha)
                throw new InvalidOperationException($"{library} inside {archive} does not match the checksum in duckdb.pin (expected {librarySha}).");

            File.Copy(extracted, target, overwrite: true);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private static async Task FetchAsync(string releaseBase, string? source, string archive, string destination, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(source) && !source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var local = Path.Combine(source, archive);
            if (!File.Exists(local))
                throw new FileNotFoundException($"{SourceVariable} points at '{source}', which has no {archive}.", local);
            File.Copy(local, destination);
            return;
        }

        var address = $"{(string.IsNullOrWhiteSpace(source) ? releaseBase : source).TrimEnd('/')}/{archive}";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        try
        {
            using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(destination);
            await response.Content.CopyToAsync(file, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Could not download the DuckDB engine from {address}: {ex.Message}. On a machine without internet access, put {archive} in a directory and set {SourceVariable} to it.", ex);
        }
    }

    private static async Task ExtractAsync(string archivePath, string name, string destination, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null) continue;
            if (!string.Equals(entry.Name.TrimStart('.', '/'), name, StringComparison.Ordinal)) continue;
            await using var output = File.Create(destination);
            await entry.DataStream.CopyToAsync(output, cancellationToken);
            return;
        }
        throw new InvalidOperationException($"{name} is not in {Path.GetFileName(archivePath)}.");
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
    }

    private static string Get(IReadOnlyDictionary<string, string> pin, string key) =>
        pin.TryGetValue(key, out var value) ? value : throw new InvalidOperationException($"duckdb.pin has no {key}.");

    /// <summary>The <c>KEY=value</c> lines of a pin; comments and blank lines are ignored.</summary>
    public static IReadOnlyDictionary<string, string> ParsePin(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (line.StartsWith('#') || eq <= 0) continue;
            values[line[..eq]] = line[(eq + 1)..].Trim();
        }
        return values;
    }

    /// <summary>The pin this build was made with (the repository's <c>duckdb.pin</c>, embedded).</summary>
    public static string ReadEmbeddedPin()
    {
        using var stream = typeof(DuckDbEngine).Assembly.GetManifestResourceStream("duckdb.pin")
            ?? throw new InvalidOperationException("duckdb.pin is not embedded in this build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The engine release the pin names (for example <c>2.0.0</c>).</summary>
    public static string PinnedVersion => Get(ParsePin(ReadEmbeddedPin()), "VERSION");
}
