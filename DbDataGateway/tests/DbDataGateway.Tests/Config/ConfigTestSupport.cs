using DbDataGateway.Server.Configuration;

namespace DbDataGateway.Tests.Config;

/// <summary>Tests that touch process-wide state (environment variables, Console.Error) must not run beside the host fixtures that read both.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessStateCollection
{
    public const string Name = "ProcessState";
}

internal static class ConfigTestSupport
{
    public static string GoldenDirectory => Path.Combine(AppContext.BaseDirectory, "Config", "Golden");

    public static IEnumerable<object[]> GoldenCases(string prefix) =>
        Directory.EnumerateFiles(GoldenDirectory, prefix + "*.json").Select(f => new object[] { Path.GetFileNameWithoutExtension(f) }).OrderBy(c => (string)c[0], StringComparer.Ordinal);

    public static GatewayConfigLoad LoadFile(string path, GatewayConfigLoadOptions? options = null) =>
        GatewayConfig.Load(new ConfigurationBuilder().AddJsonFile(path, optional: false, reloadOnChange: false).Build(), options);

    /// <summary>Writes the JSON to a temp file (so positions and sources are real) and loads it.</summary>
    public static GatewayConfigLoad LoadJson(string json, GatewayConfigLoadOptions? options = null)
    {
        using var file = TempFile.Create(json);
        return LoadFile(file.Path, options);
    }

    /// <summary>Loads a configuration directory and lets go of it: the directory's provider watches the folder, and on Linux each watcher left open is one of the 128 inotify instances a user has.</summary>
    public static GatewayConfigLoad LoadDirectory(string directory)
    {
        using var root = (ConfigurationRoot)new ConfigurationBuilder().Add(new GatewayConfigDirectorySource(directory)).Build();
        return GatewayConfig.Load(root);
    }

    /// <summary>The management audit of the gateway in <paramref name="dataDirectory"/>, oldest first, each entry as the JSON it is kept as (plan 20 O02: in the state, no longer a file).</summary>
    public static List<System.Text.Json.Nodes.JsonNode> AuditEntries(string dataDirectory)
    {
        var log = new DbDataGateway.Server.State.StateAuditLog(DbDataGateway.Server.State.GatewayState.Open(dataDirectory));
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        return [.. log.Read(int.MaxValue).Select(e => System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(e, json))!)];
    }

    public static string Format(GatewayConfigLoad load) => GatewayConfig.Format(load.Diagnostics);
}

/// <summary>A data source as a <c>connection/1</c> file (plan 13, E02), written the way the gateway's own example is.</summary>
internal static class Conn
{
    /// <param name="connectionString">The connection string (no credential), or null to leave it out.</param>
    /// <param name="auth">The <c>auth:</c> line(s), such as <c>auth: { mode: sql, user: u, password_secret: env:PW }</c>.</param>
    /// <param name="gateway">Lines for <c>extensions.gateway</c>, each already a <c>key: value</c> (no indentation).</param>
    public static string Yaml(string name, string engine = "sqlserver", string? connectionString = "Server=s;Database=d", string? auth = null, string[]? gateway = null, string? extra = null)
    {
        var text = $"schema: connection/1\nname: {name}\nengine: {engine}\n";
        if (connectionString is not null) text += $"connection_string: '{connectionString}'\n";
        if (auth is not null) text += auth + "\n";
        if (extra is not null) text += extra + "\n";
        if (gateway is { Length: > 0 }) text += "extensions:\n  gateway:\n" + string.Join("", gateway.Select(l => "    " + l + "\n"));
        return text;
    }

    /// <summary>The line (1-based) a key is on in a file written by <see cref="Yaml"/>.</summary>
    public static int LineOf(string text, string key) => Array.FindIndex(text.Split('\n'), l => l.TrimStart().StartsWith(key + ":", StringComparison.Ordinal)) + 1;
}

/// <summary>A temporary configuration directory (<c>server.yaml</c>, <c>gateway/</c>, <c>connections/</c>) written from literal text.</summary>
internal static class TestFolder
{
    /// <summary>Deletes a folder a test made. A git repository's object files are read-only, which Windows will not delete until the attribute is cleared.</summary>
    public static void Delete(string path)
    {
        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            System.IO.Directory.Delete(path, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class TempConfig : IDisposable
{
    private readonly TempFile _holder = TempFile.Create("", "placeholder");

    public string Directory => _holder.DirectoryPath;

    public string ServerYaml => Path("server.yaml");

    public string Path(string relative) => System.IO.Path.Combine(Directory, relative);

    public static TempConfig Create(params (string Name, string Content)[] files)
    {
        var config = new TempConfig();
        foreach (var (name, content) in files) config.Write(name, content);
        return config;
    }

    public void Write(string relative, string content)
    {
        var path = Path(relative);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose() => _holder.Dispose();
}

internal sealed class TempFile : IDisposable
{
    private readonly string _dir = System.IO.Directory.CreateTempSubdirectory("dbgateway-config-").FullName;

    public string Path { get; }

    private TempFile(string name, string content)
    {
        Path = System.IO.Path.Combine(_dir, name);
        File.WriteAllText(Path, content);
    }

    public static TempFile Create(string content, string name = "appsettings.json") => new(name, content);

    public string DirectoryPath => _dir;

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

/// <summary>Sets environment variables for the scope of a test (use inside the ProcessState collection).</summary>
internal sealed class EnvScope : IDisposable
{
    private readonly List<string> _names = [];

    public EnvScope(params (string Name, string Value)[] variables)
    {
        foreach (var (name, value) in variables)
        {
            Environment.SetEnvironmentVariable(name, value);
            _names.Add(name);
        }
    }

    public void Dispose()
    {
        foreach (var name in _names) Environment.SetEnvironmentVariable(name, null);
    }
}
