using System.Text.Json.Nodes;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Scheduler;
using DbDataKit.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbDataConsole.Server;

/// <summary>
/// The runner embedded in the console (plan 28 section 6a, plan 28a R20): the engine of <c>DbDataKit.Runner</c> hosted by the console as a group of one, <c>console</c>, with its own state in the console's state database.
/// <list type="bullet">
/// <item><b>What it serves.</b> The non-production channels the settings name (<c>dev</c> and <c>preview</c> by default) and the channel <c>system</c> of the console's own tasks. The production channel only after an administrator recorded
/// <see cref="PostureSetting.AcceptProductionOnConsoleRunner"/> (<see cref="AcceptProduction"/>); until then publishing or activating into it is refused with a message that names the choice. Once recorded, the console logs and shows the warning that
/// production schedules stop when the console does.</item>
/// <item><b>Isolation.</b> The same as any runner: every task of a package runs in a process of its own through the executor, with time and memory limits. Only the console's own system tasks (<see cref="ConsoleSystemTasks"/>) run in its process.</item>
/// <item><b>A cap</b> (<c>max_concurrent</c>) on what it runs at once.</item>
/// <item><b>Its version is the console's.</b> A product's own housekeeping stays with the product.</item>
/// </list>
/// </summary>
public sealed class ConsoleRunner : IHostedService, IAsyncDisposable
{
    public const string ProductionChannel = "prod";

    private readonly ConsoleRunnerSettings _settings;
    private readonly StateDatabase _state;
    private readonly ILogger _log;
    private readonly ConsoleChannels _channels;
    private RunnerHost? _host;

    public ConsoleRunner(ConsoleRunnerSettings settings, ConsoleSettings console, StateDatabase state, ILogger log)
    {
        _settings = settings;
        _state = state;
        _log = log;
        Posture = new PostureGuard(state, ConsoleState.ServiceId);
        _channels = new ConsoleChannels(settings, Posture);
        Publishing = new RunnerPublishing(state, serviceId: ConsoleState.ServiceId, instanceLease: null) { ChannelRefusal = Refusal };
        Directory = settings.DirectoryFor(console.Directory);
        Retention = console.State?.Retention;
        Version = ConsoleHost.Version;
    }

    public ConsoleRunnerSettings Settings => _settings;

    public PostureGuard Posture { get; }

    public RunnerPublishing Publishing { get; }

    public string Directory { get; }

    public string Version { get; }

    private RetentionSettings? Retention { get; }

    /// <summary>The engine; null until the console has started it and after it has stopped.</summary>
    public RunnerHost? Host => _host;

    public IReadOnlyCollection<string> Channels => _channels;

    /// <summary>Whether an administrator chose to run production packages here.</summary>
    public bool ProductionAccepted => _channels.ProductionAccepted;

    /// <summary>Who may do what on the embedded runner: the role of a signed-in person as the runner's roles.</summary>
    public static DbDataKit.Web.Trust.ServerRole RolesOf(UserRole role) => DbDataKit.Web.Trust.ServerRoles.Close(ConsoleRelay.RolesOf(role));

    /// <summary>Whether the tasks the runner starts run under an account other than the console's: they do not yet (no account switch is built, plan 28 question 6), so this is false.</summary>
    public static bool AccountSeparate => false;

    /// <summary>Why a channel is refused here, or null when this runner serves it.</summary>
    public string? Refusal(string channel)
    {
        if (channel == ConsoleRunnerSettings.SystemChannel) return null;
        if (_settings.RequireSeparateAccount && !AccountSeparate)
            return "The embedded runner of the console is set to require a separate account (runner.require_separate_account) and its tasks would run under the console's own account, so it takes no package of its own: the console's system tasks only. " +
                   "Run the package on a dbdatarunner server, or turn the setting off.";
        if (_channels.Contains(channel)) return null;
        if (channel == ProductionChannel)
        {
            try { Posture.Require(PostureSetting.AcceptProductionOnConsoleRunner, RunnerModule.AllPackages); }
            catch (PostureRefusedException ex)
            {
                return $"The console's embedded runner does not run production packages ('{channel}'): {ex.Setting.Effect} An administrator chooses '{ex.Setting.Name}' (run 'dbdatakit runner accept-production --yes', or confirm it in the console), " +
                       "which is recorded with who chose it and when.";
            }

            return null;
        }

        return $"The console's embedded runner serves the channels {string.Join(", ", _channels.NonProduction)} and '{ConsoleRunnerSettings.SystemChannel}', not '{channel}'. Add it to runner.channels in settings.yaml if it is not production.";
    }

    /// <summary>Records the administrator's choice to run production packages on the console's runner (audited: who chose it and when). Refused unless <paramref name="confirmed"/>.</summary>
    public PostureChoice AcceptProduction(string actor, string? note, bool confirmed)
    {
        var choice = Posture.Choose(PostureSetting.AcceptProductionOnConsoleRunner, RunnerModule.AllPackages, actor, note, confirmed);
        _channels.Refresh();
        _log.LogWarning("{Warning}", ProductionWarning);
        return choice;
    }

    /// <summary>Takes the choice back: the production channel is no longer served (its packages stay, and wait).</summary>
    public bool RevokeProduction(string actor, string? note = null)
    {
        var revoked = Posture.Revoke(PostureSetting.AcceptProductionOnConsoleRunner, RunnerModule.AllPackages, actor, note);
        _channels.Refresh();
        return revoked;
    }

    public const string ProductionWarning = "The console's embedded runner runs production packages: their schedules stop whenever the console stops, and they share the console's process, account and resources.";

    /// <summary>What the status of the embedded runner says besides the runner's own: its kind, its channels, the posture choice and the warning.</summary>
    public void DecorateStatus(JsonObject status)
    {
        var accepted = ProductionAccepted;
        var choice = Posture.Choices.List(RunnerModule.AllPackages).FirstOrDefault(c => c.Setting == PostureSetting.AcceptProductionOnConsoleRunner.Name);
        status["embedded"] = true;
        status["console_runner"] = new JsonObject
        {
            ["channels"] = new JsonArray([.. _channels.NonProduction.Select(c => (JsonNode)JsonValue.Create(c)!)]),
            ["system_channel"] = ConsoleRunnerSettings.SystemChannel,
            ["max_concurrent"] = _settings.MaxConcurrent,
            ["production_accepted"] = accepted,
            ["production_chosen_by"] = accepted ? choice?.ChosenBy : null,
            ["production_chosen_at"] = accepted ? choice?.ChosenAt.UtcDateTime.ToString("O") : null,
            ["production_warning"] = accepted ? ProductionWarning : null,
            ["accept_production_effect"] = PostureSetting.AcceptProductionOnConsoleRunner.Effect,
            ["require_separate_account"] = _settings.RequireSeparateAccount,
            ["account_separate"] = AccountSeparate,
        };
        if (status["capabilities"] is JsonObject capabilities) capabilities["account_separate"] = AccountSeparate ? "true" : "false";
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Directory);
        // the console's own tasks are published first, in process, so the first tick has them
        PublishSystemPackage();
        var system = new ConsoleSystemTasks(_state, Retention);
        var hostSettings = new RunnerHostSettings(Directory)
        {
            Channels = _channels,
            Group = ConsoleRunnerSettings.Group,
            ServiceId = ConsoleState.ServiceId,
            Version = Version,
            Generation = DbDataKit.Web.Trust.SuiteCompatibility.Generation,
            MaxConcurrentRuns = _settings.MaxConcurrent,
            Tick = _settings.Tick,
            CancellationGrace = _settings.CancellationGrace,
            TaskHostPath = _settings.TaskHostPath,
            WrapRunner = executor => new ConsoleTaskRunner(system, _settings.WrapRunner?.Invoke(executor) ?? executor),
        };
        _host = new RunnerHost(hostSettings, _state, _log);
        await _host.StartAsync(cancellationToken).ConfigureAwait(false);
        if (ProductionAccepted) _log.LogWarning("{Warning}", ProductionWarning);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_host is { } host) await host.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is { } host) await host.DisposeAsync().ConfigureAwait(false);
        _host = null;
    }

    private void PublishSystemPackage()
    {
        var actor = new RunnerActor("system:console", "dbdatakit console", "command");
        var manifest = ConsoleSystemTasks.Manifest(_settings.RetentionEvery);
        Publishing.Publish(new PublishInput(ConsoleSystemTasks.Package, manifest, [], ConsoleRunnerSettings.SystemChannel, PublishMode.Serve, Note: "the console's own system tasks (started with the console)"), actor, RunnerPublishing.InstallVerb);
    }

    /// <summary>The channels the scheduler reads at every tick: the configured non-production ones, <c>system</c>, and <c>prod</c> once an administrator chose it.</summary>
    private sealed class ConsoleChannels(ConsoleRunnerSettings settings, PostureGuard posture) : IReadOnlyCollection<string>
    {
        private volatile bool _accepted;
        private long _checked;

        public IReadOnlyList<string> NonProduction => settings.Channels;

        public bool ProductionAccepted
        {
            get
            {
                // the choice is read from the state at most once a second: a tick asks several times
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _checked) > 1000) Refresh();
                return _accepted;
            }
        }

        public void Refresh()
        {
            _accepted = posture.IsAllowed(PostureSetting.AcceptProductionOnConsoleRunner, RunnerModule.AllPackages);
            Interlocked.Exchange(ref _checked, Environment.TickCount64);
        }

        private IEnumerable<string> Current()
        {
            foreach (var c in settings.Channels) yield return c;
            yield return ConsoleRunnerSettings.SystemChannel;
            if (ProductionAccepted) yield return ProductionChannel;
        }

        public int Count => Current().Count();

        public IEnumerator<string> GetEnumerator() => Current().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
