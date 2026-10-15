using System.Globalization;
using System.Text;
using DbData.Config.Yaml;

namespace DbData.Config;

/// <summary>A document being migrated: text plus surgical edits (comments and ordering survive). Every edit re-parses; a failed edit throws <see cref="MigrationException"/>.</summary>
public sealed class YamlDocument
{
    private readonly IDiagnosticCatalog _catalog;
    private readonly string _file;

    public YamlDocument(string text, string file, IDiagnosticCatalog? catalog = null)
    {
        Text = text;
        _file = file;
        _catalog = catalog ?? DefaultDiagnosticCatalog.Instance;
    }

    public string Text { get; private set; }

    public YamlNode? Root
    {
        get
        {
            var d = new List<ConfigDiagnostic>();
            return StrictYamlReader.Read(Text, _file, d, _catalog);
        }
    }

    /// <summary>The scalar at a path (<c>a.b[0].c</c>), or null.</summary>
    public string? Get(string path) => Find(path) is YamlScalar s ? s.Value : null;

    public YamlNode? Find(string path)
    {
        var node = Root;
        foreach (var seg in CommandLineSource.ParsePath(path))
        {
            node = node switch
            {
                YamlMapping m => m.Get(seg),
                YamlSequence q when int.TryParse(seg, out var i) && i >= 0 && i < q.Items.Count => q.Items[i],
                _ => null,
            };
            if (node == null) return null;
        }
        return node;
    }

    public bool Has(string path) => Find(path) != null;

    public void Set(string path, YamlValue value) => Apply(YamlEditor.Set(Text, path, value, _file, _catalog));
    public void Remove(string path) => Apply(YamlEditor.Remove(Text, path, _file, _catalog));
    public void Insert(string path, YamlValue value, int? index = null) => Apply(YamlEditor.Insert(Text, path, value, index, _file, _catalog));

    /// <summary>Renames a key in place (same position, comments and value kept). The new name must not exist.</summary>
    public void RenameKey(string path, string newName)
    {
        if (Find(path) == null) throw new MigrationException($"`{path}` was not found.");
        var segs = CommandLineSource.ParsePath(path);
        var parent = segs.Count == 1 ? Root : Find(string.Join('.', segs.Take(segs.Count - 1)));
        if (parent is not YamlMapping pm) throw new MigrationException($"`{path}` is not a mapping key.");
        var entry = pm.GetEntry(segs[^1])!;
        if (pm.GetEntry(newName) != null) throw new MigrationException($"`{newName}` already exists next to `{segs[^1]}`.");
        Apply(new EditResult(Text[..entry.Key.Start] + YamlText.Scalar(newName) + Text[entry.Key.End..], null));
    }

    internal void SetVersion(string key, int version, string? headerKind = null)
    {
        var value = headerKind is null ? version.ToString(CultureInfo.InvariantCulture) : SchemaHeader.Format(headerKind, version);
        if (Has(key)) { Set(key, YamlValue.Raw(value)); return; }
        // new key goes first, below any leading comment block, so the file's header stays on top
        var nl = Text.Contains("\r\n") ? "\r\n" : "\n";
        var at = 0;
        foreach (var line in Text.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.Length == 0 || t[0] == '#') at += line.Length + 1; else break;
        }
        at = Math.Min(at, Text.Length);
        var insert = $"{key}: {value}{nl}";
        if (at > 0 && at == Text.Length && !Text.EndsWith('\n')) insert = nl + insert;
        Apply(new EditResult(Text[..at] + insert + Text[at..], null));
    }

    private void Apply(EditResult r)
    {
        if (r.Problem != null) throw new MigrationException(r.Problem.Message);
        var d = new List<ConfigDiagnostic>();
        StrictYamlReader.Read(r.Text!, _file, d, _catalog);
        if (d.HasErrors()) throw new MigrationException("The edit would produce an invalid document: " + d.First().Message);
        Text = r.Text!;
    }
}

public sealed class MigrationException(string message) : Exception(message);

public interface IMigrationLog
{
    void Info(string message);
    /// <summary>
    /// Asks a question when intent is ambiguous. Returns the answer, or null when none was supplied; an unanswered question
    /// fails the run (non-interactive runs never guess). The migration must then return without changing the document.
    /// </summary>
    string? Ask(string id, string question, IReadOnlyList<string>? options = null);
}

/// <summary>A pure step from one version of a file kind to the next. It edits the document; the engine writes the new <c>schemaVersion</c>.</summary>
public interface IConfigMigration
{
    string Kind { get; }
    int From { get; }
    int To { get; }
    void Apply(YamlDocument doc, IMigrationLog log);
}

public enum MigrationStatus { UpToDate, Migrated, Refused, Failed }

public sealed record MigrationQuestion(string Id, string Question, IReadOnlyList<string>? Options);

public sealed record MigrationResult(
    MigrationStatus Status, int FromVersion, int ToVersion, string OriginalText, string NewText, string Diff,
    IReadOnlyList<ConfigDiagnostic> Diagnostics, IReadOnlyList<string> Log, IReadOnlyList<MigrationQuestion> Questions)
{
    public bool Ok => Status is MigrationStatus.UpToDate or MigrationStatus.Migrated;
    public bool Changed => NewText != OriginalText;
}

public sealed record MigrationOptions
{
    /// <summary>Answers to migration questions by id.</summary>
    public IReadOnlyDictionary<string, string>? Answers { get; init; }
    public string VersionKey { get; init; } = "schemaVersion";
    /// <summary>Stop at this version instead of the latest known (default: the highest version reachable).</summary>
    public int? TargetVersion { get; init; }
    public IDiagnosticCatalog? Catalog { get; init; }
}

public sealed record ApplyResult(MigrationResult Migration, bool Written, string? BackupPath);

/// <summary>
/// Runs chained migrations (1 to 2 to 3) on a document. Modes: <see cref="Plan"/> (dry-run with a unified diff, writes nothing, also the in-memory
/// migrate-on-read), <see cref="Apply"/> (writes a <c>.bak</c> first). A file whose version is newer than the newest known is refused with an upgrade message.
/// A file without the version key is version 1. Nothing is ever migrated down: restore the backup instead.
/// </summary>
public sealed class MigrationEngine
{
    private readonly List<IConfigMigration> _migrations;

    public MigrationEngine(IEnumerable<IConfigMigration> migrations)
    {
        _migrations = [.. migrations];
        foreach (var g in _migrations.GroupBy(m => (m.Kind, m.From)).Where(g => g.Count() > 1))
            throw new ArgumentException($"Two migrations of `{g.Key.Kind}` start at version {g.Key.From}.");
        foreach (var m in _migrations.Where(m => m.To != m.From + 1))
            throw new ArgumentException($"Migration {m.Kind} {m.From}->{m.To} must advance exactly one version.");
    }

    /// <summary>The newest version this engine can produce for the kind (1 if it knows no migrations), at least <paramref name="declaredLatest"/>.</summary>
    public int LatestVersion(string kind, int declaredLatest = 1) => Math.Max(declaredLatest, _migrations.Where(m => m.Kind == kind).Select(m => m.To).DefaultIfEmpty(1).Max());

    public MigrationResult Plan(string kind, string text, string file, MigrationOptions? options = null)
    {
        options ??= new();
        var catalog = options.Catalog ?? DefaultDiagnosticCatalog.Instance;
        var diags = new List<ConfigDiagnostic>();
        var log = new Log(options.Answers);

        MigrationResult Result(MigrationStatus s, int from, int to, string now) =>
            new(s, from, to, text, now, UnifiedDiff.Create(text, now, file), diags, log.Lines, log.Pending);

        var parse = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(text, file, parse, catalog);
        if (parse.HasErrors()) { diags.AddRange(parse); return Result(MigrationStatus.Failed, 0, 0, text); }
        if (root != null && root is not YamlMapping)
        { diags.Add(catalog.Create(SharedCodes.InvalidValue, file, root.Line, root.Column, "The document must be a mapping of keys to values.")); return Result(MigrationStatus.Failed, 0, 0, text); }

        var from = 1;
        var usesHeader = false;
        if ((root as YamlMapping)?.Get(SchemaHeader.Key) is { } hn && options.VersionKey != SchemaHeader.Key)
        {
            if (((YamlMapping)root!).Get(options.VersionKey) != null)
            {
                diags.Add(catalog.Create(SharedCodes.SchemaVersionInvalid, file, hn.Line, hn.Column, $"Both `{SchemaHeader.Key}` and `{options.VersionKey}` are present; use one."));
                return Result(MigrationStatus.Failed, 0, 0, text);
            }
            if (hn is not YamlScalar hs || !SchemaHeader.TryParse(hs.Value, out var hk, out from) || hk != kind)
            {
                diags.Add(catalog.Create(SharedCodes.SchemaVersionInvalid, file, hn.Line, hn.Column, $"`{SchemaHeader.Key}` must be `{kind}/<positive whole number>`."));
                return Result(MigrationStatus.Failed, 0, 0, text);
            }
            usesHeader = true;
        }
        else if ((root as YamlMapping)?.Get(options.VersionKey) is { } vn)
        {
            if (vn is not YamlScalar vs || !int.TryParse(vs.Value, NumberStyles.None, CultureInfo.InvariantCulture, out from) || from < 1)
            {
                diags.Add(catalog.Create(SharedCodes.SchemaVersionInvalid, file, vn.Line, vn.Column, $"`{options.VersionKey}` must be a positive whole number."));
                return Result(MigrationStatus.Failed, 0, 0, text);
            }
        }

        var latest = LatestVersion(kind);
        var target = options.TargetVersion ?? latest;
        if (from > latest)
        {
            var keyName = usesHeader ? SchemaHeader.Key : options.VersionKey;
            var at = (root as YamlMapping)?.Get(keyName);
            diags.Add(catalog.Create(SharedCodes.SchemaVersionNewer, file, at?.Line ?? 0, at?.Column ?? 0,
                $"`{keyName}` is {from}, but this tool understands `{kind}` files up to version {latest}.", "Upgrade the tool; this file was written by a newer version."));
            return Result(MigrationStatus.Refused, from, from, text);
        }
        if (target > latest || target < from)
        {
            diags.Add(catalog.Create(SharedCodes.MigrationFailed, file, 0, 0, $"Cannot migrate `{kind}` from version {from} to {target} (known range {from}..{latest})."));
            return Result(MigrationStatus.Failed, from, from, text);
        }
        if (from == target) return Result(MigrationStatus.UpToDate, from, from, text);

        var doc = new YamlDocument(text, file, catalog);
        var version = from;
        try
        {
            while (version < target)
            {
                var step = _migrations.FirstOrDefault(m => m.Kind == kind && m.From == version);
                if (step == null)
                {
                    diags.Add(catalog.Create(SharedCodes.MigrationFailed, file, 0, 0, $"There is no migration of `{kind}` from version {version} to {version + 1}."));
                    return Result(MigrationStatus.Failed, from, version, text);
                }
                log.Info($"{kind}: {step.From} -> {step.To}");
                var before = doc.Text;
                step.Apply(doc, log);
                if (log.Pending.Count > 0)
                {
                    foreach (var q in log.Pending)
                        diags.Add(catalog.Create(SharedCodes.MigrationQuestion, file, 0, 0, $"Migration {kind} {step.From} -> {step.To} needs an answer: {q.Question}" + (q.Options != null ? $" ({string.Join(" / ", q.Options)})" : ""), $"Supply an answer for `{q.Id}`."));
                    return Result(MigrationStatus.Failed, from, version, text);
                }
                if (usesHeader) doc.SetVersion(SchemaHeader.Key, step.To, kind); else doc.SetVersion(options.VersionKey, step.To);
                version = step.To;
            }
        }
        catch (MigrationException ex)
        {
            diags.Add(catalog.Create(SharedCodes.MigrationFailed, file, 0, 0, $"Migration of `{kind}` failed at version {version}: {ex.Message}"));
            return Result(MigrationStatus.Failed, from, version, text);
        }
        return Result(MigrationStatus.Migrated, from, version, doc.Text);
    }

    /// <summary>Migrates in memory for read-only consumers. Fails (null text) when refused or failed.</summary>
    public (string? Text, MigrationResult Result) MigrateOnRead(string kind, string text, string file, MigrationOptions? options = null)
    {
        var r = Plan(kind, text, file, options);
        return (r.Ok ? r.NewText : null, r);
    }

    /// <summary>Plans, then writes the file with a <c>.bak</c> copy of the original (<c>name.bak</c>, or <c>name.bak.1</c>, ... if that exists). Nothing is written when the plan fails.</summary>
    public ApplyResult Apply(string kind, string path, MigrationOptions? options = null)
    {
        var original = File.ReadAllText(path);
        var plan = Plan(kind, original, path, options);
        if (plan.Status != MigrationStatus.Migrated) return new ApplyResult(plan, false, null);
        var backup = path + ".bak";
        for (var n = 1; File.Exists(backup); n++) backup = $"{path}.bak.{n}";
        File.Copy(path, backup);
        var temp = path + ".migrating";
        File.WriteAllText(temp, plan.NewText, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        return new ApplyResult(plan, true, backup);
    }

    private sealed class Log(IReadOnlyDictionary<string, string>? answers) : IMigrationLog
    {
        public List<string> Lines { get; } = [];
        public List<MigrationQuestion> Pending { get; } = [];
        public void Info(string message) => Lines.Add(message);
        public string? Ask(string id, string question, IReadOnlyList<string>? options = null)
        {
            if (answers != null && answers.TryGetValue(id, out var a) && (options == null || options.Contains(a))) return a;
            Pending.Add(new MigrationQuestion(id, question, options));
            return null;
        }
    }
}
