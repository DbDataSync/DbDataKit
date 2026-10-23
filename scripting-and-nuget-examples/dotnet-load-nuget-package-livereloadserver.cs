//https://weblog.west-wind.com/posts/2025/Jun/09/Adding-Runtime-NuGet-Package-Loading-to-an-Application#integrating-nuget

//dotnet add package Nuget.Protocol
public record class NugetPackageDependency(string PackageId, string Version);
public record class PackageConfiguration(
    List<NugetPackageDependency> Packages,
    List<string> NugetSources
);
/*
{
    "Packages": [
        {
            "packageId": "Westwind.Ai",
            "version": "0.2.7"
        },
        {
            "packageId": "Humanizer.Core",
            "version": "2.14.1"
        }
    ],
    "Sources": [
        "https://api.nuget.org/v3/index.json"
    ]
}
*/
async Task DoLoad(NuGetPackageLoader nuget, IMvcBuilder mvcBuilder, PackageConfiguration packageConfiguration){

    foreach (var package in packageConfiguration.Packages)
    {
        try
        {
            await nuget.LoadPackageAsync(package.PackageId,
                package.Version,
                mvcBuilder,
                packageConfiguration.NugetSources,
                LoadedNugetPackages, FailedNugetPackages); // lists
        }
        catch
        {
            FailedNugetPackages.Add(package.PackageId);
        }
    }
}

public class NuGetPackageLoader
{
    private readonly string _packagesFolder;

    public NuGetPackageLoader(string packagesFolder)
    {
        _packagesFolder = packagesFolder;
    }

    public async Task LoadPackageAsync(string packageId, string version, IMvcBuilder builder,
                IList<string> packageSources = null,
                IList<string> sucessPackages = null,
                IList<string> failedPackages = null)
    {
        sucessPackages = sucessPackages ?? new List<string>();
        failedPackages = failedPackages ?? new List<string>();

        var logger = NullLogger.Instance;
        var cache = new SourceCacheContext();
        var repositories = Repository.Provider.GetCoreV3(); // gets the default nuget.org store

        var packageVersion = new NuGetVersion(version);
        var packagePath = Path.Combine(_packagesFolder, packageId, version);
        Directory.CreateDirectory(packagePath);

        using var packageStream = new MemoryStream();

        FindPackageByIdResource resource = null;

        foreach (var source in packageSources)
        {
            if (!source.StartsWith("http"))
            {
                // local packages
                var sourceRepository = Repository.Factory.GetCoreV3(source); // local path
                try
                {
                    resource = await sourceRepository.GetResourceAsync<FindPackageByIdResource>();
                }
                catch { continue; }
            }
            else
            {
                // only packages
                var packageSource = new PackageSource(source);
                var sourceRepository = new SourceRepository(packageSource, repositories);
                try
                {
                    resource = await sourceRepository.GetResourceAsync<FindPackageByIdResource>();
                }
                catch { continue; }
            }
            if (await resource.CopyNupkgToStreamAsync(packageId, packageVersion, packageStream, cache, logger, default))
            {
                packageStream.Seek(0, SeekOrigin.Begin);
                break;
            }

            failedPackages.Add(packageId);
            return;
        }

        if (packageStream == null || packageStream.Length < 1)
        {
            failedPackages.Add(packageId);
            return;
        }

        var packageReader = new PackageArchiveReader(packageStream);

        // find the highest compatible framework
        string framework = GetTargetFramework(packageReader);

        // load dependencies
        var dependencies = packageReader
                    .GetPackageDependencies()
                    .Where(d => d.TargetFramework.ToString() == framework)
                    .ToList();

        foreach (var packRef in dependencies)
        {
            var pkg = packRef.Packages.FirstOrDefault();
            if (pkg?.Id == null) // framework references?
                continue;
            await LoadPackageAsync(pkg.Id, pkg.VersionRange.OriginalString, builder,packageSources, sucessPackages, failedPackages);
        }

        bool error = false;
        var files = packageReader.GetFiles().Where(f => f.Contains("/" + framework + "/"));
        foreach (var file in files)
        {
            var filePath = Path.Combine(packagePath, file.Replace("/", "\\"));
            if (!File.Exists(filePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                try
                {
                    using var fileStream = File.Create(filePath);
                    await packageReader.GetStream(file).CopyToAsync(fileStream);
                }
                catch { /* ignore - most likely the file exists already  */ }
            }

            if (filePath.EndsWith(".dll") && File.Exists(filePath))
            {
                try
                {
                    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(filePath);
                    if (assembly != null)
                    {
                        builder.AddApplicationPart(assembly);
                    }
                }
                catch
                {
                    error = true;
                    failedPackages.Add("-- " + Path.GetFileName(filePath));
                }
            }
        }

        if (error)
            failedPackages.Add(packageId);
        else
            sucessPackages.Add(packageId);
    }

    private string GetTargetFramework(PackageArchiveReader packageReader)
    {
        var frameworks = packageReader.GetReferenceItems();
        if (frameworks == null) return null;

        string framework = frameworks
            .Where(f => f.TargetFramework?.ToString().StartsWith("net") ?? false)
            .OrderByDescending(f=> f.TargetFramework.ToString())
            .Select(f => f.TargetFramework.ToString())
            .FirstOrDefault();

        if (framework == null)
            framework = frameworks
                            .FirstOrDefault(f => f.TargetFramework?.ToString().StartsWith("netstandard") ?? false)?
                            .TargetFramework.ToString();

        return framework;
    }
}
