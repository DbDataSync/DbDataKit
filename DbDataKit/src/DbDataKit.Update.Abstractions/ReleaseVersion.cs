using System.Text.RegularExpressions;

namespace DbDataKit.Update;

/// <summary>Release channel. Ordering is by "how unstable": Stable &lt; Beta &lt; Snapshot.</summary>
public enum Channel { Stable = 0, Beta = 1, Snapshot = 2 }

/// <summary>Semantic version (semver 2.0 precedence) with channel inference from the pre-release tag.</summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private static readonly Regex Pattern = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? PreRelease { get; }
    public string? Build { get; }

    private ReleaseVersion(int major, int minor, int patch, string? pre, string? build)
        => (Major, Minor, Patch, PreRelease, Build) = (major, minor, patch, pre, build);

    /// <summary>Stable when there is no pre-release tag; Beta for tags starting "beta"/"rc"/"preview"; otherwise Snapshot.</summary>
    public Channel Channel
    {
        get
        {
            if (PreRelease is null) return Channel.Stable;
            var head = PreRelease.Split('.')[0];
            return head.StartsWith("beta", StringComparison.OrdinalIgnoreCase)
                || head.StartsWith("rc", StringComparison.OrdinalIgnoreCase)
                || head.StartsWith("preview", StringComparison.OrdinalIgnoreCase)
                ? Channel.Beta : Channel.Snapshot;
        }
    }

    public static ReleaseVersion Parse(string text)
        => TryParse(text, out var v) ? v : throw new FormatException($"Not a semantic version: '{text}'.");

    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ReleaseVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.Length > 0 && (t[0] == 'v' || t[0] == 'V')) t = t[1..];
        var m = Pattern.Match(t);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, out var ma) || !int.TryParse(m.Groups[2].Value, out var mi) || !int.TryParse(m.Groups[3].Value, out var pa))
            return false;
        version = new ReleaseVersion(ma, mi, pa,
            m.Groups[4].Success ? m.Groups[4].Value : null,
            m.Groups[5].Success ? m.Groups[5].Value : null);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;
        var a = PreRelease.Split('.');
        var b = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = ulong.TryParse(a[i], out var x);
            var bn = ulong.TryParse(b[i], out var y);
            c = (an, bn) switch
            {
                (true, true) => x.CompareTo(y),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (c != 0) return Math.Sign(c);
        }
        return a.Length.CompareTo(b.Length);
    }

    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is ReleaseVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (PreRelease is null ? "" : "-" + PreRelease) + (Build is null ? "" : "+" + Build);

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;
}
