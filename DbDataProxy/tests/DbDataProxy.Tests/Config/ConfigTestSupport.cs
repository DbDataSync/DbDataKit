using DbDataProxy.Server.Configuration;

namespace DbDataProxy.Tests.Config;

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

    public static ProxyConfigLoad LoadFile(string path, ProxyConfigLoadOptions? options = null) =>
        ProxyConfig.Load(new ConfigurationBuilder().AddJsonFile(path, optional: false, reloadOnChange: false).Build(), options);

    /// <summary>Writes the JSON to a temp file (so positions and sources are real) and loads it.</summary>
    public static ProxyConfigLoad LoadJson(string json, ProxyConfigLoadOptions? options = null)
    {
        using var file = TempFile.Create(json);
        return LoadFile(file.Path, options);
    }

    public static string Format(ProxyConfigLoad load) => ProxyConfig.Format(load.Diagnostics);
}

/// <summary>A temporary configuration directory (<c>server.yaml</c>, <c>proxy/</c>, <c>connections/</c>) written from literal text.</summary>
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
    private readonly string _dir = System.IO.Directory.CreateTempSubdirectory("dbproxy-config-").FullName;

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
