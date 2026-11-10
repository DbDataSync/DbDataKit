using DbDataKit.State;
using Microsoft.Extensions.Configuration;

namespace DbDataGateway.Server.Configuration;

/// <summary>
/// The state engine and connection the suite's variables name for the Gateway (<c>DBDATAKIT_STATE_GATEWAY_ENGINE</c>, else <c>DBDATAKIT_STATE_ENGINE</c>; the same for the connection, <see cref="SuiteEnvironment"/>) as the
/// configuration keys <c>Gateway:State:Engine</c> and <c>Gateway:State:Connection</c>. Added above the settings files and below the Gateway's own environment spellings and the command line, which still win.
/// </summary>
public sealed class SuiteStateConfigurationSource(Func<string, string?>? environment = null) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new SuiteStateConfigurationProvider(environment);
}

/// <summary>The provider of <see cref="SuiteStateConfigurationSource"/>; it remembers which variable gave each key so <c>state show</c> can name it.</summary>
public sealed class SuiteStateConfigurationProvider(Func<string, string?>? environment) : ConfigurationProvider
{
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);

    public override void Load()
    {
        var read = environment ?? Environment.GetEnvironmentVariable;
        Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        _variables.Clear();
        foreach (var (key, own, install) in new[]
        {
            ($"{GatewayOptions.SectionName}:State:Engine", SuiteEnvironment.StateEngineVariableOf("gateway"), SuiteEnvironment.StateEngineVariable),
            ($"{GatewayOptions.SectionName}:State:Connection", SuiteEnvironment.StateConnectionVariableOf("gateway"), SuiteEnvironment.StateConnectionVariable),
        })
            foreach (var name in new[] { own, install })
                if (read(name) is { } value && !string.IsNullOrWhiteSpace(value))
                {
                    Data[key] = value;
                    _variables[key] = name;
                    break;
                }
    }

    /// <summary>The variable that gave <paramref name="key"/>.</summary>
    public string VariableOf(string key) => _variables.TryGetValue(key, out var name) ? name : key;
}
