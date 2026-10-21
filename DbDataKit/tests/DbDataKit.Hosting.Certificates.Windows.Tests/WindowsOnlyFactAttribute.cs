using System.Runtime.CompilerServices;

namespace DbDataKit.Hosting.Certificates.Windows.Tests;

/// <summary>A <see cref="FactAttribute"/> that reports itself skipped on any platform but Windows, rather than failing there.</summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only — exercises the real certificate store/DPAPI, not available on this platform.";
    }
}
