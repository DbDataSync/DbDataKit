using System.Text;

namespace DbDataKit.Update.Tools;

/// <summary>
/// <c>dbdata-update</c>: keys generate | keys rotate | manifest create | manifest sign | manifest verify.
/// The private key is a base64 32-byte ed25519 seed, read from <c>--key-file</c> or the env var <c>DBDATA_SIGNING_KEY</c>
/// (so CI can inject it from a protected environment secret without writing it to disk).
/// Exit codes: 0 ok, 1 verification/usage failure, 2 unexpected error.
/// </summary>
public static class UpdateToolCli
{
    public const string SigningKeyEnv = "DBDATA_SIGNING_KEY";

    private sealed class Opts
    {
        public Dictionary<string, List<string>> Values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string k) => Values.TryGetValue(k, out var v) ? v[^1] : null;
        public IReadOnlyList<string> All(string k) => Values.TryGetValue(k, out var v) ? v : [];
        public bool Flag(string k) => Values.ContainsKey(k);
        public string Require(string k) => Get(k) ?? throw new UsageException($"Missing required option --{k}.");
    }

    private sealed class UsageException(string m) : Exception(m);

    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "requires-config-migration", "revoked" };

    private static Opts Parse(string[] a, int from)
    {
        var o = new Opts();
        for (var i = from; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--")) throw new UsageException($"Unexpected argument '{a[i]}'.");
            var k = a[i][2..];
            string v;
            if (Flags.Contains(k)) v = "true";
            else if (++i < a.Length) v = a[i];
            else throw new UsageException($"Option --{k} needs a value.");
            if (!o.Values.TryGetValue(k, out var l)) o.Values[k] = l = new();
            l.Add(v);
        }
        return o;
    }

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, Func<string, string?> getEnv)
    {
        try
        {
            if (args.Length < 2) throw new UsageException("Usage: dbdata-update <keys|manifest> <generate|rotate|create|sign|verify> [options]");
            var o = Parse(args, 2);
            return (args[0].ToLowerInvariant(), args[1].ToLowerInvariant()) switch
            {
                ("keys", "generate") => KeysGenerate(o, stdout),
                ("keys", "rotate") => KeysRotate(o, stdout, getEnv),
                ("manifest", "create") => Create(o, stdout),
                ("manifest", "sign") => Sign(o, stdout, getEnv),
                ("manifest", "verify") => Verify(o, stdout, stderr),
                _ => throw new UsageException($"Unknown command '{args[0]} {args[1]}'."),
            };
        }
        catch (UsageException ex) { stderr.WriteLine(ex.Message); return 1; }
        catch (UpdateVerificationException ex) { stderr.WriteLine($"VERIFICATION FAILED ({ex.Failure}): {ex.Message}"); return 1; }
        catch (Exception ex) when (ex is FormatException or IOException or ArgumentException or UnauthorizedAccessException)
        { stderr.WriteLine("Error: " + ex.Message); return 1; }
    }

    private static byte[] LoadSeed(Opts o, Func<string, string?> getEnv)
    {
        var text = o.Get("key-file") is { } f ? File.ReadAllText(f) : getEnv(SigningKeyEnv)
            ?? throw new UsageException($"Provide --key-file or set {SigningKeyEnv}.");
        var seed = Convert.FromBase64String(text.Trim());
        if (seed.Length != Ed25519.KeySize) throw new UsageException("Signing key must be a base64 32-byte ed25519 seed.");
        return seed;
    }

    private static int KeysGenerate(Opts o, TextWriter stdout)
    {
        var (seed, pub) = Ed25519.GenerateKeyPair();
        var keyId = o.Require("key-id");
        var priv = o.Require("out-private");
        if (File.Exists(priv)) throw new UsageException($"Refusing to overwrite existing private key file '{priv}'.");
        File.WriteAllText(priv, Convert.ToBase64String(seed) + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(priv, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var pubB64 = Convert.ToBase64String(pub);
        if (o.Get("out-public") is { } p) File.WriteAllText(p, pubB64 + "\n");
        stdout.WriteLine($"key-id: {keyId}");
        stdout.WriteLine($"public-key: {pubB64}");
        return 0;
    }

    private static int KeysRotate(Opts o, TextWriter stdout, Func<string, string?> getEnv)
    {
        var keys = o.All("add").Select(s =>
        {
            var i = s.IndexOf('=');
            if (i <= 0) throw new UsageException("--add expects id=base64PublicKey.");
            return new TrustedKey(s[..i], Convert.FromBase64String(s[(i + 1)..]));
        }).ToList();
        var bytes = TrustStore.CreateKeyManifest(int.Parse(o.Require("sequence")), keys, o.All("retire"), LoadSeed(o, getEnv), o.Require("signer-id"));
        File.WriteAllBytes(o.Require("out"), bytes);
        stdout.WriteLine($"Wrote key manifest {o.Get("out")}");
        return 0;
    }

    private static int Create(Opts o, TextWriter stdout)
    {
        var schema = new Dictionary<string, int>();
        foreach (var s in o.All("schema-version"))
        {
            var i = s.IndexOf('=');
            if (i <= 0 || !int.TryParse(s[(i + 1)..], out var n)) throw new UsageException("--schema-version expects name=number.");
            schema[s[..i]] = n;
        }
        var m = ManifestBuilder.Build(new ManifestBuildOptions
        {
            Product = o.Require("product"), Version = o.Require("version"), Channel = o.Get("channel") ?? "",
            BaseUrl = o.Require("base-url"), MinUpdater = o.Get("min-updater"), RequiresConfigMigration = o.Flag("requires-config-migration"),
            SchemaVersions = schema, Supersedes = o.All("supersedes").ToList(), MinimumSafeVersion = o.Get("minimum-safe-version"),
            Revoked = o.Flag("revoked"), Notes = o.Get("notes"),
        }, o.Require("artifacts"));
        var outPath = o.Get("out") ?? Path.Combine(o.Require("artifacts"), "release.json");
        File.WriteAllBytes(outPath, ManifestBuilder.ToBytes(m));
        stdout.WriteLine($"Wrote {outPath}: {m.Product} {m.Version} ({m.Channel}), {m.Artifacts.Count} artifact(s).");
        return 0;
    }

    private static int Sign(Opts o, TextWriter stdout, Func<string, string?> getEnv)
    {
        var manifest = o.Require("manifest");
        var bytes = File.ReadAllBytes(manifest);
        ReleaseManifest.Parse(bytes); // never sign something that is not a valid manifest
        var seed = LoadSeed(o, getEnv);
        var text = ManifestSigner.Sign(bytes, seed, o.Require("key-id"));
        var sigPath = o.Get("out") ?? manifest + ".sig";
        File.WriteAllText(sigPath, text, new UTF8Encoding(false));
        stdout.WriteLine($"Wrote {sigPath} (public key {Convert.ToBase64String(Ed25519.PublicKeyFromSeed(seed))}).");
        return 0;
    }

    private static int Verify(Opts o, TextWriter stdout, TextWriter stderr)
    {
        var manifest = o.Require("manifest");
        var keys = o.All("trusted-key").Select(s =>
        {
            var i = s.IndexOf('=');
            if (i <= 0) throw new UsageException("--trusted-key expects id=base64PublicKey.");
            return new TrustedKey(s[..i], Convert.FromBase64String(s[(i + 1)..]));
        }).ToList();
        if (keys.Count == 0) throw new UsageException("Provide at least one --trusted-key id=base64.");
        var trust = new TrustStore(keys);
        var bytes = File.ReadAllBytes(manifest);
        var v = ReleaseVerifier.VerifyManifest(bytes, File.ReadAllText(o.Get("sig") ?? manifest + ".sig"), trust, o.Get("product"));
        // Optionally check artifacts sitting next to the manifest.
        var dir = o.Get("artifacts");
        if (dir is not null)
            foreach (var a in v.Manifest.Artifacts)
            {
                var file = Path.Combine(dir, a.Name ?? Path.GetFileName(a.Url));
                ReleaseVerifier.VerifyFileAsync(file, a).GetAwaiter().GetResult();
            }
        stdout.WriteLine($"OK: {v.Manifest.Product} {v.Manifest.Version} ({v.Manifest.Channel}) manifest-sha256={v.Sha256}");
        return 0;
    }
}
