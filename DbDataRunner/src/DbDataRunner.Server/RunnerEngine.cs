using DbDataKit.Runner.Api;
using DbDataKit.Runner;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbDataRunner.Server;

/// <summary>
/// The runner inside the <c>dbdatarunner</c> server process (plan 28a R10), a hosted service over the engine of <c>DbDataKit.Runner</c> (<see cref="RunnerHost"/>, which the console's embedded runner hosts too, R20): the settings of the
/// server are turned into the host's, and the host's start and stop are the service's. See <see cref="RunnerHost"/> for what start and stop do.
/// </summary>
public sealed class RunnerEngine(RunnerSettings settings, DbDataKit.State.StateDatabase state, ILogger? logger = null) : RunnerHost(settings.ToHostSettings() with { VersionGate = DbDataKit.Runner.Api.RunnerVersionGate.Create(state) }, state, logger), IHostedService
{
}
