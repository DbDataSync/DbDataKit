using System.Security.AccessControl;
using System.Security.Principal;

namespace DbData.Secrets.Windows;

/// <summary>One deviation from the expected permissions.</summary>
public sealed record AclDrift(string Path, string Description);

/// <summary>Result of <see cref="AclHelper.Verify"/>.</summary>
public sealed record AclReport(bool InSync, IReadOnlyList<AclDrift> Drift);

/// <summary>
/// Restricts files and directories to a set of identities and reports drift. Cross-platform:
/// on Windows it replaces the DACL with explicit FullControl ACEs for the given identities and turns
/// inheritance off; on Unix it sets mode 0600 (file) / 0700 (directory) and ignores the identities
/// (the owning user is the only identity). Intended for PFX files, key files and secret blobs, and
/// reused by service hosting (Plan 03).
/// <para>Identities may be SIDs ("S-1-5-18"), "DOMAIN\user", "NT SERVICE\name", or the aliases
/// <c>SYSTEM</c>, <c>Administrators</c> and <c>CurrentUser</c>. Restricting to identities that exclude the
/// caller is allowed (the owner can always re-grant), but the caller then loses access.</para>
/// </summary>
public static class AclHelper
{
    public const UnixFileMode DefaultFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    public const UnixFileMode DefaultDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Resolves an identity string to a SID (Windows only).</summary>
    public static SecurityIdentifier ResolveIdentity(string identity)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Identity resolution is Windows-only.");
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        var id = identity.Trim();
        if (id.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return new SecurityIdentifier(id);
        switch (id.ToUpperInvariant())
        {
            case "SYSTEM" or "NT AUTHORITY\\SYSTEM": return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            case "ADMINISTRATORS" or "BUILTIN\\ADMINISTRATORS": return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            case "CURRENTUSER": return WindowsIdentity.GetCurrent().User!;
        }
        return (SecurityIdentifier)new NTAccount(id).Translate(typeof(SecurityIdentifier));
    }

    /// <summary>Restricts a file or directory. Directories propagate to children by inheritance on Windows.</summary>
    public static void Restrict(string path, IEnumerable<string> identities, UnixFileMode? unixMode = null)
    {
        var isDir = Directory.Exists(path);
        if (!isDir && !File.Exists(path)) throw new FileNotFoundException("Cannot restrict a path that does not exist.", path);

        if (OperatingSystem.IsWindows())
        {
            var sids = identities.Select(ResolveIdentity).Distinct().ToArray();
            if (sids.Length == 0) throw new ArgumentException("At least one identity is required.", nameof(identities));
            if (isDir)
            {
                var sec = new DirectorySecurity();
                sec.SetAccessRuleProtection(true, false);
                foreach (var sid in sids)
                    sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(path).SetAccessControl(sec);
            }
            else
            {
                var sec = new FileSecurity();
                sec.SetAccessRuleProtection(true, false);
                foreach (var sid in sids)
                    sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(sec);
            }
        }
        else
        {
            File.SetUnixFileMode(path, unixMode ?? (isDir ? DefaultDirectoryMode : DefaultFileMode));
        }
    }

    /// <summary>Creates a directory (if needed) restricted before any content is placed in it.</summary>
    public static void EnsureRestrictedDirectory(string path, IEnumerable<string> identities, UnixFileMode? unixMode = null)
    {
        if (!Directory.Exists(path))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
            else Directory.CreateDirectory(path, unixMode ?? DefaultDirectoryMode);
        }
        Restrict(path, identities, unixMode);
    }

    /// <summary>
    /// Writes a file that is never readable by anyone but the allowed identities: the (empty) file is
    /// created and restricted first, then the content is written.
    /// </summary>
    public static void WriteAllBytesRestricted(string path, ReadOnlySpan<byte> content, IEnumerable<string> identities, UnixFileMode? unixMode = null)
    {
        var ids = identities.ToArray();
        if (!OperatingSystem.IsWindows())
        {
            using var fs = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = unixMode ?? DefaultFileMode,
            });
            File.SetUnixFileMode(path, unixMode ?? DefaultFileMode); // umask / pre-existing file
            fs.Write(content);
            return;
        }
        if (!File.Exists(path)) File.Create(path).Dispose();
        Restrict(path, ids, unixMode);
        using var stream = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.None);
        stream.Write(content);
    }

    /// <summary>Checks the permissions against what <see cref="Restrict"/> would set; never modifies anything.</summary>
    public static AclReport Verify(string path, IEnumerable<string> identities, bool recurse = false, UnixFileMode? unixMode = null)
    {
        var drift = new List<AclDrift>();
        var ids = identities.ToArray();
        HashSet<SecurityIdentifier>? sids = null;
        if (OperatingSystem.IsWindows()) sids = ids.Select(ResolveIdentity).ToHashSet();
        VerifyOne(path, sids, unixMode, drift, checkProtection: true);
        if (recurse && Directory.Exists(path))
            foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                VerifyOne(child, sids, unixMode, drift, checkProtection: false);
        return new AclReport(drift.Count == 0, drift);
    }

    private static void VerifyOne(string path, HashSet<SecurityIdentifier>? expected, UnixFileMode? unixMode, List<AclDrift> drift, bool checkProtection)
    {
        var isDir = Directory.Exists(path);
        if (!isDir && !File.Exists(path)) { drift.Add(new(path, "path does not exist")); return; }

        if (!OperatingSystem.IsWindows())
        {
            var want = unixMode ?? (isDir ? DefaultDirectoryMode : DefaultFileMode);
            var have = File.GetUnixFileMode(path);
            if (have != want) drift.Add(new(path, $"mode is {Octal(have)}, expected {Octal(want)}"));
            return;
        }

        FileSystemSecurity sec = isDir ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                                       : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        if (checkProtection && !sec.AreAccessRulesProtected) drift.Add(new(path, "inheritance is enabled (parent permissions apply)"));
        var granted = new HashSet<SecurityIdentifier>();
        foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            granted.Add(sid);
            if (!expected!.Contains(sid)) drift.Add(new(path, $"unexpected grant to {Describe(sid)}"));
        }
        foreach (var sid in expected!)
            if (!granted.Contains(sid)) drift.Add(new(path, $"missing grant for {Describe(sid)}"));
    }

    private static string Octal(UnixFileMode m) => "0" + Convert.ToString((int)m, 8);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string Describe(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; } catch (IdentityNotMappedException) { return sid.Value; }
    }
}
