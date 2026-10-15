using System.Security;
using System.Text;

namespace DbData.Hosting.WindowsService;

public sealed record PlanStep(string Action, string Description);

public sealed record InstallPlan(IReadOnlyList<PlanStep> Steps, bool ServiceExists)
{
    public bool IsNoOp => Steps.Count == 0;

    /// <summary>Dry-run text. Contains no secrets (passwords are never part of a plan).</summary>
    public string Render()
    {
        if (IsNoOp) return "Nothing to do: the service already matches.";
        var sb = new StringBuilder();
        var i = 1;
        foreach (var s in Steps)
        {
            sb.Append(i++).Append(". ").Append(s.Action).Append(": ").AppendLine(s.Description);
        }

        return sb.ToString();
    }
}

public enum InstallOutcome
{
    Created,
    Reconfigured,
    Unchanged,
    DryRun,
}

public sealed record InstallResult(InstallOutcome Outcome, InstallPlan Plan);

public sealed class InstallOptions
{
    public bool DryRun { get; init; }

    /// <summary>The version currently installed (from the product's own records), for downgrade detection.</summary>
    public string? InstalledVersion { get; init; }

    public bool AllowDowngrade { get; init; }
}

public sealed class ServiceInstallException(string message) : Exception(message);

/// <summary>Idempotent install/uninstall/start/stop on top of an <see cref="IServiceControlManager"/>.</summary>
public sealed class ServiceProvisioner(
    IServiceControlManager scm,
    IEventLogSourceRegistrar? eventLog = null,
    Func<TimeSpan, Task>? delay = null)
{
    private readonly Func<TimeSpan, Task> _delay = delay ?? (t => Task.Delay(t));

    /// <summary>Validate the spec and password against the safety rules. Throws <see cref="ServiceInstallException"/>.</summary>
    public static void Validate(ServiceInstallSpec spec, SecureString? password, ServiceConfigSnapshot? existing)
    {
        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Name.IndexOfAny(['\\', '/']) >= 0)
        {
            throw new ServiceInstallException("Service name is empty or contains a path separator.");
        }

        if (string.IsNullOrWhiteSpace(spec.BinaryPath))
        {
            throw new ServiceInstallException("Binary path is required.");
        }

        foreach (var a in spec.Arguments)
        {
            if (a.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                throw new ServiceInstallException("Service arguments must not carry passwords; credentials are never placed on a command line.");
            }
        }

        var account = spec.EffectiveAccount;
        if (account.Kind == ServiceAccountKind.LocalSystem && !spec.AllowLocalSystem)
        {
            throw new ServiceInstallException("Running as LocalSystem is refused unless explicitly allowed (--allow-localsystem).");
        }

        var accountChanges = existing is null || !SameAccount(existing.AccountName, account);
        if (account.RequiresPassword)
        {
            if (accountChanges && (password is null || password.Length == 0))
            {
                throw new ServiceInstallException($"Account '{account.Name}' needs a password; supply it with --password-stdin.");
            }
        }
        else if (password is not null && password.Length > 0)
        {
            throw new ServiceInstallException($"A password was supplied but account kind {account.Kind} does not take one.");
        }
    }

    public InstallPlan Plan(ServiceInstallSpec spec, SecureString? password = null, InstallOptions? options = null)
    {
        options ??= new InstallOptions();
        var existing = scm.GetConfig(spec.Name);
        Validate(spec, password, existing);
        CheckDowngrade(spec, options);

        var steps = new List<PlanStep>();
        var account = spec.EffectiveAccount;
        if (spec.EventLogSource is { } src && eventLog is not null && !eventLog.Exists(src))
        {
            steps.Add(new("register-event-source", $"Register Event Log source '{src}'"));
        }

        if (existing is null)
        {
            steps.Add(new("create", $"Create service '{spec.Name}' running {spec.CommandLine} as {account.Name} ({spec.StartMode})"));
            steps.Add(new("configure", $"Description, failure actions ({spec.Failure.RestartDelays.Count} restarts), SID type {spec.SidType}"));
            return new InstallPlan(steps, false);
        }

        var diffs = new List<string>();
        if (!string.Equals(existing.BinaryPathName, spec.CommandLine, StringComparison.Ordinal)) diffs.Add($"command line -> {spec.CommandLine}");
        if (existing.StartMode != spec.StartMode) diffs.Add($"start mode {existing.StartMode} -> {spec.StartMode}");
        if (!SameAccount(existing.AccountName, account)) diffs.Add($"account {existing.AccountName} -> {account.Name}");
        else if (password is not null && account.RequiresPassword) diffs.Add("account password");
        if (!string.Equals(existing.DisplayName, spec.DisplayName ?? spec.Name, StringComparison.Ordinal)) diffs.Add("display name");
        if (spec.Description is not null && !string.Equals(existing.Description, spec.Description, StringComparison.Ordinal)) diffs.Add("description");
        if (existing.FailureRestartActions != spec.Failure.RestartDelays.Count) diffs.Add("failure actions");
        if (existing.SidType != spec.SidType) diffs.Add($"SID type {existing.SidType} -> {spec.SidType}");
        if (!existing.Dependencies.SequenceEqual(spec.Dependencies, StringComparer.OrdinalIgnoreCase)) diffs.Add("dependencies");

        if (diffs.Count > 0)
        {
            steps.Add(new("reconfigure", $"Update '{spec.Name}': {string.Join("; ", diffs)}"));
        }

        return new InstallPlan(steps, true);
    }

    public InstallResult Install(ServiceInstallSpec spec, SecureString? password = null, InstallOptions? options = null)
    {
        options ??= new InstallOptions();
        var plan = Plan(spec, password, options);
        if (options.DryRun)
        {
            return new InstallResult(InstallOutcome.DryRun, plan);
        }

        if (plan.IsNoOp)
        {
            return new InstallResult(InstallOutcome.Unchanged, plan);
        }

        if (spec.EventLogSource is { } src && eventLog is not null && !eventLog.Exists(src))
        {
            eventLog.Register(src);
        }

        if (plan.ServiceExists)
        {
            if (plan.Steps.Any(s => s.Action == "reconfigure"))
            {
                scm.Reconfigure(spec, password);
                return new InstallResult(InstallOutcome.Reconfigured, plan);
            }

            return new InstallResult(InstallOutcome.Unchanged, plan);
        }

        scm.Create(spec, password);
        return new InstallResult(InstallOutcome.Created, plan);
    }

    /// <summary>Stop (waiting), then delete. Returns false when there was nothing to remove.</summary>
    public async Task<bool> UninstallAsync(string name, TimeSpan stopTimeout, bool dryRun = false, string? eventLogSource = null)
    {
        if (scm.GetConfig(name) is null) return false;
        if (dryRun) return true;

        await StopAsync(name, stopTimeout).ConfigureAwait(false);
        scm.Delete(name);
        if (eventLogSource is not null && eventLog?.Exists(eventLogSource) == true)
        {
            eventLog.Unregister(eventLogSource);
        }

        return true;
    }

    public async Task StartAsync(string name, TimeSpan timeout)
    {
        scm.Start(name);
        await WaitForAsync(name, ServiceRunState.Running, timeout).ConfigureAwait(false);
    }

    public async Task StopAsync(string name, TimeSpan timeout)
    {
        if (scm.QueryStatus(name).State == ServiceRunState.Stopped) return;
        scm.Stop(name);
        await WaitForAsync(name, ServiceRunState.Stopped, timeout).ConfigureAwait(false);
    }

    private async Task WaitForAsync(string name, ServiceRunState target, TimeSpan timeout)
    {
        var waited = TimeSpan.Zero;
        var step = TimeSpan.FromMilliseconds(250);
        while (scm.QueryStatus(name).State != target)
        {
            if (waited >= timeout)
            {
                throw new TimeoutException($"Service '{name}' did not reach {target} within {timeout.TotalSeconds:0}s.");
            }

            await _delay(step).ConfigureAwait(false);
            waited += step;
        }
    }

    private static void CheckDowngrade(ServiceInstallSpec spec, InstallOptions options)
    {
        if (options.AllowDowngrade || options.InstalledVersion is null || spec.Version is null) return;
        if (Version.TryParse(Core(options.InstalledVersion), out var have)
            && Version.TryParse(Core(spec.Version), out var want)
            && want < have)
        {
            throw new ServiceInstallException($"Refusing to downgrade from {options.InstalledVersion} to {spec.Version} without --allow-downgrade.");
        }
    }

    private static string Core(string v)
    {
        var cut = v.IndexOfAny(['-', '+']);
        return cut < 0 ? v : v[..cut];
    }

    internal static bool SameAccount(string? existing, ServiceAccount wanted)
    {
        if (wanted.Kind == ServiceAccountKind.LocalSystem)
        {
            return existing is null || existing.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
                || existing.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(existing, wanted.Name, StringComparison.OrdinalIgnoreCase)
            || (existing is not null && existing.StartsWith(@".\", StringComparison.Ordinal)
                && string.Equals(existing[2..], wanted.Name.Replace(@".\", ""), StringComparison.OrdinalIgnoreCase));
    }
}
