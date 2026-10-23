using System.Diagnostics;

namespace DbDataProxy.Tests.Support;

/// <summary>Locates (publishing on demand) the self-contained 32-bit worker executable.</summary>
public static class WorkerBuild
{
    private static readonly object Gate = new();

    public static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DbDataProxy.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    /// <summary>Path to the win-x86 worker, building it if it is missing or older than its sources.</summary>
    public static string EnsurePublished()
    {
        string root = RepositoryRoot();
        string outDir = Path.Combine(root, "artifacts", "worker-x86");
        string exe = Path.Combine(outDir, "dbdataproxy-worker.exe");

        lock (Gate)
        {
            if (File.Exists(exe) && File.GetLastWriteTimeUtc(exe) >= NewestSource(root)) return exe;

            var psi = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[]
                     {
                         "publish", Path.Combine(root, "src", "DbDataProxy.Worker"), "-c", "Release", "-r", "win-x86",
                         "--self-contained", "-p:PublishSingleFile=true", "-o", outDir,
                     })
                psi.ArgumentList.Add(a);

            using var process = Process.Start(psi)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("Publishing the 32-bit worker failed:\n" + output);
            return exe;
        }
    }

    private static DateTime NewestSource(string root) =>
        new[] { "DbDataProxy.Worker", "DbDataProxy.Core" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, "src", p), "*.cs*", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
}
