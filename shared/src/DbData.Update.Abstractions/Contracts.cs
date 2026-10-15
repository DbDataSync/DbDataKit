namespace DbData.Update;

/// <summary>How aggressively an install may update itself. Default for products is Disabled or Notify.</summary>
public enum UpdateMode { Disabled, Notify, Approve, Auto }

/// <summary>Daily maintenance window for <see cref="UpdateMode.Auto"/>. Start==End means always open. Wraps past midnight.</summary>
public sealed record UpdateWindow(TimeOnly Start, TimeOnly End, IReadOnlyList<DayOfWeek>? Days = null)
{
    public bool Contains(DateTimeOffset localNow)
    {
        if (Days is { Count: > 0 } && !Days.Contains(localNow.DayOfWeek)) return false;
        var t = TimeOnly.FromDateTime(localNow.DateTime);
        if (Start == End) return true;
        return Start < End ? t >= Start && t < End : t >= Start || t < End;
    }
}

/// <summary>A release listed by a source, before its manifest has been fetched or verified.</summary>
public sealed record ReleaseRef(string Version, Uri? ManifestLocation, Uri? SignatureLocation, string SourceName)
{
    /// <summary>True when the source offers no signed manifest (e.g. NuGet flat container): informational only.</summary>
    public bool IsUnsignedHint => ManifestLocation is null;
}

/// <summary>Reads bytes from some location (http(s), file). Resumable via offset.</summary>
public interface IContentFetcher
{
    Task<Stream> OpenReadAsync(Uri location, long offset, CancellationToken ct);
}

public interface IReleaseSource
{
    string Name { get; }
    Task<IReadOnlyList<ReleaseRef>> ListAsync(CancellationToken ct);
}

public enum InstallMethod { Unknown, Scoop, DotnetTool, Archive, Container, ServiceSwap }

public sealed record UpdateApplyRequest(
    string Product, string ToolId, ReleaseVersion Current, ReleaseVersion Target,
    Channel Channel, string? DownloadUrl = null, string? ImageRef = null, bool Global = true);

public enum ApplyOutcome { Instructions, CommandReady, ManualRequired, NotSupported }

public sealed record CommandLine(string FileName, IReadOnlyList<string> Arguments)
{
    public override string ToString() => FileName + (Arguments.Count == 0 ? "" : " " + string.Join(' ', Arguments));
}

public sealed record ApplyResult(ApplyOutcome Outcome, string Message, CommandLine? Command = null);

/// <summary>Strategy for applying (or explaining how to apply) an update for one install method.</summary>
public interface IUpdateApplier
{
    InstallMethod Method { get; }
    ApplyResult Plan(UpdateApplyRequest request);
}
