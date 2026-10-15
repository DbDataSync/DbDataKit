using System.Text;
using System.Text.RegularExpressions;

namespace DbDataKit.Hosting.Systemd;

public enum ImageFlavor
{
    /// <summary>ASP.NET runtime image only. The default.</summary>
    Runtime,

    /// <summary>SDK image (e.g. Sync's in-app library installs). Explicit opt-in, labelled as such in the file.</summary>
    Dev,
}

public sealed class DockerfileOptions
{
    /// <summary>Dll to run, e.g. <c>DbDataProxy.Server.dll</c>.</summary>
    public required string EntryAssembly { get; init; }

    /// <summary>Directory of the <c>dotnet publish</c> output relative to the build context.</summary>
    public string PublishDir { get; init; } = "publish";

    public ImageFlavor Flavor { get; init; } = ImageFlavor.Runtime;

    /// <summary>Base image reference. Pin with <c>@sha256:...</c> for reproducible builds.</summary>
    public string? BaseImage { get; init; }

    /// <summary>The numeric uid/gid to run as (never root). 1654 is the built-in <c>app</c> user of the .NET images.</summary>
    public int Uid { get; init; } = 1654;

    public int Port { get; init; } = 8080;

    public string HealthPath { get; init; } = "/health/live";

    public string DataDir { get; init; } = "/data";

    /// <summary>False for chiseled images, which have no shell for the mkdir/chown step.</summary>
    public bool HasShell { get; init; } = true;

    public const string DefaultRuntimeImage = "mcr.microsoft.com/dotnet/aspnet:10.0";
    public const string DefaultSdkImage = "mcr.microsoft.com/dotnet/sdk:10.0";
}

/// <summary>
/// Dockerfile template: non-root numeric user, HEALTHCHECK through the app's own <c>--healthcheck</c> probe
/// (see <c>HealthProbeClient</c>; no curl needed in the image), data volume. Deterministic LF output.
/// </summary>
public static partial class DockerfileGenerator
{
    public static string Generate(DockerfileOptions o)
    {
        if (o.Uid <= 0) throw new ArgumentException("The container must not run as root (uid > 0 required).");
        var image = o.BaseImage ?? (o.Flavor == ImageFlavor.Dev ? DockerfileOptions.DefaultSdkImage : DockerfileOptions.DefaultRuntimeImage);
        if (!ImageRef().IsMatch(image)) throw new ArgumentException($"Invalid image reference '{image}'.");
        foreach (var v in new[] { o.EntryAssembly, o.PublishDir, o.HealthPath, o.DataDir })
        {
            if (v.Any(c => char.IsControl(c) || c == '"' || c == '\\')) throw new ArgumentException($"Unsafe value '{v}'.");
        }

        var sb = new StringBuilder();
        void Line(string t = "") => sb.Append(t).Append('\n');

        if (o.Flavor == ImageFlavor.Dev)
        {
            Line("# DEV IMAGE: contains the .NET SDK. Do not use for production; build with the Runtime flavor.");
        }

        Line($"FROM {image}");
        Line("WORKDIR /app");
        Line($"COPY --chown={o.Uid}:{o.Uid} {o.PublishDir}/ /app/");
        if (o.HasShell)
        {
            Line($"RUN mkdir -p {o.DataDir} && chown {o.Uid}:{o.Uid} {o.DataDir}");
        }

        Line($"ENV ASPNETCORE_URLS=http://+:{o.Port} \\");
        Line($"    DOTNET_gcServer=0 \\");
        Line($"    DBDATA_DATA_DIR={o.DataDir}");
        Line($"VOLUME [\"{o.DataDir}\"]");
        Line($"EXPOSE {o.Port}");
        Line($"USER {o.Uid}:{o.Uid}");
        Line($"HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \\");
        Line($"  CMD [\"dotnet\", \"{o.EntryAssembly}\", \"--healthcheck\", \"http://127.0.0.1:{o.Port}{o.HealthPath}\"]");
        Line($"ENTRYPOINT [\"dotnet\", \"{o.EntryAssembly}\"]");
        return sb.ToString();
    }

    [GeneratedRegex(@"^[a-z0-9][a-zA-Z0-9._\-/:]*(@sha256:[0-9a-f]{64})?$")]
    private static partial Regex ImageRef();
}
