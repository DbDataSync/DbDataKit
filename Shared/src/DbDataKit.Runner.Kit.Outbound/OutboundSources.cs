using System.Data.Common;
using System.Runtime.CompilerServices;
using DbDataKit.Store;

namespace DbDataKit.Runner.Kit.Outbound;

/// <summary>
/// Where the suite's data comes from when an integration sends it out (plan 27a IX22), as <see cref="OutboundRecord"/>s: a table, a view or any query through a connection (a Build model's table or view is a table or view of the target database), and the rows of a DbDataStore entity.
/// A query of a semantic model goes through the Gateway (Flight SQL) and has no source here yet.
/// </summary>
public static class OutboundSources
{
    /// <summary>The rows of a data reader as records: <paramref name="keyColumns"/> make the key (their text, joined), every column is a value. The reader is read once, in order, and is not disposed; null becomes null.</summary>
    public static async IAsyncEnumerable<OutboundRecord> FromReaderAsync(DbDataReader reader, IReadOnlyList<string> keyColumns, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (keyColumns.Count == 0) throw new ArgumentException("A record needs a key: name the column or columns that identify a row at the other end.", nameof(keyColumns));
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        foreach (var key in keyColumns)
            if (Array.IndexOf(names, key) < 0) throw new ArgumentException($"The query has no column '{key}' for the key.", nameof(keyColumns));
        var ordinals = keyColumns.Select(reader.GetOrdinal).ToArray();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var values = new Dictionary<string, object?>(names.Length, StringComparer.Ordinal);
            for (var i = 0; i < names.Length; i++) values[names[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            yield return new OutboundRecord(string.Join('\u001f', ordinals.Select(o => Convert.ToString(values[names[o]], System.Globalization.CultureInfo.InvariantCulture))), values);
        }
    }

    /// <summary>The rows of a DbDataStore entity (the open versions, in key order, a page at a time) as records keyed by the entity's key attributes. The Store's version and period columns are not part of the values: a record's content is what a person edited.</summary>
    public static async IAsyncEnumerable<OutboundRecord> FromStoreAsync(ModelWriter writer, DbDataKit.DataModel.DataModel model, string entity, int pageSize = 500, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(model);
        var definition = model.Entities.FirstOrDefault(e => e.Name == entity) ?? throw new ArgumentException($"The model '{model.Name}' has no entity '{entity}'.", nameof(entity));
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await writer.ListAsync(entity, Math.Clamp(pageSize, 1, 1000), offset, ct).ConfigureAwait(false);
            foreach (var row in page)
                yield return new OutboundRecord(string.Join('\u001f', definition.Key.Select(k => Convert.ToString(row.Values[k], System.Globalization.CultureInfo.InvariantCulture))), row.Values);
            if (page.Count < Math.Clamp(pageSize, 1, 1000)) yield break;
        }
    }
}
