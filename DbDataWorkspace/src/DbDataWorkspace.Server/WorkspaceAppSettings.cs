using DbDataKit.Apps.Bundling;
using DbDataKit.Config.Yaml;

namespace DbDataWorkspace.Server;

/// <summary>
/// How a Workspace server builds an app (plan 22a GP35): the <c>apps:</c> section of its <c>server.yaml</c>, the same keys the Gateway reads for <c>apps build</c>. A build always runs from a vendored package store (plan 22a GP33, QG12): the
/// store must exist on this machine, and a server without one refuses to build and says what to set.
/// <code>
/// apps:
///   packageStore: /var/lib/dbdataworkspace/package-store   # made by `node DbDataKit/web/app-store/build.mjs --out ...` on a machine with a network
///   allowedPackages: [left-pad]                             # further packages an app may import; they must be in the store
///   packageStoreSha256: 3f2a...                              # optional: the digest `apps store-verify` prints
///   isolation: required                                      # required (default): no network namespace, no build; auto: build anyway and say so
///   buildTimeoutSeconds: 300
/// </code>
/// </summary>
public sealed record WorkspaceAppSettings(string? PackageStore = null, IReadOnlyList<string>? AllowedPackages = null, string? PackageStoreSha256 = null, NetworkIsolation Isolation = NetworkIsolation.Required, int BuildTimeoutSeconds = 300, string? Bun = null)
{
    public static readonly IReadOnlyList<string> Keys = ["packageStore", "allowedPackages", "packageStoreSha256", "isolation", "buildTimeoutSeconds", "bun"];

    public IReadOnlyList<string> Allowed => AllowedPackages ?? [];

    /// <summary>The settings of <c>server.yaml</c> in <paramref name="directory"/>; the defaults when the file or the section is absent. A key that is not a setting, or a value that is not one, is refused naming the file and the line.</summary>
    public static WorkspaceAppSettings Load(string directory)
    {
        var path = Path.Combine(directory, "server.yaml");
        if (!File.Exists(path)) return new();
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(File.ReadAllText(path), path, diagnostics);
        if (diagnostics.FirstOrDefault(d => d.Severity == ConfigSeverity.Error) is { } error) throw new InvalidOperationException($"{path}: the file is not valid YAML ({error.Message}, line {error.Line}).");
        if (root is not YamlMapping top || top.GetEntry("apps") is not { } entry) return new();
        if (entry.Value is not YamlMapping section) throw new InvalidOperationException($"{path}, line {entry.Key.Line}: apps is a set of settings ({string.Join(", ", Keys)}).");

        string? Scalar(string key) => section.Get(key) is YamlScalar { Value.Length: > 0 } s ? s.Value : null;
        foreach (var e in section.Entries)
            if (!Keys.Contains(e.Key.Value)) throw new InvalidOperationException($"{path}, line {e.Key.Line}: apps.{e.Key.Value} is not a setting; the settings are {string.Join(", ", Keys)}.");
        var allowed = section.Get("allowedPackages") switch
        {
            null => null,
            YamlSequence list => list.Items.Select(i => i is YamlScalar s ? s.Value : throw new InvalidOperationException($"{path}, line {i.Line}: apps.allowedPackages lists package names.")).ToList(),
            YamlScalar { Value.Length: 0 } => null,
            var other => throw new InvalidOperationException($"{path}, line {other.Line}: apps.allowedPackages is a list of package names."),
        };
        var isolation = Scalar("isolation") switch
        {
            null or "required" => NetworkIsolation.Required,
            "auto" => NetworkIsolation.IfAvailable,
            var other => throw new InvalidOperationException($"{path}: apps.isolation is required or auto, not '{other}'."),
        };
        var seconds = 300;
        if (Scalar("buildTimeoutSeconds") is { } text && (!int.TryParse(text, out seconds) || seconds is < 5 or > 3600))
            throw new InvalidOperationException($"{path}: apps.buildTimeoutSeconds is a number of seconds from 5 to 3600, not '{text}'.");
        return new WorkspaceAppSettings(Scalar("packageStore"), allowed, Scalar("packageStoreSha256"), isolation, seconds, Scalar("bun"));
    }

    /// <summary>What a build needs and whether this machine has it, in words a person can act on: the store, Bun, and how isolated a build is.</summary>
    public WorkspaceBuildReadiness Check()
    {
        var problems = new List<string>();
        VendoredPackageStore? store = null;
        if (PackageStore is not { Length: > 0 })
            problems.Add("No package store is set. A Workspace server builds an app only from a vendored package store (it installs nothing and reaches no network): make one on a machine with a network with `node DbDataKit/web/app-store/build.mjs --out <folder>`, copy it to this machine and set apps.packageStore in server.yaml to its folder.");
        else
        {
            var folder = Path.GetFullPath(PackageStore);
            if (!Directory.Exists(folder)) problems.Add($"The package store {folder} (apps.packageStore in server.yaml) does not exist on this machine. Make it with `node DbDataKit/web/app-store/build.mjs --out <folder>` and copy it here.");
            else
            {
                try { store = VendoredPackageStore.Load(folder, PackageStoreSha256); }
                catch (VendoredStoreException ex) { problems.Add(ex.Message); }
            }
        }
        var bun = BunLocator.Locate(Bun);
        if (!bun.Found) problems.Add(bun.Problem!);
        if (Isolation == NetworkIsolation.Required && !NetworkIsolator.IsAvailable)
            problems.Add($"Builds are set to run with no network (apps.isolation: required), and {NetworkIsolator.WhyNot}. Allow user namespaces, or set apps.isolation to auto in server.yaml to build anyway (the build then says it was not isolated).");
        return new WorkspaceBuildReadiness(problems, store, bun.Found ? bun.Path : null, bun.Source, Isolation == NetworkIsolation.Required ? "required" : "auto", NetworkIsolator.IsAvailable);
    }
}

/// <summary>Whether a Workspace server can build: <paramref name="Problems"/> is empty when it can.</summary>
public sealed record WorkspaceBuildReadiness(IReadOnlyList<string> Problems, VendoredPackageStore? Store, string? BunPath, string? BunSource, string Isolation, bool CanIsolate)
{
    public bool Ready => Problems.Count == 0;
}
