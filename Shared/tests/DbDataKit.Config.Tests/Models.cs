using DbDataKit.Config.Yaml;

namespace DbDataKit.Config.Tests;

public enum Bitness { X86, X64 }

[ConfigSchema("proxy", Version = 2)]
public class ProxyConfig
{
    public ListenConfig Listen { get; set; } = new();
    public List<UserConfig> Users { get; set; } = [];
    public Dictionary<string, DataSourceConfig> DataSources { get; set; } = [];
    [ConfigKey(Default = 30, Min = 1, Max = 3600, Description = "Seconds.")]
    public int Timeout { get; set; } = 30;
    [Secret]
    public string? Password { get; set; }
    public Bitness Bitness { get; set; } = Bitness.X64;
    public TimeSpan? Idle { get; set; }
    [Deprecated("use timeout", Since = "2")]
    public int? OldTimeout { get; set; }
    public bool Verbose { get; set; }
    public List<string> Tags { get; set; } = [];
    public Segment? Segment { get; set; }
}

public class ListenConfig
{
    public string Address { get; set; } = "127.0.0.1";
    [ConfigKey(Min = 1, Max = 65535)]
    public int Port { get; set; } = 5432;
}

public class UserConfig
{
    [ConfigKey(Required = true)]
    public string Name { get; set; } = "";
    public List<string> DataSources { get; set; } = [];
}

[ConfigSchema(Naming = KeyNaming.SnakeCase)]
public class DataSourceConfig
{
    [ConfigKey(Required = true, Pattern = "[a-z]+")]
    public string Provider { get; set; } = "";
    public int ConnectTimeout { get; set; } = 15;
}

[ConfigUnion("kind")]
[ConfigUnionCase("rows", typeof(RowsSegment))]
[ConfigUnionCase("time", typeof(TimeSegment))]
public abstract class Segment;
public class RowsSegment : Segment { public int Size { get; set; } = 1000; }
public class TimeSegment : Segment { [ConfigKey(Required = true)] public string Column { get; set; } = ""; }

[ConfigSchema("loose", UnknownKeys = UnknownKeyPolicy.Preserve)]
public class LooseConfig
{
    public string Name { get; set; } = "";
    [ConfigExtra]
    public Dictionary<string, YamlNode> Extra { get; set; } = [];
}

public static class Util
{
    public static BindResult<T> Bind<T>(string yaml, string file = "t.yml") where T : class
    {
        var d = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, file, d);
        var r = new ConfigBinder().Bind<T>(root, file);
        return new BindResult<T>(r.Value, [.. d, .. r.Diagnostics], r.Sources);
    }
}
