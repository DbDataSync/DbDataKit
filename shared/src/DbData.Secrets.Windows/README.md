# DbData.Secrets.Windows

DPAPI store. `AclHelper` now lives in `DbData.Security.Acl` (namespace `DbData.Security.Acl`, referenced by this package) and is shared with `DbData.Hosting.WindowsService`.

## Public contract
- `DpapiProtector.Protect/Unprotect/ToInlineRef` (Windows only, `[SupportedOSPlatform("windows")]`).
- `DpapiSecretStore(directory, entropy, scope, allowedIdentities?)`: `ISecretStore` + `ISecretProvider`.
  One `<name with ':' -> '~'>.secret` file per secret; handles `secret:dbdata:...` names and inline `dpapi:<base64>`.
  Throws `PlatformNotSupportedException` off Windows. With `allowedIdentities` the directory and each file are ACL-restricted.
- `AclHelper.Restrict(path, identities, unixMode?)`, `EnsureRestrictedDirectory`, `WriteAllBytesRestricted`
  (create + restrict before writing content), `Verify(path, identities, recurse, unixMode?)` -> `AclReport`
  (`InSync`, `Drift` list), `ResolveIdentity`.
  - Windows: protected DACL (inheritance off), explicit FullControl ACEs for the identities (SIDs, `DOMAIN\user`,
    `NT SERVICE\x`, aliases `SYSTEM`, `Administrators`, `CurrentUser`). Directories propagate to children.
  - Unix: 0600 file / 0700 directory; identities ignored.

## Decisions and caveats
- LocalMachine scope is not a boundary between processes on one host: anyone with the entropy who can read the file can
  decrypt. The ACL is the control. Per-product entropy only prevents accidental cross-product reads.
- `Restrict` to a set excluding the caller removes the caller's access (owner can still re-grant).
- `Verify` ignores Deny ACEs and only judges Allow grants; recursion skips the inheritance check on children.
- Tests are guarded with `Assert.SkipUnless(OperatingSystem.IsWindows())`; the Unix branch test is skipped on Windows
  and has not been executed yet.
