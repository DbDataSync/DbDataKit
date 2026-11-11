using System.Text.Json.Nodes;
using DbDataKit.Semantic.Presentation;
using DbDataKit.Serving;

namespace DbDataKit.Views;

/// <summary>
/// An answer read and drawn the way a dashboard widget is (plan 14a SL16, D37): <see cref="ReadAsync"/> reads the batches into the result document the engine's renderer reads (<c>descriptor</c> and <c>rows</c> as objects, the same
/// code as <see cref="WidgetResolver"/>), and <see cref="Render"/> hands a view document and that result to <see cref="ViewRenderer"/>, the one place a matrix or a chart becomes data. A caller that is not a dashboard (the Gateway's
/// explorer) uses these two so it cannot disagree with a dashboard on a cell.
/// </summary>
public static class AnswerViews
{
    /// <summary>The answer as a result document (<c>{descriptor, rows}</c>). More than <paramref name="maxRows"/> rows, or a column a view cannot read, is a <see cref="ViewsAnswerException"/>.</summary>
    public static async Task<JsonObject> ReadAsync(IServingAnswer answer, long maxRows, CancellationToken cancellationToken)
    {
        try { return (await AnswerRows.ReadAsync(answer, maxRows, cancellationToken).ConfigureAwait(false)).Result; }
        catch (WidgetProblem problem) { throw new ViewsAnswerException(problem.Code, problem.Message); }
    }

    /// <summary>The renderer's answer for a view document and a result document: <c>{kind: rendered_view, status: complete, views}</c> or <c>{status: failed, errors}</c> in the engine's words.</summary>
    public static JsonObject Render(JsonObject viewDocument, JsonObject result) => new ViewRenderer().Render(viewDocument, result);
}

/// <summary>An answer that cannot be read into a result document (too many rows, a column type a view cannot read), with the code and words of the widget error a dashboard would show.</summary>
public sealed class ViewsAnswerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
