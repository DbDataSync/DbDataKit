using DbDataKit.Quality;
using DbDataKit.Runner.Kit;

namespace DbDataBuild.Cli.Runner;

/// <summary>
/// The refresh package's <c>check</c> task (plan 35 I3a): the checks of the project's models are evaluated again, read only, with the read logins the package's slots bound, and recorded in the Runner's <c>quality</c> module under this run, so
/// a block clears when the data was fixed, without a refresh. The run is the shared <see cref="QualityCheckJob"/>; Build gives it the source (<see cref="ModelQuality.PackageSourceAsync"/>). Nothing is loaded and nothing is written to a target.
/// </summary>
[RunnerTask(QualityCheckTask.Name)]
public sealed class CheckJob : QualityCheckJob
{
    protected override Task<QualityCheckSource> SourceAsync(CancellationToken ct)
    {
        var project = Environment.GetEnvironmentVariable(RefreshJob.PackageDirectoryVariable);
        if (string.IsNullOrWhiteSpace(project) || !Directory.Exists(project))
            throw new TaskFailureException(DbDataKit.Runner.TaskFailureKind.Permanent, $"The package directory ({RefreshJob.PackageDirectoryVariable}) is not set or does not exist; the check task reads the project the package holds.");
        return ModelQuality.PackageSourceAsync(project, Environment.GetEnvironmentVariable, ct);
    }
}
