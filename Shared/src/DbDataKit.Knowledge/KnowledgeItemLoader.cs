using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Knowledge;

/// <summary>What <see cref="KnowledgeItemLoader.Load"/> found: the item, or the diagnostics that say why there is none (warnings may come with an item).</summary>
public sealed class KnowledgeItemResult(KnowledgeItem? item, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public KnowledgeItem? Item { get; } = item;

    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;

    public bool Ok => Item is not null && !Diagnostics.HasErrors();
}

/// <summary>The fields a folder's <c>_knowledge.yml</c> gives every item beneath it (<c>defaults:</c>); a field left out is not set here.</summary>
public sealed record FolderDefaults(string File, KnowledgeRole? Role, IReadOnlyList<KnowledgeSubject>? About, KnowledgeAudience? For, string? Owner, ChunkingSetting? Chunking);

public sealed class FolderDefaultsResult(FolderDefaults? defaults, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public FolderDefaults? Defaults { get; } = defaults;

    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;

    public bool Ok => Defaults is not null && !Diagnostics.HasErrors();
}

/// <summary>
/// Strict reader of a knowledge item, <c>knowledge/1</c> (plan 27a KN01, plan 27 section 3.5c): Markdown with a YAML front matter block (<c>knowledge</c>, <c>title</c>, <c>role</c>, <c>about</c>, <c>for</c>, <c>owner</c>, <c>kind</c>, <c>chunking</c>),
/// and the folder file <c>_knowledge.yml</c> with <c>defaults:</c>, nearest wins as Build's <c>_dbdatabuild.yml</c> does. Every problem is a diagnostic with the file, line and column and nothing throws on a damaged file. Codes:
/// <c>DBD-KNW-001</c> a value, <c>-002</c> the format number, <c>-003</c> the front matter block itself, <c>-004</c> the folder file, <c>-005</c> (a warning) an instruction or guardrail with no owner,
/// <c>-006</c> (a warning) a chunking setting on an item that is never split. A subject is checked against the catalog by <see cref="SubjectResolver"/>, not here.
/// </summary>
public static class KnowledgeItemLoader
{
    public const string BadItem = "DBD-KNW-001";
    public const string FormatNumber = "DBD-KNW-002";
    public const string FrontMatter = "DBD-KNW-003";
    public const string BadFolderFile = "DBD-KNW-004";
    public const string NoOwner = "DBD-KNW-005";
    public const string ChunkingIgnored = "DBD-KNW-006";

    /// <summary>The format this program reads. Formats are numbered from 1; a newer one is refused naming the file and both numbers (plan 24a, the rule of formats).</summary>
    public const int Version = 1;

    public const string Kind = "knowledge";

    /// <summary>The name of a folder's defaults file.</summary>
    public const string FolderFileName = "_knowledge.yml";

    public const int MaximumTitle = 200;
    public const int MaximumSubjects = 50;

    /// <summary>The keys of an item's front matter. The JSON Schema has the same keys; a test keeps them equal.</summary>
    public static readonly string[] ItemKeys = ["knowledge", "title", "role", "about", "for", "owner", "kind", "chunking", "questions", "model", "request", "semantic-sql", "saved-query", "verified"];

    /// <summary>The keys only a <c>kind: verified-query</c> has (KN21).</summary>
    public static readonly string[] VerifiedKeys = ["questions", "model", "request", "semantic-sql", "saved-query", "verified"];

    /// <summary>The keys of <c>defaults:</c> in a folder file (an item's own title and kind are never a default).</summary>
    public static readonly string[] DefaultKeys = ["role", "about", "for", "owner", "chunking"];

    /// <summary>Words that belong to the install policy (<c>agent-policy/1</c>), never to knowledge: a front matter that uses one is refused with a message that says where it goes.</summary>
    internal static readonly string[] PolicySettings = ["prompting", "limits", "data-rules", "data_rules"];

    public static readonly string[] Roles = ["reference", "instruction", "guardrail"];

    /// <summary>The kinds of item: an item, and a verified query (KN21), a question and the request that answers it, verified by a person.</summary>
    public static readonly string[] Kinds = ["item", VerifiedQueries.Kind];

    public const int MaximumQuestions = 20;
    public const int MaximumQuestion = 500;

    /// <summary>The subject types, each with the keys that may narrow it (plan 27 section 3.5c item 3).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> SubjectTypes = new Dictionary<string, string[]>
    {
        ["build-project"] = ["folder", "model"],
        ["semantic-model"] = ["measure", "dimension"],
        ["store-model"] = ["field"],
        ["connection"] = [],
        ["report-catalog"] = ["item"],
        ["app"] = [],
        ["integration"] = [],
        ["tag"] = [],
        ["property"] = [],
        ["product"] = [],
        ["install"] = [],
    };

    public static readonly string[] Products = ["build", "sync", "store", "integration", "apps", "serving", "runner", "console"];

    public static readonly string[] AudienceKeys = ["audience", "agent", "channel"];

    public static readonly string[] Audiences = ["front-end", "back-end"];

    private static readonly Regex Property = new("^[^=\\s]+=\\S.*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The JSON Schema of the front matter (an embedded resource), for editors and hosts. The loader is the authority; a test keeps the two in agreement.</summary>
    public static string Schema()
    {
        using var stream = typeof(KnowledgeItemLoader).Assembly.GetManifestResourceStream("DbDataKit.Knowledge.schemas.knowledge.schema.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The words of a refusal for a file in a format this program does not read, naming the kind, the file and both numbers.</summary>
    public static string Refusal(string file, int format) => format > Version
        ? $"the {Kind} item '{file}' is in format {format}, and this program reads format {Version} of {Kind}. Upgrade this program; the file was written by a newer version."
        : $"the {Kind} item '{file}' is in format {format}, which this program does not read (it reads format {Version} of {Kind}).";

    /// <summary>The version of an item: the sha256 of its text with line endings as <c>\n</c>.</summary>
    public static string HashOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")))).ToLowerInvariant();

    /// <param name="text">The file's text.</param>
    /// <param name="id">Where the item sits in its project (the relative path, forward slashes).</param>
    /// <param name="file">The path diagnostics name.</param>
    /// <param name="defaults">The folder files above the item, nearest first.</param>
    /// <param name="impliedAbout">The subject of the project the file is in (a file in a project's <c>knowledge/</c> folder is about that project); null when it is in none.</param>
    public static KnowledgeItemResult Load(string text, string id, string file, IReadOnlyList<FolderDefaults>? defaults = null, KnowledgeSubject? impliedAbout = null)
    {
        var diags = new List<ConfigDiagnostic>();
        text = text.TrimStart('﻿');
        if (!TrySplit(text, out var yaml, out var body, out var bodyLine))
        {
            diags.Add(DefaultDiagnosticCatalog.Instance.Create(FrontMatter, file, 1, 1, "A knowledge item starts with a front matter block: a line `---`, the fields, and a closing line `---`.", "Start the file with `---`, `knowledge: 1`, `role: reference` and `---`."));
            return new(null, diags);
        }
        var root = StrictYamlReader.Read(yaml, file, diags);
        if (root is null && !diags.HasErrors()) diags.Add(DefaultDiagnosticCatalog.Instance.Create(FrontMatter, file, 2, 1, "The front matter is empty.", "Write `knowledge: 1` and a `role`."));
        if (root is null || diags.HasErrors()) return new(null, diags);

        var reader = new Reader(file, diags);
        var item = reader.ReadItem(root, id, text, body, bodyLine, defaults ?? [], impliedAbout);
        return new(diags.HasErrors() ? null : item, diags);
    }

    /// <summary>Reads a folder's <c>_knowledge.yml</c>.</summary>
    public static FolderDefaultsResult LoadFolderFile(string text, string file)
    {
        var diags = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(text.TrimStart('﻿'), file, diags);
        if (root is null && !diags.HasErrors()) diags.Add(DefaultDiagnosticCatalog.Instance.Create(BadFolderFile, file, 1, 1, "The folder file is empty.", "Write `knowledge: 1` and `defaults:`."));
        if (root is null || diags.HasErrors()) return new(null, diags);
        var defaults = new Reader(file, diags).ReadFolder(root);
        return new(diags.HasErrors() ? null : defaults, diags);
    }

    // the front matter is between the first line `---` and the next; the YAML is padded with a blank line so the lines the reader reports are the file's own
    private static bool TrySplit(string text, out string yaml, out string body, out int bodyLine)
    {
        yaml = body = "";
        bodyLine = 0;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd() != "---") return false;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() != "---") continue;
            yaml = "\n" + string.Join('\n', lines[1..i]);
            body = string.Join('\n', lines[(i + 1)..]);
            bodyLine = i + 2;
            return true;
        }
        return false;
    }

    private sealed record Fields(KnowledgeRole? Role, IReadOnlyList<KnowledgeSubject>? About, KnowledgeAudience? For, string? Owner, ChunkingSetting? Chunking, YamlNode? ChunkingNode);

    private sealed class Reader(string fileName, List<ConfigDiagnostic> diags) : YamlFieldReader(fileName, diags)
    {
        private void Bad(YamlNode at, string message, string? fix = null) => Add(BadItem, at, message, fix);

        public KnowledgeItem? ReadItem(YamlNode root, string id, string fullText, string body, int bodyLine, IReadOnlyList<FolderDefaults> chain, KnowledgeSubject? impliedAbout)
        {
            if (root is not YamlMapping map) { Bad(root, "The front matter is a mapping with `knowledge: 1` and a `role`."); return null; }
            if (!CheckFormat(map)) return null;
            PolicyKeys(map);
            CheckKeys(map, ItemKeys.Concat(PolicySettings), "a knowledge item");
            if (map.Get("kind") is { } kind && !(kind is YamlScalar { Value: var k } && Kinds.Contains(k)))
                Bad(kind, $"`{(kind as YamlScalar)?.Value}` is not a kind of item this program reads.", $"Use one of: {string.Join(", ", Kinds)}, or leave `kind` out.");
            var verifiedKind = map.Get("kind") is YamlScalar { Value: VerifiedQueries.Kind };
            if (!verifiedKind)
                foreach (var entry in map.Entries.Where(e => VerifiedKeys.Contains(e.Key.Value)))
                    Bad(entry.Key, $"`{entry.Key.Value}` belongs to a verified query.", "Add `kind: verified-query`, or remove the key.");

            var own = ReadFields(map);
            var role = own.Role ?? chain.Select(c => c.Role).FirstOrDefault(r => r is not null);
            if (verifiedKind)
            {
                role ??= KnowledgeRole.Reference;
                if (role != KnowledgeRole.Reference) Bad(map.Get("role") ?? map, "A verified query is found by search like a reference and never always given: its role is `reference`.");
            }
            if (role is null) Add(SharedCodes.MissingKey, map, "Required key `role` is missing (and no `_knowledge.yml` above gives one).", $"Add `role:` with one of: {string.Join(", ", Roles)}.");
            var about = own.About ?? chain.Select(c => c.About).FirstOrDefault(a => a is not null);
            var implied = about is null && impliedAbout is not null;
            about ??= implied ? new List<KnowledgeSubject> { impliedAbout! } : new List<KnowledgeSubject>();
            var audience = own.For ?? chain.Select(c => c.For).FirstOrDefault(a => a is not null) ?? KnowledgeAudience.Everyone;
            var owner = own.Owner ?? chain.Select(c => c.Owner).FirstOrDefault(o => o is not null);
            var chunking = own.Chunking ?? chain.Select(c => c.Chunking).FirstOrDefault(c => c is not null) ?? ChunkingSetting.Default;

            var title = Title(map, body);
            VerifiedQuery? verified = null;
            if (verifiedKind)
            {
                verified = ReadVerified(map, body);
                if (own.About is null && !chain.Any(c => c.About is not null) && verified is not null) { about = [new KnowledgeSubject("semantic-model", [verified.Model])]; implied = true; }
            }
            if (role is null || title is null || (verifiedKind && verified is null)) return null;
            if (role != KnowledgeRole.Reference && own.ChunkingNode is { } node)
                Diagnostics.Add(new ConfigDiagnostic(ChunkingIgnored, ConfigSeverity.Warning, File, node.Line, node.Column, $"An {role.ToString()!.ToLowerInvariant()} is always given whole; its `chunking` is ignored.", "Remove `chunking`."));
            if (role != KnowledgeRole.Reference && owner is null)
                Diagnostics.Add(new ConfigDiagnostic(NoOwner, ConfigSeverity.Warning, File, 1, 1, $"An {role.ToString()!.ToLowerInvariant()} changes what agents do and needs an owner who approves changes to it.", "Add `owner:` with a group of the identity home, here or as a default in `_knowledge.yml`."));
            // the question texts are what is embedded and found (the notes are for people)
            if (verified is not null)
                return new KnowledgeItem(id, File, title, role.Value, about, implied, audience, owner, ChunkingSetting.None, "- " + string.Join("\n- ", verified.Questions), bodyLine, HashOf(fullText), verified);
            return new KnowledgeItem(id, File, title, role.Value, about, implied, audience, owner, role == KnowledgeRole.Reference ? chunking : ChunkingSetting.None, body, bodyLine, HashOf(fullText));
        }

        // the keys of a verified query (KN21): the questions (what is embedded), the model, the pinned request and/or its semantic SQL or a saved query of the model, and who verified it
        private VerifiedQuery? ReadVerified(YamlMapping map, string body)
        {
            var before = Diagnostics.Count;
            var questions = new List<string>();
            if (map.Get("questions") is not { } qNode) Add(SharedCodes.MissingKey, map, "Required key `questions` is missing: the questions this query answers, as people ask them (they are what is searched).", "Add `questions:` with one to twenty questions.");
            else if (qNode is not YamlSequence { Items.Count: > 0 } seq || seq.Items.Count > MaximumQuestions) Bad(qNode, $"`questions` is a list of one to {MaximumQuestions} questions.");
            else
                foreach (var item in seq.Items)
                {
                    if (item is YamlScalar { Value.Length: > 0 } q && q.Value.Length <= MaximumQuestion && !q.Value.Any(char.IsControl)) questions.Add(q.Value);
                    else Bad(item, $"A question is one line of at most {MaximumQuestion} characters.");
                }
            var model = map.Get("model") is YamlScalar { Value.Length: > 0 } m && m.Value.Length <= 200 && !m.Value.Any(char.IsControl) ? m.Value : null;

            JsonObject? request = null;
            if (map.Get("request") is { } rNode) request = Request(rNode);
            string? sql = null;
            if (map.Get("semantic-sql") is { } sNode)
            {
                if (sNode is YamlScalar { Value.Length: > 0 } sql0) sql = sql0.Value.Trim();
                else Bad(sNode, "`semantic-sql` is the query as semantic SQL (kept for reading; the request is the truth).");
            }
            string? saved = null, savedVersion = null;
            if (map.Get("saved-query") is { } savedNode)
            {
                if (savedNode is YamlScalar { Value.Length: > 0 } sv && Regex.Match(sv.Value, "^((?<model>[^/\\s@]+)/)?(?<name>[A-Za-z_][A-Za-z0-9_]*)(@(?<version>\\S+))?$") is { Success: true } hit)
                {
                    saved = hit.Groups["name"].Value;
                    savedVersion = hit.Groups["version"].Success ? hit.Groups["version"].Value : null;
                    if (hit.Groups["model"].Success)
                    {
                        if (model is not null && model != hit.Groups["model"].Value) Bad(savedNode, $"`saved-query` names a saved query of {hit.Groups["model"].Value}, and `model` is {model}.");
                        model ??= hit.Groups["model"].Value;
                    }
                }
                else Bad(savedNode, "`saved-query` is the name of a saved query of the model, with the model before a `/` and the version it was verified against after an `@` when it is pinned (`Sales/revenue_by_region@3`).");
                if (map.Get("request") is not null || map.Get("semantic-sql") is not null) Bad(savedNode, "A verified query names a saved query, or has a request or semantic SQL, not both.");
            }
            else if (map.Get("request") is null && map.Get("semantic-sql") is null)
                Add(SharedCodes.MissingKey, map, "A verified query needs the request that answers the question (`request:`), or semantic SQL (`semantic-sql:`), or a `saved-query:`.", "Add `request:` (the JSON `semantic query --request` takes).");

            if (model is null) Add(map.Get("model") is null ? SharedCodes.MissingKey : BadItem, map.Get("model") ?? map, "`model` is the name of the semantic model the query is asked of.", "Add `model: Sales`.");

            Verification? verification = null;
            if (map.Get("verified") is not { } vNode) Add(SharedCodes.MissingKey, map, "Required key `verified` is missing: who verified the query, when and against which version of the model.", "Add `verified: { by: alice, at: 2026-11-01, model-version: \"4\" }`.");
            else if (vNode is not YamlMapping vm) Bad(vNode, "`verified` is a mapping with `by`, `at` and `model-version`.");
            else
            {
                CheckKeys(vm, ["by", "at", "model-version"], "`verified`");
                string? Field(string key) => vm.Get(key) is YamlScalar { Value.Length: > 0 } f && f.Value.Length <= 200 && !f.Value.Any(char.IsControl) ? f.Value : null;
                var by = Field("by");
                var at = Field("at");
                var version = Field("model-version");
                if (by is null) Bad(vm.Get("by") ?? vm, "`verified.by` names the person who verified the query.");
                if (at is null || !DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)) Bad(vm.Get("at") ?? vm, "`verified.at` is the date (or date and time) it was verified, such as 2026-11-01.");
                if (version is null) Bad(vm.Get("model-version") ?? vm, "`verified.model-version` is the version of the model it was verified against (quote a number).");
                if (by is not null && at is not null && version is not null) verification = new Verification(by, at, version);
            }
            if (Diagnostics.Skip(before).Any(d => d.Severity == ConfigSeverity.Error) || verification is null || model is null || questions.Count == 0) return null;
            return new VerifiedQuery(questions, model, request, sql, saved, savedVersion, verification, body.Trim());
        }

        // the pinned request: the envelope `semantic query --request` takes, written as a mapping or as a JSON text
        private JsonObject? Request(YamlNode node)
        {
            JsonObject? request = null;
            try
            {
                request = node switch
                {
                    YamlScalar { Value.Length: > 0 } text => JsonNode.Parse(text.Value) as JsonObject,
                    YamlMapping => ToJson(node) as JsonObject,
                    _ => null,
                };
            }
            catch (System.Text.Json.JsonException) { }
            if (request is null) { Bad(node, "`request` is the semantic request: a mapping, or JSON text, with `kind: query` and a `query` that has `measures`."); return null; }
            if (request["kind"]?.GetValue<string>() != "query" || request["query"] is not JsonObject query || query["measures"] is not JsonArray { Count: > 0 })
            { Bad(node, "`request` has `kind: query` and a `query` with at least one entry in `measures`.", "The same document `semantic query --request` takes."); return null; }
            return request;
        }

        private static JsonNode? ToJson(YamlNode node) => node switch
        {
            YamlMapping m => new JsonObject(m.Entries.Select(e => new KeyValuePair<string, JsonNode?>(e.Key.Value, ToJson(e.Value)))),
            YamlSequence s => new JsonArray([.. s.Items.Select(ToJson)]),
            YamlScalar { Quoted: false, Value: "null" or "~" or "" } => null,
            YamlScalar { Quoted: false, Value: "true" } => JsonValue.Create(true),
            YamlScalar { Quoted: false, Value: "false" } => JsonValue.Create(false),
            YamlScalar { Quoted: false } n when long.TryParse(n.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) => l is >= int.MinValue and <= int.MaxValue ? JsonValue.Create((int)l) : JsonValue.Create(l),
            YamlScalar { Quoted: false } n when double.TryParse(n.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
            YamlScalar x => JsonValue.Create(x.Value),
            _ => null,
        };

        public FolderDefaults? ReadFolder(YamlNode root)
        {
            if (root is not YamlMapping map) { Add(BadFolderFile, root, "A folder file is a mapping with `knowledge: 1` and `defaults:`."); return null; }
            if (!CheckFormat(map)) return null;
            CheckKeys(map, ["knowledge", "defaults"], "a folder file");
            if (map.Get("defaults") is not YamlMapping defaults)
            {
                Add(BadFolderFile, map.Get("defaults") ?? map, "`defaults:` is a mapping of the fields every item beneath the folder gets.", $"Keys: {string.Join(", ", DefaultKeys)}.");
                return null;
            }
            PolicyKeys(defaults);
            CheckKeys(defaults, DefaultKeys.Concat(PolicySettings), "`defaults`");
            var f = ReadFields(defaults);
            return new FolderDefaults(File, f.Role, f.About, f.For, f.Owner, f.Chunking);
        }

        // knowledge: 1, present and a format this program reads
        private bool CheckFormat(YamlMapping map)
        {
            var node = map.Get("knowledge");
            if (node is null) { Add(FormatNumber, map, $"`knowledge` is missing: the file says which format of {Kind} it is in.", "Add `knowledge: 1`."); return false; }
            if (node is not YamlScalar { Value: var text } || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var format) || format < 1)
            { Add(FormatNumber, node, "`knowledge` must be a positive whole number."); return false; }
            if (format == Version) return true;
            Add(FormatNumber, node, Refusal(File, format), format > Version ? $"Upgrade the tool, or write the file in format {Version}." : null);
            return false;
        }

        // the install policy keeps settings (prompting minimums, limits, data rules) and knowledge keeps text: a setting written in an item is refused saying where it belongs (plan 27 section 3.5c)
        private void PolicyKeys(YamlMapping map)
        {
            foreach (var entry in map.Entries.Where(e => PolicySettings.Contains(e.Key.Value)))
                Add(BadItem, entry.Key, $"`{entry.Key.Value}` is a setting of the install policy, not knowledge: policy settings (prompting minimums, limits, data rules for cloud providers) are in the `agent-policy` definition; what agents should read is the text of an item with `role: guardrail`.",
                    "Move the setting to the install policy (the console's policy editor), and keep the sentence an agent should read here.");
        }

        private string? Title(YamlMapping map, string body)
        {
            if (map.Get("title") is { } node)
            {
                if (node is YamlScalar { Value.Length: > 0 and <= MaximumTitle } s && !s.Value.Any(char.IsControl)) return s.Value;
                Bad(node, $"`title` is one line of at most {MaximumTitle} characters.");
                return null;
            }
            foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
                if (line.StartsWith("# ", StringComparison.Ordinal) && line.Length > 2) return line[2..].Trim();
            Add(SharedCodes.MissingKey, map, "Required key `title` is missing (and the text has no `# ` heading to take it from).", "Add `title:`.");
            return null;
        }

        private Fields ReadFields(YamlMapping map)
        {
            KnowledgeRole? role = null;
            if (map.Get("role") is { } roleNode)
            {
                if (roleNode is YamlScalar s && Array.IndexOf(Roles, s.Value) is var i and >= 0) role = (KnowledgeRole)i;
                else Bad(roleNode, $"`{(roleNode as YamlScalar)?.Value}` is not a role.", $"Use one of: {string.Join(", ", Roles)}.");
            }
            var about = map.Get("about") is { } aboutNode ? About(aboutNode) : null;
            var audience = map.Get("for") is { } forNode ? For(forNode) : null;
            string? owner = null;
            if (map.Get("owner") is { } ownerNode)
            {
                if (ownerNode is YamlScalar { Value.Length: > 0 and <= 200 } o && !o.Value.Any(char.IsControl)) owner = o.Value;
                else Bad(ownerNode, "`owner` is the name of a group of the identity home: one line.");
            }
            ChunkingSetting? chunking = null;
            var chunkingNode = map.Get("chunking");
            if (chunkingNode is not null) chunking = Chunking(chunkingNode);
            return new Fields(role, about, audience, owner, chunking, chunkingNode);
        }

        private ChunkingSetting? Chunking(YamlNode node)
        {
            if (node is YamlScalar { Value: "none" }) return ChunkingSetting.None;
            if (node is YamlMapping m && m.Get("split") is YamlScalar { Value: var split } && m.Entries.Count == 1
                && Regex.Match(split, "^heading-level-([1-6])$") is { Success: true } hit)
                return new ChunkingSetting(ChunkingMode.HeadingLevel, int.Parse(hit.Groups[1].Value, CultureInfo.InvariantCulture));
            Bad(node, "`chunking` is `none` (keep the item whole) or `{ split: heading-level-2 }` (split only at headings of one level, 1 to 6).");
            return null;
        }

        private KnowledgeAudience? For(YamlNode node)
        {
            if (node is not YamlMapping map) { Bad(node, "`for` is a mapping: `audience`, `agent` and `channel`, each a name or a list; leave a key out for all.", "Example: `for: { audience: [back-end] }`."); return null; }
            CheckKeys(map, AudienceKeys, "`for`");
            return new KnowledgeAudience(Names(map, "audience", Audiences), Names(map, "agent", null), Names(map, "channel", null));
        }

        // a scalar or a list of scalars, optionally from a fixed set
        private List<string> Names(YamlMapping map, string key, string[]? allowed)
        {
            var result = new List<string>();
            if (map.Get(key) is not { } node) return result;
            var items = node is YamlSequence seq ? seq.Items : [node];
            foreach (var item in items)
            {
                if (item is not YamlScalar { Value.Length: > 0 } s || s.Value.Any(char.IsControl)) { Bad(item, $"`{key}` holds names: words on one line."); continue; }
                if (allowed is not null && !allowed.Contains(s.Value)) { Bad(s, $"`{s.Value}` is not a value of `{key}`.", $"Use one of: {string.Join(", ", allowed)}."); continue; }
                result.Add(s.Value);
            }
            return result;
        }

        private List<KnowledgeSubject>? About(YamlNode node)
        {
            if (node is not YamlSequence seq || seq.Items.Count == 0)
            {
                Bad(node, "`about` is a list of subjects, one entry each, for example `- semantic-model: Sales`.", "Leave `about` out for an item that is about nothing in particular.");
                return null;
            }
            if (seq.Items.Count > MaximumSubjects) Bad(seq, $"`about` names at most {MaximumSubjects} subjects.");
            var subjects = new List<KnowledgeSubject>();
            foreach (var entry in seq.Items)
                if (Subject(entry) is { } subject) subjects.Add(subject);
            return subjects;
        }

        private KnowledgeSubject? Subject(YamlNode entry)
        {
            if (entry is not YamlMapping map) { Bad(entry, "A subject is a mapping such as `semantic-model: Sales` (with `measure: revenue` to narrow it).", $"Types: {string.Join(", ", SubjectTypes.Keys)}."); return null; }
            var types = map.Entries.Where(e => SubjectTypes.ContainsKey(e.Key.Value)).ToList();
            if (types.Count == 0)
            {
                var first = map.Entries.FirstOrDefault()?.Key;
                var near = first is null ? null : DidYouMean.Closest(first.Value, SubjectTypes.Keys);
                Bad(first ?? (YamlNode)map, "This entry names no subject type." + (near is not null ? $" Did you mean `{near}`?" : ""), $"Types: {string.Join(", ", SubjectTypes.Keys)}.");
                return null;
            }
            if (types.Count > 1) { Bad(types[1].Key, "An entry names one subject; write several entries to widen (any of them puts the item in scope).", "Use `- a: x` and `- b: y`, not both in one entry."); return null; }
            var type = types[0];
            var narrowing = SubjectTypes[type.Key.Value];
            var narrows = new List<YamlEntry>();
            foreach (var e in map.Entries.Where(e => e != type))
            {
                if (narrowing.Contains(e.Key.Value)) { narrows.Add(e); continue; }
                var near = DidYouMean.Closest(e.Key.Value, [.. narrowing, type.Key.Value]);
                Add(SharedCodes.UnknownKey, e.Key, $"`{e.Key.Value}` does not narrow `{type.Key.Value}`." + (near is not null ? $" Did you mean `{near}`?" : ""), narrowing.Length > 0 ? $"It can be narrowed by: {string.Join(", ", narrowing)}." : $"`{type.Key.Value}` has nothing to narrow it by.");
                return null;
            }
            if (narrows.Count > 1) { Bad(narrows[1].Key, $"A subject is narrowed by one of {string.Join(", ", narrowing)}, not several.", "Write another entry."); return null; }

            var names = SubjectNames(type.Key.Value, type.Value);
            if (names is null) return null;
            string narrowKind = "", narrowName = "";
            if (narrows.Count == 1)
            {
                if (narrows[0].Value is not YamlScalar { Value.Length: > 0 } n || n.Value.Any(char.IsControl) || n.Value.Length > 200) { Bad(narrows[0].Value, $"`{narrows[0].Key.Value}` is a name on one line."); return null; }
                narrowKind = narrows[0].Key.Value;
                narrowName = n.Value;
            }
            return new KnowledgeSubject(type.Key.Value, names, narrowKind, narrowName, map.Line, map.Column);
        }

        private List<string>? SubjectNames(string type, YamlNode value)
        {
            if (type == "install")
            {
                if (value is YamlScalar { Value: "true" }) return ["true"];
                Bad(value, "`install` is `true`: the item is about everything in the install.");
                return null;
            }
            if (type == "product")
            {
                var names = new List<string>();
                foreach (var item in value is YamlSequence seq ? seq.Items : [value])
                {
                    if (item is YamlScalar s && Products.Contains(s.Value)) names.Add(s.Value);
                    else { Bad(item, $"`{(item as YamlScalar)?.Value}` is not a product.", $"Use: {string.Join(", ", Products)}."); return null; }
                }
                if (names.Count == 0) { Bad(value, "`product` names one product or a list of them."); return null; }
                return names;
            }
            if (value is not YamlScalar { Value.Length: > 0 } scalar || scalar.Value.Any(char.IsControl) || scalar.Value.Length > 200)
            { Bad(value, $"`{type}` names one thing, on one line."); return null; }
            if (type == "property" && !Property.IsMatch(scalar.Value)) { Bad(scalar, "`property` is `name=value`, as Build's selector writes it.", "Example: `property: tier=1`."); return null; }
            return [scalar.Value];
        }
    }
}
