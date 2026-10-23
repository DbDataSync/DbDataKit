using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;

// Register the resolver for your specific assembly
NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (libraryName, assembly, searchPath) =>
{
    // Check if the requested library is your native dependency
    if (libraryName == "YourNativeLibraryName")
    {
        // Construct the path to the runtimes folder
        string rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" :
                     RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux-x64" : "osx-x64";

        string extension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".dll" :
                           RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? ".so" : ".dylib";

        string baseDir = AppContext.BaseDirectory;
        string nativeLibPath = Path.Combine(baseDir, "runtimes", rid, "native", libraryName + extension);

        if (File.Exists(nativeLibPath))
        {
            return NativeLibrary.Load(nativeLibPath);
        }
    }

    // Fallback to default loading behavior
    return IntPtr.Zero;
});
