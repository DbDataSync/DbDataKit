using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace DbDataConsole.Servers.Tests;

/// <summary>A program of the suite started from its built output (<c>dotnet exec &lt;dll&gt;</c>), with what it printed kept so a failure can show it.</summary>
internal sealed class RealProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly StringBuilder output = new();

    private RealProcess(Process process) => this.process = process;

    public string Output { get { lock (output) return output.ToString(); } }

    public static RealProcess Start(string dll, IEnumerable<string> arguments, string? input = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add(dll);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) info.Environment[name] = value;
        var process = new Process { StartInfo = info };
        var started = new RealProcess(process);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (started.output) started.output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (started.output) started.output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (input is not null) { process.StandardInput.Write(input); process.StandardInput.Close(); }
        return started;
    }

    /// <summary>Runs a command of the program to its end and returns what it printed; a command that does not exit 0 fails the test with its output.</summary>
    public static async Task<string> RunAsync(string dll, params string[] arguments) => await RunWithInputAsync(dll, null, arguments);

    public static async Task<string> RunWithInputAsync(string dll, string? input, params string[] arguments)
    {
        await using var run = Start(dll, arguments, input);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await run.process.WaitForExitAsync(timeout.Token);
        run.process.WaitForExit();   // lets the output readers finish
        Assert.True(run.process.ExitCode == 0, $"`{string.Join(' ', arguments)}` exited {run.process.ExitCode}:\n{run.Output}");
        return run.Output;
    }

    /// <summary>Waits until the address answers (a server's health), or fails with what the program printed.</summary>
    public async Task WaitUntilAsync(Uri url, TimeSpan timeout)
    {
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (process.HasExited) Assert.Fail($"The server exited with {process.ExitCode} before it answered at {url}:\n{Output}");
            try { if ((await http.GetAsync(url)).IsSuccessStatusCode) return; }
            catch (HttpRequestException) { }
            await Task.Delay(250);
        }
        Assert.Fail($"The server did not answer at {url} within {timeout.TotalSeconds:0} s:\n{Output}");
    }

    public ValueTask DisposeAsync()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        process.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Where the programs are and what they need: found from this assembly's own place, so the same test runs from any folder.</summary>
internal static class Programs
{
    public static string RepositoryRoot { get; } = FindRoot();

    private static string Configuration => new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Parent!.Name;   // .../bin/<Configuration>/net10.0/

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DbDataKit.slnx"))) return directory.FullName;
        throw new InvalidOperationException("The repository root (the folder with DbDataKit.slnx) was not found above the test's own folder.");
    }

    private static string Built(string project, string dll)
    {
        var path = Path.Combine(RepositoryRoot, project, "bin", Configuration, "net10.0", dll);
        // not a skip: the test needs the real program, and says which build makes it
        Assert.True(File.Exists(path), $"{path} is not built. Build it first: dotnet build {project} -c {Configuration}");
        return path;
    }

    public static string Build => Built("DbDataBuild/src/DbDataBuild.Cli", "dbdatabuild.dll");

    public static string Proxy => Built("DbDataProxy/src/DbDataProxy.Server", "dbdataproxy.dll");

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The code a <c>server pair</c> command printed.</summary>
    public static string CodeIn(string output) => Regex.Match(output, @"Pairing code: (\S+)").Groups[1].Value is { Length: > 0 } code ? code : throw new InvalidOperationException($"No pairing code in:\n{output}");
}
