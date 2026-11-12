using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DbDataKit.Access;
using DbDataKit.Semantic;

namespace DbDataKit.Serving.Semantic;

/// <summary>
/// The column rules of a model's <c>access</c> section applied for one caller (plan 22a GP13): the caller's names (id, name, the groups in <see cref="ServingCaller.Roles"/>) resolve the rules into
/// <see cref="ColumnTreatments"/>, and the treatments give the caller their own view of the catalog (<see cref="CatalogColumnView"/>). Views are kept per model object and per set of treatments, so a caller costs one rewrite the first time and
/// two callers with the same treatments share one view; a republished model is a new object, so it never serves a view of the old rules.
/// </summary>
public static class SemanticColumnAccess
{
    private sealed class PerModel(AccessSection? access)
    {
        public AccessSection? Access { get; } = access;

        public ConcurrentDictionary<string, CatalogColumnView> Views { get; } = new(StringComparer.Ordinal);
    }

    private static readonly ConditionalWeakTable<SemanticModel, PerModel> Models = new();

    private static PerModel Of(SemanticModel model) => Models.GetValue(model, m => new PerModel(m.Catalog.Access));

    /// <summary>What the model's column rules do to this caller.</summary>
    public static ColumnTreatments TreatmentsOf(SemanticModel model, ServingCaller caller) =>
        ColumnTreatments.For(Of(model).Access, caller.Id, caller.Name, caller.Roles);

    /// <summary>
    /// The caller's resolved column treatments as the part of a cache key they own (<c>-</c> for none): equal for callers whose treatments are equal, different for two that differ, so an entry made for one is never served to the other.
    /// The access resolver's fingerprint (plan 22a GP10) takes this value as one of its parts.
    /// </summary>
    public static string Fingerprint(SemanticModel model, ServingCaller caller) => TreatmentsOf(model, caller).Fingerprint;

    /// <summary>The model as this caller sees it (the same object when no treatment applies) and the view that says how.</summary>
    internal static (SemanticModel Model, CatalogColumnView View) For(SemanticModel model, ServingCaller caller)
    {
        var per = Of(model);
        if (per.Access is not { Columns.Count: > 0 }) return (model, CatalogColumnView.Unrestricted(model.Catalog));
        var treatments = ColumnTreatments.For(per.Access, caller.Id, caller.Name, caller.Roles);
        if (treatments.IsEmpty) return (model, CatalogColumnView.Unrestricted(model.Catalog));
        var view = per.Views.GetOrAdd(treatments.Fingerprint, _ => CatalogColumnView.For(model.Catalog, treatments));
        return (model with { Catalog = view.Catalog }, view);
    }

    /// <summary>
    /// A request cannot filter, sort, join or compute by a column the caller sees masked or nulled: probing it by equality would learn the value. The attributes of the request's own <c>attributes</c> (what it groups and selects by)
    /// are the only place such a name may stand. The message names the column, which the caller sees (masked) anyway.
    /// </summary>
    internal static void RefuseProbing(JsonObject document, CatalogColumnView view)
    {
        if (view.Treated.Count == 0) return;
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in view.Treated.Keys) { names[name] = name; names[Catalog.ColName(name)] = name; }
        var selected = document["query"]?["attributes"] as JsonArray;
        if (document["kind"]?.GetValue<string>() == "members")
        {
            // a members listing of a masked column lists its masked values; searching it would find the real ones
            if (document["attribute"]?.GetValue<string>() is { } listed && names.TryGetValue(listed, out var named) && document["search"] is not null)
                throw new ServingInvalidException($"The column '{named}' is {Describe(view.Treated[named])} for you, so its members cannot be searched.");
            return;
        }

        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonValue value:
                    if (value.TryGetValue<string>(out var text) && names.TryGetValue(text, out var attribute))
                        throw new ServingInvalidException($"The column '{attribute}' is {Describe(view.Treated[attribute])} for you, so a request cannot filter, sort, join or compute by it; it can be selected and grouped by.");
                    break;
                case JsonObject obj:
                    foreach (var (key, child) in obj)
                    {
                        if (names.TryGetValue(key, out var byKey)) throw new ServingInvalidException($"The column '{byKey}' is {Describe(view.Treated[byKey])} for you, so a request cannot filter, sort, join or compute by it; it can be selected and grouped by.");
                        Walk(child);
                    }

                    break;
                case JsonArray array:
                    if (ReferenceEquals(array, selected)) break;
                    foreach (var item in array) Walk(item);
                    break;
            }
        }

        Walk(document);
    }

    internal static string Describe(ColumnTreatmentOf treatment) => treatment.Treatment == ColumnTreatment.Null ? "null" : "masked";

    private static readonly byte[] ProcessKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>The key of a keyed hash mask: the host's (<see cref="SemanticModels.MaskKey"/>, from its secrets store) or, when it gives none, one for this process, so a hash is stable while the process runs and never across restarts.</summary>
    internal static byte[] KeyOf(SemanticModels models) => models.MaskKey?.Invoke() is { Length: > 0 } key ? key : ProcessKey;
}

/// <summary>The keyed hash that replaces the values of the attributes a caller sees hash-masked, in an answer or a members list.</summary>
internal sealed class HashMasking(byte[] key, string model, IReadOnlySet<string> hashed)
{
    public static HashMasking? Of(CatalogColumnView view, SemanticModels models, string model) => view.Hashed.Count == 0 ? null : new HashMasking(SemanticColumnAccess.KeyOf(models), model, view.Hashed);

    public bool Covers(string attribute) => hashed.Contains(attribute);

    public string Apply(string attribute, string value) => ColumnMasks.Hash(key, model, attribute, value);
}
