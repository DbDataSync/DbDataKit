using System.Security;

namespace DbData.Hosting.WindowsService;

/// <summary>Fake SCM for tests and dry runs: records every operation and never touches the machine.</summary>
public sealed class InMemoryServiceControlManager : IServiceControlManager
{
    private readonly Dictionary<string, ServiceConfigSnapshot> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServiceRunState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Operations in order, e.g. "create:Svc", "start:Svc". Passwords are never recorded.</summary>
    public List<string> Operations { get; } = [];

    /// <summary>True if the last create/reconfigure was handed a password (the value itself is not kept).</summary>
    public bool LastPasswordSupplied { get; private set; }

    public ServiceConfigSnapshot? GetConfig(string name) => _services.GetValueOrDefault(name);

    public void Create(ServiceInstallSpec spec, SecureString? password)
    {
        if (_services.ContainsKey(spec.Name))
        {
            throw new ServiceControlException(ServiceControlError.AlreadyExists, 1073, "create", spec.Name);
        }

        Operations.Add($"create:{spec.Name}");
        LastPasswordSupplied = password is not null;
        _services[spec.Name] = ToSnapshot(spec);
        _states[spec.Name] = ServiceRunState.Stopped;
    }

    public void Reconfigure(ServiceInstallSpec spec, SecureString? password)
    {
        Require(spec.Name, "reconfigure");
        Operations.Add($"reconfigure:{spec.Name}");
        LastPasswordSupplied = password is not null;
        _services[spec.Name] = ToSnapshot(spec);
    }

    public void Delete(string name)
    {
        Require(name, "delete");
        Operations.Add($"delete:{name}");
        _services.Remove(name);
        _states.Remove(name);
    }

    public void Start(string name)
    {
        Require(name, "start");
        Operations.Add($"start:{name}");
        _states[name] = ServiceRunState.Running;
    }

    public void Stop(string name)
    {
        Require(name, "stop");
        Operations.Add($"stop:{name}");
        _states[name] = ServiceRunState.Stopped;
    }

    public ServiceStatus QueryStatus(string name)
    {
        Require(name, "query status");
        return new ServiceStatus(_states[name], _states[name] == ServiceRunState.Running ? 4242 : 0);
    }

    private void Require(string name, string op)
    {
        if (!_services.ContainsKey(name))
        {
            throw new ServiceControlException(ServiceControlError.NotFound, 1060, op, name);
        }
    }

    private static ServiceConfigSnapshot ToSnapshot(ServiceInstallSpec spec) => new()
    {
        Name = spec.Name,
        DisplayName = spec.DisplayName ?? spec.Name,
        Description = spec.Description,
        BinaryPathName = spec.CommandLine,
        StartMode = spec.StartMode,
        AccountName = spec.EffectiveAccount.ScmName ?? "LocalSystem",
        Dependencies = spec.Dependencies,
        FailureRestartActions = spec.Failure.RestartDelays.Count,
        SidType = spec.SidType,
    };
}

/// <summary>In-memory Event Log source registry for tests.</summary>
public sealed class InMemoryEventLogSourceRegistrar : IEventLogSourceRegistrar
{
    public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Exists(string source) => Sources.Contains(source);

    public void Register(string source) => Sources.Add(source);

    public void Unregister(string source) => Sources.Remove(source);
}
