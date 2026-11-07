# DbDataKit.Runner

The runner's engine (plan 28, slices R05 onward of plan 28a). So far: the **artifact cache**, which materializes a published version ("what runs is what was approved", decision DR13).

A version is a list of files (`ArtifactFile`: path, SHA-256, size, media type, payload id). An `IArtifactSource` lists them and opens one as a stream; `PayloadArtifactSource` is the implementation over `PayloadStore` (plan 29 section 3.6), and plan 30's `version_artifact` table can implement the same interface later.

```csharp
var cache = new ArtifactCache("/var/lib/dbdatarunner/cache", source);
using var view = await cache.MaterializeAsync("orders-sync@7", files, ct);   // or without files: the source lists them
// view.Directory is this run's read-only copy; view.PathOf("bin/Orders.dll") a file in it
await cache.EvictAsync(hashesOfEveryServedVersion, ct);                        // removes the rest, never what a live view holds
```

- **Objects by hash.** `<dir>/objects/<first two hex>/<sha256>`. A file is streamed to `<dir>/tmp/<guid>.part` while it is hashed (one 128 KiB buffer, whatever the size), checked against the descriptor, made read-only and renamed into place. A wrong hash is refused with the path and both hashes and leaves neither an object nor a partial file. Two materializations of one hash, in one process or two, leave one object.
- **A view per run.** `<dir>/views/<version>-<id>/`, laid out by the artifact paths. On Linux and macOS its files are symbolic links to the objects (copies with `ArtifactLinkMode.Copy`, and wherever linking is not allowed), the objects are mode 0444 and the view's directories 0555, so a task running as the same user can neither write a file nor add, delete or replace one. On Windows files get the read-only attribute (directories too, which stops less there). Disposing the view removes it.
- **Paths are checked before anything is fetched.** `..`, `.`, empty segments, absolute paths, drive letters, backslashes, reserved device names (`CON`, `NUL`, `LPT1`...), characters Windows refuses, a duplicate (also one that differs only in case), and a file that is also a directory are refused with a message naming the path.
- **Eviction** takes the set of hashes still needed. A hash held by a live view of this process stays (the result counts them); a second process using the same directory is not protected.
