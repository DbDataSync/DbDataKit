using System.Text.RegularExpressions;

namespace DbDataKit.Update.Detect;

internal static partial class Safe
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    public static partial Regex Id();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9./_-]{0,200}(:[A-Za-z0-9_][A-Za-z0-9._-]{0,127})?(@sha256:[0-9a-f]{64})?$")]
    public static partial Regex ImageRef();

    public static void RequireId(string id, string what)
    {
        if (!Id().IsMatch(id)) throw new ArgumentException($"{what} '{id}' contains characters that are not allowed.");
    }
}

/// <summary>Scoop or winget: only ever tells the user (or the host UI) which command to run; the package manager does the update.</summary>
public sealed class PackageManagerHint(InstallMethod method = InstallMethod.Scoop) : IUpdateApplier
{
    public InstallMethod Method { get; } = method is InstallMethod.Scoop or InstallMethod.Winget
        ? method : throw new ArgumentOutOfRangeException(nameof(method), "Scoop or Winget only.");

    public ApplyResult Plan(UpdateApplyRequest r)
    {
        Safe.RequireId(r.ToolId, "Tool id");
        var cmd = Method == InstallMethod.Scoop
            ? new CommandLine("scoop", ["update", r.ToolId])
            : new CommandLine("winget", ["upgrade", "--id", r.ToolId, "--exact"]);
        return new(ApplyOutcome.Instructions,
            $"{r.Product} {r.Target} is available (installed {r.Current}). Installed via {Method}; run: {cmd}", cmd);
    }
}

/// <summary>Builds <c>dotnet tool update</c> argument lists. Never runs anything and never goes through a shell.</summary>
public sealed class DotnetToolApplier : IUpdateApplier
{
    public InstallMethod Method => InstallMethod.DotnetTool;

    public ApplyResult Plan(UpdateApplyRequest r)
    {
        Safe.RequireId(r.ToolId, "Tool id");
        var args = new List<string> { "tool", "update" };
        if (r.Global) args.Add("-g");
        args.Add(r.ToolId);
        args.Add("--version");
        args.Add(r.Target.ToString());
        var cmd = new CommandLine("dotnet", args);
        return new(ApplyOutcome.CommandReady, $"Run: {cmd}", cmd);
    }
}

/// <summary>Archive/unknown installs: download-and-replace is a manual (or host-implemented, verified) step.</summary>
public sealed class ManualApplier : IUpdateApplier
{
    public InstallMethod Method => InstallMethod.Archive;

    public ApplyResult Plan(UpdateApplyRequest r)
    {
        var url = r.DownloadUrl is not null && Uri.TryCreate(r.DownloadUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u.AbsoluteUri : null;
        var where = url is null ? "the project's releases page" : url;
        return new(ApplyOutcome.ManualRequired,
            $"{r.Product} {r.Target} is available (installed {r.Current}). Download it from {where}, verify it against the signed release.json, and replace the existing files.");
    }
}

/// <summary>Containers are replaced, not patched: never offer an in-place update.</summary>
public sealed class ContainerNotice : IUpdateApplier
{
    public InstallMethod Method => InstallMethod.Container;

    public ApplyResult Plan(UpdateApplyRequest r)
    {
        if (r.ImageRef is not null && !Safe.ImageRef().IsMatch(r.ImageRef)) throw new ArgumentException("Image reference is not valid.");
        var tag = r.ImageRef is null ? "" : $" Pull the new image: {r.ImageRef.Split(':')[0]}:{r.Target}.";
        return new(ApplyOutcome.NotSupported,
            $"Running in a container: {r.Product} {r.Target} is available but containers are not updated in place.{tag} Redeploy with the new tag; keep the previous tag for rollback.");
    }
}

/// <summary>Service installs update through the privileged helper (DbDataKit.Update.Service), never from the CLI.</summary>
public sealed class ServiceSwapNotice : IUpdateApplier
{
    public InstallMethod Method => InstallMethod.ServiceSwap;

    public ApplyResult Plan(UpdateApplyRequest r)
        => new(ApplyOutcome.NotSupported, $"{r.Product} runs from a side-by-side service install; {r.Target} is applied by the service updater (Updates:Mode), not by this command.");
}

public static class UpdateApplierSelector
{
    public static IUpdateApplier For(InstallMethod method) => method switch
    {
        InstallMethod.Scoop => new PackageManagerHint(InstallMethod.Scoop),
        InstallMethod.Winget => new PackageManagerHint(InstallMethod.Winget),
        InstallMethod.DotnetTool => new DotnetToolApplier(),
        InstallMethod.Container => new ContainerNotice(),
        InstallMethod.ServiceSwap => new ServiceSwapNotice(),
        _ => new ManualApplier(),
    };

    public static ApplyResult PlanFor(InstallEnvironment env, UpdateApplyRequest request)
        => For(InstallMethodDetector.Detect(env).Method).Plan(request);
}
