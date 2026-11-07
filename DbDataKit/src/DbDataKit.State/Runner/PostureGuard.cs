using System.Data.Common;
using System.Text.Json;

namespace DbDataKit.State;

/// <summary>
/// One default of the runner that an administrator may loosen (plan 28 section 5): its name as stored in <c>posture_choice</c>, what it is today, what changes and why it matters. The suite does not
/// block these choices; it makes each one explicit and records who made it.
/// </summary>
public sealed record PostureSetting(string Name, string Default, string Effect)
{
    /// <summary>Run a task inside the runner's own process instead of a process of its own.</summary>
    public static readonly PostureSetting RunInProcess = new("run_in_process", "a task runs in its own process",
        "The task runs inside the runner's own process: a crash, a memory leak or malicious code in it takes the runner and every other task down with it, and the process limits do not apply to it.");

    /// <summary>Time and memory limits larger than the runner's defaults.</summary>
    public static readonly PostureSetting LongerLimits = new("longer_limits", "a task runs within the runner's default time and memory limits",
        "The package may ask for more time and memory than the runner's defaults: one task can hold the machine's memory or a worker for hours, and other tasks on the runner may starve.");

    /// <summary>A more privileged account than the runner's low-privilege service account.</summary>
    public static readonly PostureSetting PrivilegedAccount = new("privileged_account", "a task runs under the runner's low-privilege service account",
        "The task runs under an account with more rights than the runner's service account: whatever that account can read or change on the machine and the network, the package's code can too.");

    /// <summary>Recompile a package's source on the instance that runs it.</summary>
    public static readonly PostureSetting RecompileOnInstance = new("recompile_on_instance", "a package runs from the build artifact that was prepared and checked once",
        "The instance compiles the package's source itself instead of running the checked artifact: the compiler and its package sources run on the runner, and what is built there is not what was reviewed.");

    /// <summary>A separate account per package.</summary>
    public static readonly PostureSetting PerPackageAccount = new("per_package_account", "every task runs under the one account of the runner",
        "Each package runs under its own configured account: it isolates packages from each other, but the runner must be able to switch to those accounts (and hold their credentials), which widens what the runner itself can do.");

    /// <summary>Run production packages on the console's embedded runner.</summary>
    public static readonly PostureSetting AcceptProductionOnConsoleRunner = new("accept_production_on_console_runner", "the console's embedded runner runs evaluation, system and development work only",
        "Production packages run on the runner embedded in the console: their schedules stop whenever the console stops, and they share the console's process, account and resources.");

    /// <summary>Every setting that can be loosened.</summary>
    public static IReadOnlyList<PostureSetting> All { get; } =
        [RunInProcess, LongerLimits, PrivilegedAccount, RecompileOnInstance, PerPackageAccount, AcceptProductionOnConsoleRunner];

    public static PostureSetting? Find(string name) => All.FirstOrDefault(s => s.Name == name);

    public override string ToString() => Name;
}

/// <summary>A loosened default was used without an administrator's recorded choice (or a choice was made without confirmation).</summary>
public sealed class PostureRefusedException(PostureSetting setting, string package, string message) : InvalidOperationException(message)
{
    public PostureSetting Setting { get; } = setting;
    public string Package { get; } = package;
}

/// <summary>
/// Plan 28 section 5 over <see cref="PostureChoices"/>: a default never needs a choice; loosening one is refused until an administrator has confirmed it, and every choice and revoke is audited
/// (<c>runner.posture.choose</c>, <c>runner.posture.revoke</c>; the target is the setting). The audit entry is written in the same transaction as the choice when the <c>runner</c> and <c>audit</c>
/// modules are in one store, and immediately after when they are not (a SQLite file of their own).
/// </summary>
public sealed class PostureGuard(StateDatabase database, string serviceId)
{
    public const string ChooseVerb = "runner.posture.choose";
    public const string RevokeVerb = "runner.posture.revoke";
    public const string SettingKind = "posture_setting";

    private readonly AuditLog _audit = new(database, serviceId);

    /// <summary>The recorded choices.</summary>
    public PostureChoices Choices { get; } = new(database);

    /// <summary>True when a choice applies to the package (its own or the install-wide one).</summary>
    public bool IsAllowed(PostureSetting setting, string package) => Choices.IsChosen(package, setting.Name);

    /// <summary>Returns when the package may use the loosened <paramref name="setting"/>; otherwise says what it would change and how an administrator confirms it.</summary>
    public void Require(PostureSetting setting, string package)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentException.ThrowIfNullOrWhiteSpace(package);
        if (Choices.IsChosen(package, setting.Name)) return;
        throw new PostureRefusedException(setting, package,
            $"Package '{package}' asks for '{setting.Name}', which loosens a default (today: {setting.Default}). {setting.Effect} " +
            $"An administrator confirms it by choosing '{setting.Name}' for this package, or for all packages ('{RunnerModule.AllPackages}'), in the console's runner settings, confirming the effect above; " +
            "the choice is recorded in the audit with who chose it and when.");
    }

    /// <summary>
    /// Records the choice and audits it. Refused (<see cref="PostureRefusedException"/>) unless <paramref name="confirmed"/> is true: the caller shows the setting's effect and asks.
    /// <paramref name="package"/> is a package name or <see cref="RunnerModule.AllPackages"/>.
    /// </summary>
    public PostureChoice Choose(PostureSetting setting, string package, string actor, string? note, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentException.ThrowIfNullOrWhiteSpace(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (!confirmed)
            throw new PostureRefusedException(setting, package,
                $"'{setting.Name}' for '{package}' was not confirmed, so nothing changed. {setting.Effect} Choose it again with the confirmation set to say that you accept this.");
        Write(setting, package, actor, ChooseVerb, note, (connection, tx) =>
        {
            Choices.Record(connection, tx, package, setting.Name, actor, note);
            return true;
        });
        return Choices.List(package).First(c => c.Setting == setting.Name);
    }

    /// <summary>Takes a choice back so the default applies again; false when there was none. Audited when something was revoked.</summary>
    public bool Revoke(PostureSetting setting, string package, string actor, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentException.ThrowIfNullOrWhiteSpace(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        return Write(setting, package, actor, RevokeVerb, note, (connection, tx) => Choices.Revoke(connection, tx, package, setting.Name));
    }

    private bool SameStore() =>
        database.Layout is not { } layout || string.Equals(layout.Namespace(RunnerModule.Name).File, layout.Namespace(AuditModule.Name).File, StringComparison.Ordinal);

    /// <summary>Runs the change and audits it when it changed something; returns what the change returned.</summary>
    private bool Write(PostureSetting setting, string package, string actor, string verb, string? note, Func<DbConnection, DbTransaction?, bool> change)
    {
        var entry = new AuditEvent("person", actor, verb, TargetKind: SettingKind, TargetId: setting.Name,
            Detail: JsonSerializer.Serialize(new { package, setting = setting.Name, note }));
        if (SameStore())
        {
            return database.Retry(() =>
            {
                using var connection = database.OpenConnection();
                using var transaction = database.BeginTransaction(connection, RunnerModule.Name);
                var changed = change(connection, transaction);
                if (changed) AuditLog.Record(database, connection, transaction, serviceId, entry);
                transaction.Commit();
                return changed;
            });
        }

        var done = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            return change(connection, null);
        });
        if (done) _audit.Record(entry);
        return done;
    }
}
