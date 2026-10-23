using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

public class NugetPluginLoader
{
    public static async Task<Assembly> DownloadAndLoadPackageAsync(string packageId, string version)
    {
        string downloadPath = Path.Combine(Path.GetTempPath(), "DownloadedNugets", packageId, version);
        Directory.CreateDirectory(downloadPath);

        // 1. Define the NuGet Source Repository (Default Nuget.org)
        ILogger logger = NullLogger.Instance;
        CancellationToken cancellationToken = CancellationToken.None;
        SourceRepository repository = Repository.Factory.GetCoreV3("https://nuget.org");

        // 2. Fetch the download resource
        DownloadResource downloadResource = await repository.GetResourceAsync<DownloadResource>();
        SourceCacheContext cacheContext = new SourceCacheContext();

        // 3. Download the package
        using (DownloadResourceResult downloadResult = await downloadResource.GetDownloadResourceResultAsync(
            new NuGet.Packaging.Core.PackageIdentity(packageId, NuGetVersion.Parse(version)),
            new PackageDownloadContext(cacheContext),
            Settings.LoadDefaultSettings(null),
            logger,
            cancellationToken))
        {
            // 4. Extract package files to target path
            var packageReader = downloadResult.PackageReader;
            var libFiles = await packageReader.GetLibItemsAsync(cancellationToken);

            // Note: In real production code, you would filter 'libFiles' to find the closest
            // matching Target Framework Moniker (TFM), e.g., "net8.0"
            var targetGroup = libFiles.FirstOrDefault(g => g.TargetFramework.GetShortFolderName().StartsWith("net"));

            string mainDllPath = "";
            if (targetGroup != null)
            {
                foreach (var file in targetGroup.Items)
                {
                    string targetFile = Path.Combine(downloadPath, Path.GetFileName(file));
                    using (var stream = await packageReader.GetStreamAsync(file, cancellationToken))
                    using (var fs = File.Create(targetFile))
                    {
                        await stream.CopyToAsync(fs);
                    }
                    if (Path.GetExtension(targetFile).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        mainDllPath = targetFile; // Simplification: assuming the principal DLL matches
                    }
                }
            }

            // 5. Load the Assembly into an isolated context
            if (!string.IsNullOrEmpty(mainDllPath))
            {
                var myContext = new AssemblyLoadContext("NugetPluginContext", isCollectible: true);
                return myContext.LoadFromAssemblyPath(mainDllPath);
            }
        }

        throw new FileNotFoundException("Could not extract a valid assembly from the package.");
    }
}
