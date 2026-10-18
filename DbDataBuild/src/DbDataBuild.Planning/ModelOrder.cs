using DbDataBuild.Models;

namespace DbDataBuild.Planning;

public static class ModelOrder
{
    /// <summary>
    /// Dependency order: a model comes after the models it reads from. Ties break by name, so the order is deterministic. Sources and unknown tables
    /// are not models and impose no order. On a cycle, <paramref name="cycle"/> names it and the models in it are left out.
    /// </summary>
    public static IReadOnlyList<PlannedModel> Sort(IReadOnlyList<PlannedModel> models, out IReadOnlyList<string>? cycle)
    {
        var byName = models.ToDictionary(m => m.Definition.Name, StringComparer.OrdinalIgnoreCase);
        var result = new List<PlannedModel>();
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 1 visiting, 2 done
        IReadOnlyList<string>? found = null;
        var stack = new List<string>();

        void Visit(PlannedModel m)
        {
            var name = m.Definition.Name;
            if (state.GetValueOrDefault(name) == 2 || found != null) return;
            if (state.GetValueOrDefault(name) == 1)
            {
                var start = stack.FindIndex(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
                found = stack.Skip(start).Append(name).ToList();
                return;
            }
            state[name] = 1;
            stack.Add(name);
            foreach (var dep in m.BaseTables.Order(StringComparer.Ordinal))
                if (byName.TryGetValue(dep, out var d) && !string.Equals(dep, name, StringComparison.OrdinalIgnoreCase)) Visit(d);
            stack.RemoveAt(stack.Count - 1);
            if (found != null) return;
            state[name] = 2;
            result.Add(m);
        }

        foreach (var m in models.OrderBy(m => m.Definition.Name, StringComparer.Ordinal)) Visit(m);
        cycle = found;
        return found == null ? result : [];
    }
}
