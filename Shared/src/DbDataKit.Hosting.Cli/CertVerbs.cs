namespace DbDataKit.Hosting.Cli;

/// <summary>A <c>cert</c> verb a product adds to (or replaces in) the shared command.</summary>
/// <param name="Name">The word after <c>cert</c>, matched case-insensitively.</param>
/// <param name="Usage">The usage text for this verb: one or more lines, shown in the usage listing in registration order.</param>
/// <param name="Run">Runs the verb with the arguments after its name; returns the process exit code.</param>
public sealed record CertVerb(string Name, string Usage, Func<string[], IConsole, int> Run);

/// <summary>
/// The verbs a product registers on <see cref="CertCommand"/>. A registered verb takes precedence over a built-in
/// of the same name, so a product keeps its own <c>status</c> or <c>renew</c> while the rest stay shared.
/// </summary>
public sealed class CertVerbs
{
    private readonly List<CertVerb> _verbs = [];

    public IReadOnlyList<CertVerb> All => _verbs;

    public IEnumerable<string> Names => _verbs.Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a verb. Registering a name again adds another usage entry (a verb can have several forms) and keeps the first handler.</summary>
    public CertVerbs Add(string name, string usage, Func<string[], IConsole, int> run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(run);
        _verbs.Add(new CertVerb(name, usage, run));
        return this;
    }

    /// <summary>Runs the registered verb called <paramref name="name"/>; false when there is none.</summary>
    public bool TryRun(string name, string[] args, IConsole console, out int exitCode)
    {
        var verb = _verbs.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (verb is null)
        {
            exitCode = 0;
            return false;
        }

        exitCode = verb.Run(args, console);
        return true;
    }

    /// <summary>The usage entries in registration order, one per registration.</summary>
    public IEnumerable<string> UsageLines => _verbs.Select(v => v.Usage);
}
