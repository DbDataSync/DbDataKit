using System.Collections;
using System.Text;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Config.Hosting;

public enum OriginKind { Default, File, Environment, CommandLine }

/// <summary>One effective value with its source. <see cref="Value"/> is <see cref="ShowOrigin.Mask"/> when the key is a <c>[Secret]</c>.</summary>
public sealed record OriginEntry(string Path, string Value, string Source, OriginKind Kind, int Line, bool Secret)
{
    public override string ToString() => $"{Path} = {Value}   ({(Line > 0 ? $"{Source}:{Line}" : Source)})";
}

/// <summary>Data behind <c>config show --origin</c>: every effective scalar with its file:line, env var, CLI flag or default, secrets masked.</summary>
public static class ShowOrigin
{
    public const string Mask = "********";

    public static IReadOnlyList<OriginEntry> From<T>(ConfigResult<T> result) where T : class
    {
        var entries = new List<OriginEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, file, line, value) in result.Merged.Origins())
        {
            if (path.Length == 0) continue;
            seen.Add(path);
            var secret = IsSecret(result.Schema, path);
            var kind = file.StartsWith("env:", StringComparison.Ordinal) ? OriginKind.Environment : file.StartsWith("cli:", StringComparison.Ordinal) ? OriginKind.CommandLine : file == "defaults" ? OriginKind.Default : OriginKind.File;
            entries.Add(new OriginEntry(path, secret ? Mask : value, file, kind, line, secret));
        }
        if (result.Value != null) AddBuiltInDefaults(result.Schema, result.Value, "", seen, entries);
        return entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
    }

    public static string Format(IEnumerable<OriginEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries) sb.Append(e).Append('\n');
        return sb.ToString();
    }

    private static void AddBuiltInDefaults(ObjectSchema o, object value, string path, HashSet<string> seen, List<OriginEntry> into)
    {
        foreach (var p in o.Properties)
        {
            var child = path.Length == 0 ? p.Key : path + "." + p.Key;
            var v = p.Property.GetValue(value);
            if (v == null) continue;
            if (p.Type is ObjectSchema nested) { AddBuiltInDefaults(nested, v, child, seen, into); continue; }
            if (p.Type is not ScalarSchema || seen.Contains(child)) continue;
            into.Add(new OriginEntry(child, p.Secret ? Mask : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "", "default", OriginKind.Default, 0, p.Secret));
        }
    }

    /// <summary>True when the path resolves to a <c>[Secret]</c> property (indexes and map keys are skipped over).</summary>
    public static bool IsSecret(ObjectSchema root, string path)
    {
        TypeSchema? cur = root;
        var secret = false;
        foreach (var seg in CommandLineSource.ParsePath(path))
        {
            switch (cur)
            {
                case ObjectSchema o:
                    var p = o.Find(seg);
                    if (p == null) return false;
                    secret |= p.Secret;
                    cur = p.Type;
                    break;
                case ListSchema l: cur = l.Item; break;
                case MapSchema m: cur = m.Value; break;
                case UnionSchema u:
                    // pick the first case that has the key
                    var c = u.Cases.Values.FirstOrDefault(x => x.Find(seg) != null);
                    var q = c?.Find(seg);
                    if (q == null) return false;
                    secret |= q.Secret;
                    cur = q.Type;
                    break;
                default: return secret;
            }
        }
        return secret;
    }
}
