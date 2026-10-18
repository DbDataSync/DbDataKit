using System.Text;
using System.Text.RegularExpressions;
using DbDataBuild.Models;

namespace DbDataBuild.Targets.Loaders;

/// <summary>
/// The strategy statements of DESIGN.md 6.6, shared across engines. Every script stages the query result once into a temporary table (so
/// the body runs once and its result cannot change between the delete and the insert), then applies it inside one transaction that the
/// script opens and closes itself. Engines supply only the primitives that differ: quoting, transaction and staging syntax, delete,
/// merge and duration arithmetic. Parameters appear only as value placeholders (@name), bound by the driver.
/// </summary>
public abstract partial class LoaderBase : ILoader
{
    /// <summary>The name the transpiled body is given as a CTE. Chosen so it cannot collide with a CTE the body defines.</summary>
    public const string BodyName = "ddb_body";

    [GeneratedRegex(@"^[0-9T:\- .+]+$")]
    private static partial Regex SafeLiteral();

    protected abstract string Quote(string identifier);
    protected abstract string Stage { get; }
    protected abstract IReadOnlyList<string> Prelude { get; }
    protected abstract IReadOnlyList<string> OpenTransaction { get; }
    protected abstract IReadOnlyList<string> CloseTransaction { get; }
    protected abstract string StageStatement(string prefix, string selectList, string? where);
    protected virtual string? DropStage => null;
    protected abstract string DeleteMatchingKeys(string table, IReadOnlyList<string> keys);
    protected abstract string Merge(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keys);
    protected abstract string SubtractDuration(string expression, LoadDuration duration, string columnType);
    protected abstract string TypedLiteral(string logicalType, string literal);

    /// <summary>SchemaName-qualified, quoted target table of a model (`marts.fct_orders` becomes [marts].[fct_orders]).</summary>
    protected string Table(string model)
    {
        var i = model.LastIndexOf('.');
        return i < 0 ? Quote(model) : $"{Quote(model[..i])}.{Quote(model[(i + 1)..])}";
    }

    protected static string Literal(string value) =>
        SafeLiteral().IsMatch(value) ? value : throw new InvalidOperationException($"`{value}` is not a safe literal.");

    public RenderedScript Render(LoadRequest r)
    {
        var op = r.Operation;
        var table = Table(r.Model);
        var names = r.Columns.Select(c => c.Name).ToList();
        var colList = string.Join(", ", names.Select(Quote));
        var parameters = new List<RenderedParameter>();
        string? resolver = null;
        var statements = new List<string>();
        string? where = null;
        string? deleteWhere = null;
        var delete = true;

        string Col(string name) => $"{BodyName}.{Quote(name)}";

        switch (op.Strategy)
        {
            case LoadStrategies.FullReplace:
                deleteWhere = null;
                break;

            case LoadStrategies.DeleteInsertByKey:
            case LoadStrategies.MergeByKey:
                break;

            case LoadStrategies.DeleteInsertByRange:
            {
                var c = op.Column!;
                where = $" WHERE {Col(c)} >= @start AND {Col(c)} < @end";
                deleteWhere = $"{Quote(c)} >= @start AND {Quote(c)} < @end";
                var startType = op.Params.First(p => p.Name == "start").Type;
                var endType = op.Params.First(p => p.Name == "end").Type;
                parameters.Add(new("start", startType, "runtime", null));
                parameters.Add(new("end", endType, "runtime", op.MaxSpan == null ? null : $"max_span {op.MaxSpan}"));
                break;
            }

            case LoadStrategies.WatermarkAppend:
            {
                var w = op.Watermark!;
                var type = r.WatermarkColumnType!;
                // with a lookback the window from the watermark is replaced, so nothing in it is ever duplicated; without one rows are only appended
                if (w.Lookback != null)
                {
                    where = $" WHERE {Col(w.Column)} >= @watermark";
                    deleteWhere = $"{Quote(w.Column)} >= @watermark";
                }
                else
                {
                    where = $" WHERE {Col(w.Column)} > @watermark";
                    delete = false;
                }
                parameters.Add(new("watermark", type, "resolver", w.Overridable ? "overridable" : null));

                var expr = $"MAX({Quote(w.Column)})";
                if (w.Lookback != null) expr = SubtractDuration(expr, w.Lookback, type);
                if (w.OnNull == WatermarkSpec.InitialLiteral) expr = $"COALESCE({expr}, {TypedLiteral(type, w.Initial!)})";
                resolver = $"SELECT {expr} FROM {table};";
                break;
            }

            default:
                throw new InvalidOperationException($"Unknown strategy {op.Strategy}.");
        }

        statements.Add(StageStatement(r.TransformedBodyPrefix, colList, where));
        switch (op.Strategy)
        {
            case LoadStrategies.DeleteInsertByKey:
                statements.Add(DeleteMatchingKeys(table, op.Key));
                break;
            case LoadStrategies.MergeByKey:
                statements.Add(Merge(table, names, op.Key));
                break;
            default:
                if (delete) statements.Add($"DELETE FROM {table}{(deleteWhere == null ? "" : " WHERE " + deleteWhere)};");
                break;
        }
        if (op.Strategy != LoadStrategies.MergeByKey)
            statements.Add($"INSERT INTO {table} ({colList})\nSELECT {colList} FROM {Stage};");
        if (DropStage is { } drop) statements.Add(drop);

        var text = new StringBuilder();
        foreach (var line in Prelude.Concat(OpenTransaction).Concat(statements).Concat(CloseTransaction)) text.Append(line).Append('\n');
        return new RenderedScript(text.ToString(), resolver, parameters);
    }
}
