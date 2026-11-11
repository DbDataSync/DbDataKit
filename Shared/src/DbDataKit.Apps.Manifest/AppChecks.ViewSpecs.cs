using DbDataKit.Config.Yaml;

namespace DbDataKit.Apps;

public static partial class AppChecks
{
    /// <summary>
    /// The view spec files (<c>view-spec/1</c>) the manifest names under <c>views</c>, read and checked: each problem names the spec's own file and line (<c>DBD-APP-014</c> to <c>018</c>, <see cref="ViewSpecLoader"/>).
    /// Beyond the file itself, a view must use a model and an entity the manifest lists (and, to write, with <c>write</c>), a link must lead to another view the manifest names and give that view's required parameters,
    /// and, when <paramref name="modelAttributes"/> is given (model, then entity, then its attributes, from the Store's schema), every attribute a view names must exist in the entity.
    /// <paramref name="read"/> gives a file's text by its path in the app, or null when there is none (that is <see cref="Files"/>' finding, not repeated here).
    /// </summary>
    public static IReadOnlyList<ConfigDiagnostic> ViewSpecs(AppManifest manifest, Func<string, string?> read, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>>? modelAttributes = null)
    {
        var found = new List<ConfigDiagnostic>();
        var specs = new Dictionary<string, ViewSpec>(StringComparer.Ordinal);
        foreach (var path in manifest.Views.Distinct(StringComparer.Ordinal))
        {
            if (read(path) is not { } text) continue;
            var load = ViewSpecLoader.Load(text, path, manifest.Name);
            found.AddRange(load.Diagnostics);
            if (load.Spec is { } spec) specs[path] = spec;
        }
        foreach (var spec in specs.Values) { CheckUses(manifest, spec, modelAttributes, found); CheckLinks(manifest, spec, specs, found); }
        return found;
    }

    private static void Problem(List<ConfigDiagnostic> found, string code, string file, SpecPos at, string message, string? fix = null) =>
        found.Add(DefaultDiagnosticCatalog.Instance.Create(code, file, at.Line, at.Column, message, fix));

    private static void CheckUses(AppManifest manifest, ViewSpec spec, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>>? modelAttributes, List<ConfigDiagnostic> found)
    {
        var use = manifest.Models.FirstOrDefault(m => m.Model == spec.Model);
        if (use is null)
        {
            Problem(found, ViewSpecLoader.UnknownViewModel, spec.File, spec.ModelAt, $"The view uses the model `{spec.Model}`, which the manifest does not list under `models`.",
                manifest.Models.Count == 0 ? "List the model and its entities under `models` in the manifest." : $"The manifest's models: {string.Join(", ", manifest.Models.Select(m => m.Model))}.");
            return;
        }
        var write = spec.Kind == ViewKind.Form;

        // each entity a view reads or writes, with the attributes it names there
        void Entity(string entity, SpecPos at, IEnumerable<(string Name, SpecPos At)> attributes)
        {
            if (!use.Entities.TryGetValue(entity, out var allowed))
            {
                Problem(found, ViewSpecLoader.UnknownViewModel, spec.File, at, $"The view uses `{spec.Model}.{entity}`, which the manifest does not list for that model.",
                    $"The manifest lists: {string.Join(", ", use.Entities.Keys.Order(StringComparer.Ordinal))}.");
                return;
            }
            // a form may also submit as a change request: the manifest's `change request` is enough for it
            if (!allowed.HasFlag(write ? DataOperations.Write : DataOperations.Read) && !(write && allowed.HasFlag(DataOperations.ChangeRequest)))
                Problem(found, ViewSpecLoader.UnknownViewModel, spec.File, at, $"The view {(write ? "writes" : "reads")} `{spec.Model}.{entity}`, but the manifest asks only to {(write ? "read" : "write")} it.", $"Add `{(write ? "write` (or `change request" : "read")}` to `{entity}` under `models`.");
            if (modelAttributes is null || !modelAttributes.TryGetValue(spec.Model, out var entities) || !entities.TryGetValue(entity, out var known)) return;
            foreach (var (name, attributeAt) in attributes.Where(a => !known.Contains(a.Name)))
                Problem(found, ViewSpecLoader.UnknownViewAttribute, spec.File, attributeAt, $"The view names `{entity}.{name}`, which the model `{spec.Model}` does not have.",
                    $"The entity has: {string.Join(", ", known.Order(StringComparer.Ordinal))}.");
        }

        var linked = spec.Links.SelectMany(l => l.With.Values).Where(v => v.Kind == ViewValueKind.Attribute).Select(v => (v.Text, v.At));
        switch (spec)
        {
            case GridView g:
                Entity(g.Entity, g.EntityAt, g.Columns.Select(c => (c.Attribute, c.At)).Concat(g.Filters.Select(f => (f.Name, f.At))).Concat(g.Sort.Select(s => (s.Attribute, s.At))).Concat(linked));
                break;
            case FormView f:
                Entity(f.Entity, f.EntityAt, f.Fields.Select(x => (x.Attribute, x.At)).Concat(linked));
                break;
            case DetailView d:
                Entity(d.Entity, d.EntityAt, d.Sections.SelectMany(s => s.Attributes).Select(a => (a.Name, a.At)).Concat(linked));
                break;
            case DashboardView board:
                foreach (var w in board.Widgets)
                    Entity(w.Entity, w.EntityAt, w.Columns.Concat(w.Measure is null ? [] : [w.Measure]).Concat(w.By is null ? [] : [w.By]).Select(a => (a.Name, a.At)));
                break;
        }
    }

    private static void CheckLinks(AppManifest manifest, ViewSpec spec, IReadOnlyDictionary<string, ViewSpec> specs, List<ConfigDiagnostic> found)
    {
        foreach (var link in spec.Links)
        {
            if (!manifest.Views.Contains(link.To, StringComparer.Ordinal))
            { Problem(found, ViewSpecLoader.BadViewLink, spec.File, link.ToAt, $"The link `{link.Label}` leads to `{link.To}`, which is not one of the app's `views`.", "Name a file listed under `views` in the manifest."); continue; }
            if (!specs.TryGetValue(link.To, out var target)) continue; // missing or damaged: its own findings say so
            foreach (var given in link.With.Keys.Where(k => target.Params.All(p => p.Name != k)))
                Problem(found, ViewSpecLoader.BadViewLink, spec.File, link.With[given].At, $"The link `{link.Label}` gives `{given}`, which `{link.To}` has no parameter for.",
                    target.Params.Count == 0 ? $"`{link.To}` takes no parameters." : $"Its parameters: {string.Join(", ", target.Params.Select(p => p.Name))}.");
            foreach (var missing in target.Params.Where(p => p.Required && !link.With.ContainsKey(p.Name)))
                Problem(found, ViewSpecLoader.BadViewLink, spec.File, link.At, $"The link `{link.Label}` does not give the required parameter `{missing.Name}` of `{link.To}`.", $"Add `with: {{{missing.Name}: ...}}`.");
            foreach (var (name, value) in link.With)
                if (target.Params.FirstOrDefault(p => p.Name == name) is { } p && value.Kind == ViewValueKind.Literal && !ViewSpecLoader.Fits(value.Text, p.Type))
                    Problem(found, ViewSpecLoader.BadViewLink, spec.File, value.At, $"`{value.Text}` is not a {p.Type.ToString().ToLowerInvariant()}, which is what `{link.To}` takes for `{name}`.");
        }
    }
}
