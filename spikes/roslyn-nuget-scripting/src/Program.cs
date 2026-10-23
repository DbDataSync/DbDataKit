using RoslynNuGetScripting;

// spike <script.csx> [--cache DIR] [--source URL_OR_FOLDER]... [--allow ID]... [--pin id/version=SHA512]... [--unload-check]
var scripts = new List<string>();
var sources = new List<string>();
var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var pins = new Dictionary<string, string>();
string? cacheDir = null;
var unloadCheck = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--cache": cacheDir = args[++i]; break;
        case "--source": sources.Add(args[++i]); break;
        case "--allow": allow.Add(args[++i]); break;
        case "--pin": var kv = args[++i].Split('=', 2); pins[kv[0].ToLowerInvariant()] = kv[1]; break;
        case "--unload-check": unloadCheck = true; break;
        default: scripts.Add(args[i]); break;
    }
}
if (scripts.Count != 1) { Console.Error.WriteLine("usage: spike <script.csx> [--cache DIR] [--source URL_OR_FOLDER]... [--allow ID]... [--pin id/version=SHA512]... [--unload-check]"); return 2; }
if (sources.Count == 0) sources.Add("https://api.nuget.org/v3/index.json");
// A service account may have no profile, so the default is only a default; production passes an explicit folder.
cacheDir ??= Environment.GetEnvironmentVariable("SPIKE_CACHE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoslynNuGetScripting");

var store = new PackageStore(Path.Combine(cacheDir, "packages"));
var options = ScriptHost.Options(store, sources, line => Console.Error.WriteLine(line),
    allow.Count == 0 ? null : p => allow.Contains(p.Id), pins);
var host = new ScriptHost(options, Path.Combine(cacheDir, "compiled"));

try
{
    var started = DateTime.UtcNow;
    var run = await host.RunAsync(File.ReadAllText(scripts[0]), Path.GetFileNameWithoutExtension(scripts[0]));
    Console.WriteLine($"result: {run.Result}");
    Console.Error.WriteLine($"compiled from cache: {run.CompiledFromCache}; native libraries loaded: {(run.NativeLibraries.Count == 0 ? "(none by the context)" : string.Join(", ", run.NativeLibraries))}; {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
    if (unloadCheck)
        Console.WriteLine($"managed side unloaded: {ScriptHost.Collect(run.Context)}");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine("error: " + e.GetBaseException().Message);
    return 1;
}
