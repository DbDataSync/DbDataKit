using System.Diagnostics;
using System.Text;
using DbDataKit.Authoring;

namespace DbDataKit.Authoring.Server;

/// <summary>
/// How the Workspace reaches a product's own program (plan 33 SC20c, SC22a): <c>dbdatabuild</c> for a Build project, <c>dbdatasync</c> for a Sync one. The Workspace does not reference a product's assemblies (a product is not a shared library): it runs the program as a child process, offline, in the draft's folder and
/// with a cleared environment (a draft is content a person wrote; the host's variables, secrets among them, are not for it), and reads what the command prints. The program is found at <paramref name="executable"/> when given,
/// else <paramref name="programName"/> (<c>.exe</c> on Windows) in the host's own folder or on <c>PATH</c>. <paramref name="extraArguments"/> go after every command's own.
/// </summary>
public class ChildProgram(string programName, string? executable, TimeSpan? timeout, string missing, IReadOnlyList<string> extraArguments)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private static readonly string[] KeptVariables = ["PATH", "HOME", "USERPROFILE", "TMPDIR", "TEMP", "TMP", "LANG", "DOTNET_ROOT", "SystemRoot"];

    /// <summary>The program's path, or null when there is none to run.</summary>
    public string? Find()
    {
        if (executable is { Length: > 0 }) return File.Exists(executable) ? executable : null;
        var name = OperatingSystem.IsWindows() ? programName + ".exe" : programName;
        var own = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(own)) return own;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            try { if (File.Exists(Path.Combine(folder, name))) return Path.Combine(folder, name); }
            catch (ArgumentException) { /* a bad entry of PATH */ }
        return null;
    }

    /// <summary>Runs <c>&lt;program&gt; &lt;arguments&gt;</c> and the extra arguments with <paramref name="workingFolder"/> as its folder; the exit code, what it printed and what it said on its error output.</summary>
    public async Task<(int Exit, string Output, string Error)> RunAsync(IReadOnlyList<string> arguments, string workingFolder, CancellationToken cancellationToken)
    {
        var program = Find() ?? throw new ProjectKindUnavailableException(missing);
        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = workingFolder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var kept = KeptVariables.Select(n => (n, v: Environment.GetEnvironmentVariable(n))).Where(p => p.v is not null).ToList();
        start.Environment.Clear();
        foreach (var (name, value) in kept) start.Environment[name] = value!;
        foreach (var a in arguments) start.ArgumentList.Add(a);
        foreach (var a in extraArguments) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new ProjectKindUnavailableException($"The {programName} process did not start.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? DefaultTimeout);
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* it ended as we looked */ }
            await process.WaitForExitAsync(CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            return (124, "", $"{programName} ran longer than this server allows and was stopped.");
        }
        return (process.ExitCode, await output, await error);
    }
}
