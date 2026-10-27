using System.Globalization;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;
using DbDataKit.Dialect;

namespace DbDataKit.DataModel;

public sealed class DataModelLoadResult(DataModel? model, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public DataModel? Model { get; } = model;
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
    public bool Ok => Model is not null && !Diagnostics.HasErrors();
}

/// <summary>Strict reader and validator of <c>model/1</c> files. A problem is a diagnostic with the file, line and column; nothing here throws on a damaged file.</summary>
public static class DataModelLoader
{
    public const string BadName = "DBD-MDL-001";
    public const string BadType = "DBD-MDL-002";
    public const string BadKey = "DBD-MDL-003";
    public const string BadReference = "DBD-MDL-004";
    public const string BadRule = "DBD-MDL-005";
    public const string BadConstraint = "DBD-MDL-006";

    public const int Version = 1;

    public static DataModelLoadResult Load(string yaml, string file = "model.yaml")
    {
        var diags = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, file, diags);
        if (root is null || diags.HasErrors()) return new(null, diags);
        if (root is not YamlMapping map)
        {
            diags.Add(DefaultDiagnosticCatalog.Instance.Create(SharedCodes.InvalidValue, file, root.Line, root.Column, "A model file is a mapping with `schemaVersion`, `name`, `storage` and `entities`."));
            return new(null, diags);
        }
        var model = new Reader(file, diags).Read(map);
        return new(diags.HasErrors() ? null : model, diags);
    }

    private sealed class Reader(string file, List<ConfigDiagnostic> diags) : YamlFieldReader(file, diags)
    {
        private static readonly string[] RootKeys = ["schemaVersion", "name", "title", "storage", "approval", "entities"];
        private static readonly string[] EntityKeys = ["title", "key", "attributes", "history", "role", "rules"];
        private static readonly string[] AttributeKeys = ["title", "type", "required", "unique", "default", "pattern", "min", "max", "allowed", "references"];
        private static readonly string[] RuleKeys = ["kind", "attribute", "group_by", "value", "tolerance"];
        private static readonly Regex Identifier = new("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly List<(YamlScalar At, string? Model, string Entity, string? Attribute, string FromEntity)> _references = [];

        public DataModel? Read(YamlMapping map)
        {
            CheckKeys(map, RootKeys, "the model");
            if (!Version(map)) return null;

            var name = Name(map, "name", "the model");
            var title = map.Get("title") is YamlScalar t ? t.Value : null;
            var connection = Connection(map);
            var approval = Approval(map);

            var entities = new List<ModelEntity>();
            if (map.Get("entities") is not YamlMapping entityMap || entityMap.Entries.Count == 0)
                Add(SharedCodes.MissingKey, (YamlNode?)map.Get("entities") ?? map, "`entities` must list at least one entity.", "Add `entities:` with a mapping of entity names.");
            else
                foreach (var entry in entityMap.Entries)
                {
                    if (entry.Value is not YamlMapping body) { Add(SharedCodes.InvalidValue, entry.Value, $"The entity `{entry.Key.Value}` must be a mapping."); continue; }
                    if (!Identifier.IsMatch(entry.Key.Value)) { Add(BadName, entry.Key, $"`{entry.Key.Value}` is not a plain name: use letters, digits and underscores, starting with a letter."); continue; }
                    if (Entity(entry.Key.Value, body) is { } entity) entities.Add(entity);
                }

            CheckReferences(entities);
            if (name is null || connection is null) return null;
            return new DataModel(name, title, connection, approval, entities);
        }

        private bool Version(YamlMapping map)
        {
            var node = map.Get("schemaVersion");
            if (node is null) { Add(SharedCodes.SchemaVersionInvalid, map, "`schemaVersion` is missing.", "Add `schemaVersion: 1`."); return false; }
            if (node is not YamlScalar { Value: var text } || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
            { Add(SharedCodes.SchemaVersionInvalid, node, "`schemaVersion` must be a positive whole number."); return false; }
            if (version > DataModelLoader.Version)
            { Add(SharedCodes.SchemaVersionNewer, node, $"`schemaVersion` is {version}, but this tool understands up to {DataModelLoader.Version}.", "Upgrade the tool; this file was written by a newer version."); return false; }
            return true;
        }

        private string? Name(YamlMapping map, string key, string where)
        {
            var scalar = Scalar(map, key, required: true, map);
            if (scalar is null) return null;
            if (Identifier.IsMatch(scalar.Value)) return scalar.Value;
            Add(BadName, scalar, $"`{scalar.Value}` is not a plain name for {where}: use letters, digits and underscores, starting with a letter.");
            return null;
        }

        private string? Connection(YamlMapping map)
        {
            if (map.Get("storage") is not YamlMapping storage) { Add(SharedCodes.MissingKey, (YamlNode?)map.Get("storage") ?? map, "`storage` is required and names the connection: `storage: {connection: name}`."); return null; }
            CheckKeys(storage, ["connection"], "`storage`");
            return Scalar(storage, "connection", required: true, storage)?.Value;
        }

        private ApprovalLevel Approval(YamlMapping map)
        {
            if (map.Get("approval") is not { } node) return ApprovalLevel.None;
            if (node is not YamlMapping approval) { Add(SharedCodes.InvalidValue, node, "`approval` must be a mapping: `approval: {changes: steward}`."); return ApprovalLevel.None; }
            CheckKeys(approval, ["changes"], "`approval`");
            return approval.Get("changes") is YamlScalar level ? Choice(level, "approval.changes", new Dictionary<string, ApprovalLevel> { ["none"] = ApprovalLevel.None, ["steward"] = ApprovalLevel.Steward, ["owner"] = ApprovalLevel.Owner }) : ApprovalLevel.None;
        }

        private T Choice<T>(YamlScalar at, string what, Dictionary<string, T> options) where T : struct
        {
            if (options.TryGetValue(at.Value, out var value)) return value;
            Add(SharedCodes.InvalidValue, at, $"`{at.Value}` is not a value of `{what}`.", $"Use one of: {string.Join(", ", options.Keys)}.");
            return default;
        }

        private ModelEntity? Entity(string name, YamlMapping body)
        {
            CheckKeys(body, EntityKeys, $"the entity `{name}`");
            var attributes = new List<ModelAttribute>();
            if (body.Get("attributes") is not YamlMapping attributeMap || attributeMap.Entries.Count == 0)
                Add(SharedCodes.MissingKey, (YamlNode?)body.Get("attributes") ?? body, $"The entity `{name}` needs `attributes`.");
            else
                foreach (var entry in attributeMap.Entries)
                {
                    if (!Identifier.IsMatch(entry.Key.Value)) { Add(BadName, entry.Key, $"`{entry.Key.Value}` is not a plain name for an attribute."); continue; }
                    if (attributes.Any(a => a.Name == entry.Key.Value)) { Add(BadName, entry.Key, $"The attribute `{entry.Key.Value}` is declared twice in `{name}`."); continue; }
                    if (Attribute(name, entry.Key.Value, entry.Value) is { } attribute) attributes.Add(attribute);
                }

            var key = new List<string>();
            if (StringList(body, "key", required: true, allowEmpty: false, unique: true) is { } keyNodes)
                foreach (var k in keyNodes)
                    if (attributes.All(a => a.Name != k.Value)) Add(BadKey, k, $"`{k.Value}` is in the key of `{name}` but is not one of its attributes.", $"Attributes: {string.Join(", ", attributes.Select(a => a.Name))}.");
                    else key.Add(k.Value);
            attributes = [.. attributes.Select(a => key.Contains(a.Name) ? a with { Required = true } : a)];

            var history = body.Get("history") is YamlScalar h ? Choice(h, "history", new Dictionary<string, HistoryMode> { ["none"] = HistoryMode.None, ["audit"] = HistoryMode.Audit, ["effective_dated"] = HistoryMode.EffectiveDated }) : HistoryMode.None;
            var role = body.Get("role") is YamlScalar r ? Choice(r, "role", new Dictionary<string, EntityRole> { ["table"] = EntityRole.Table, ["dimension"] = EntityRole.Dimension, ["mapping"] = EntityRole.Mapping, ["calendar"] = EntityRole.Calendar }) : EntityRole.Table;
            var rules = Rules(name, body, attributes);
            return new ModelEntity(name, key, attributes, history, role, rules, body.Get("title") is YamlScalar title ? title.Value : null);
        }

        private ModelAttribute? Attribute(string entity, string name, YamlNode node)
        {
            if (node is not YamlMapping body) { Add(SharedCodes.InvalidValue, node, $"The attribute `{entity}.{name}` must be a mapping with at least a `type`."); return null; }
            CheckKeys(body, AttributeKeys, $"the attribute `{entity}.{name}`");
            var typeText = Scalar(body, "type", required: true, body);
            if (typeText is null) return null;
            if (!ModelTypes.TryParse(typeText.Value, out var type, out var error)) { Add(BadType, typeText, error!); return null; }

            bool Flag(string key) => body.Get(key) is YamlScalar s && (bool.TryParse(s.Value, out var v) ? v : Bad(s, key, "true or false"));
            bool Bad(YamlScalar at, string key, string expected) { Add(SharedCodes.InvalidValue, at, $"`{key}` must be {expected}."); return false; }
            decimal? Number(string key)
            {
                if (body.Get(key) is not YamlScalar s) return null;
                if (decimal.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
                Bad(s, key, "a number");
                return null;
            }

            string? defaultValue = null;
            if (body.Get("default") is YamlScalar d)
            {
                defaultValue = d.Value;
                if (!ModelTypes.Holds(type!, d.Value)) Add(BadConstraint, d, $"The default `{d.Value}` cannot be held by `{entity}.{name}` of type {typeText.Value}.");
            }

            string? pattern = null;
            if (body.Get("pattern") is YamlScalar p)
            {
                pattern = p.Value;
                try { _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException ex) { Add(BadConstraint, p, $"The pattern of `{entity}.{name}` is not a valid regular expression: {ex.Message}"); }
                if (type!.Kind != CanonicalKind.String) Add(BadConstraint, p, $"`pattern` applies to text; `{entity}.{name}` is {typeText.Value}.");
            }

            var (min, max) = (Number("min"), Number("max"));
            if (min is { } lo && max is { } hi && lo > hi) Add(BadConstraint, body.Get("min")!, $"`min` ({lo}) is above `max` ({hi}) in `{entity}.{name}`.");

            List<string>? allowed = null;
            if (StringList(body, "allowed", required: false, allowEmpty: false, unique: true) is { } values)
            {
                allowed = [.. values.Select(v => v.Value)];
                foreach (var v in values.Where(v => !ModelTypes.Holds(type!, v.Value))) Add(BadConstraint, v, $"`{v.Value}` cannot be held by `{entity}.{name}` of type {typeText.Value}.");
            }

            AttributeReference? reference = null;
            if (body.Get("references") is { } refNode)
            {
                if (refNode is not YamlMapping r) Add(SharedCodes.InvalidValue, refNode, "`references` must be a mapping: `references: {entity: territory}`.");
                else
                {
                    CheckKeys(r, ["model", "entity", "attribute"], "`references`");
                    if (Scalar(r, "entity", required: true, r) is { } target)
                    {
                        reference = new AttributeReference(r.Get("model") is YamlScalar m ? m.Value : null, target.Value, r.Get("attribute") is YamlScalar a ? a.Value : null);
                        _references.Add((target, reference.Model, reference.Entity, reference.Attribute, entity));
                    }
                }
            }

            return new ModelAttribute(name, typeText.Value, type!, Flag("required"), Flag("unique"), defaultValue, pattern, min, max, allowed, reference, body.Get("title") is YamlScalar title ? title.Value : null);
        }

        private List<ModelRule> Rules(string entity, YamlMapping body, List<ModelAttribute> attributes)
        {
            var rules = new List<ModelRule>();
            if (body.Get("rules") is not { } node) return rules;
            if (node is not YamlSequence list) { Add(SharedCodes.InvalidValue, node, "`rules` must be a list."); return rules; }
            var names = attributes.Select(a => a.Name).ToList();

            foreach (var item in list.Items)
            {
                if (item is not YamlMapping r) { Add(SharedCodes.InvalidValue, item, "A rule is a mapping with a `kind`."); continue; }
                CheckKeys(r, RuleKeys, "a rule");
                if (r.Get("kind") is not YamlScalar kindNode) { Add(SharedCodes.MissingKey, r, "A rule needs a `kind`: sum_equals, no_overlap or no_cycle."); continue; }
                var kinds = new Dictionary<string, RuleKind> { ["sum_equals"] = RuleKind.SumEquals, ["no_overlap"] = RuleKind.NoOverlap, ["no_cycle"] = RuleKind.NoCycle };
                if (!kinds.TryGetValue(kindNode.Value, out var kind)) { Add(BadRule, kindNode, $"`{kindNode.Value}` is not a kind of rule.", $"The kinds are: {string.Join(", ", kinds.Keys)}."); continue; }

                string? Member(string key, bool required)
                {
                    if (r.Get(key) is not YamlScalar s) { if (required) Add(BadRule, r, $"A `{kindNode.Value}` rule needs `{key}`."); return null; }
                    if (!names.Contains(s.Value)) { Add(BadRule, s, $"`{s.Value}` is not an attribute of `{entity}`.", $"Attributes: {string.Join(", ", names)}."); return null; }
                    return s.Value;
                }

                var attribute = Member("attribute", kind is RuleKind.SumEquals or RuleKind.NoCycle);
                var groupBy = new List<string>();
                if (StringList(r, "group_by", required: kind == RuleKind.NoOverlap) is { } groups)
                    foreach (var g in groups)
                        if (names.Contains(g.Value)) groupBy.Add(g.Value);
                        else Add(BadRule, g, $"`{g.Value}` is not an attribute of `{entity}`.");
                decimal? value = null;
                decimal tolerance = 0;
                if (kind == RuleKind.SumEquals)
                {
                    if (r.Get("value") is YamlScalar v && decimal.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) value = parsed;
                    else Add(BadRule, (YamlNode?)r.Get("value") ?? r, "A `sum_equals` rule needs a numeric `value`.");
                    if (r.Get("tolerance") is YamlScalar tol && !decimal.TryParse(tol.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out tolerance)) Add(BadRule, tol, "`tolerance` must be a number.");
                }
                else if (r.Get("value") is { } stray) Add(BadRule, stray, $"`value` belongs to `sum_equals` rules, not `{kindNode.Value}`.");
                rules.Add(new ModelRule(kind, attribute, groupBy, value, tolerance));
            }
            return rules;
        }

        private void CheckReferences(List<ModelEntity> entities)
        {
            foreach (var (at, model, entityName, attribute, from) in _references)
            {
                if (model is not null) continue;                               // another model's entities are checked where that model is known
                var target = entities.FirstOrDefault(e => e.Name == entityName);
                if (target is null) { Add(BadReference, at, $"`{from}` points at the entity `{entityName}`, which this model does not have.", $"Entities: {string.Join(", ", entities.Select(e => e.Name))}."); continue; }
                if (target.Key.Count == 0) continue;                           // the key was refused already
                if (attribute is null)
                {
                    if (target.Key.Count != 1) Add(BadReference, at, $"`{entityName}` has a key of {target.Key.Count} attributes; name the one to point at with `attribute`.");
                }
                else if (target.Attribute(attribute) is not { } found) Add(BadReference, at, $"`{entityName}` has no attribute `{attribute}`.");
                else if (!(target.Key is [var only] && only == attribute) && !found.Unique) Add(BadReference, at, $"`{entityName}.{attribute}` is neither the key nor unique, so rows cannot point at it.");
            }
        }
    }
}
