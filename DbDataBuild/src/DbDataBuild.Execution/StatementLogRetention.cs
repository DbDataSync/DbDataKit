namespace DbDataBuild.Execution;

/// <summary>Keeps the local statement logs from piling up: `retention.statement_logs_days`. The logs are the pre-send safety net, so only old ones go, and only when a deploy or a refresh starts.</summary>
public static class StatementLogRetention
{
    /// <summary>Removes the statement logs in <paramref name="directory"/> last written more than <paramref name="days"/> days before <paramref name="nowUtc"/> (0 keeps them all). A file that cannot be removed is left. Returns how many were removed.</summary>
    public static int Prune(string directory, int days, DateTime nowUtc)
    {
        if (days <= 0 || !Directory.Exists(directory)) return 0;
        var limit = nowUtc.AddDays(-days);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) >= limit) continue;
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use, or not ours to remove: left */ }
        }
        return removed;
    }
}
