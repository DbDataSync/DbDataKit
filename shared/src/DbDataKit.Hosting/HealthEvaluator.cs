namespace DbDataKit.Hosting;

public static class HealthEvaluator
{
    public static async Task<HealthReport> EvaluateAsync(
        IEnumerable<IReadinessProbe> probes, IDrainCoordinator drain, CancellationToken cancellationToken)
    {
        var results = new List<ComponentHealth>();
        foreach (var probe in probes)
        {
            try
            {
                results.Add(await probe.CheckAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new ComponentHealth(probe.Name, HealthStatus.Unhealthy, ex.GetType().Name));
            }
        }

        return HealthReport.From(results, drain.IsDraining);
    }
}
