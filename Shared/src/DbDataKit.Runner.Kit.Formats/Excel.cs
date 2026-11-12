using System.Runtime.CompilerServices;
using ClosedXML.Excel;

namespace DbDataKit.Runner.Kit.Formats;

public sealed record ExcelOptions
{
    /// <summary>The sheet to read, by name; the first sheet when null.</summary>
    public string? Sheet { get; init; }

    /// <summary>The row (from 1) that holds the column names; the rows after it are records. 0: no header, the columns are column1, column2 and so on.</summary>
    public int HeaderRow { get; init; } = 1;

    /// <summary>A workbook is read whole into memory (ClosedXML), about ten times its size on disk. A larger file is refused with a message, not with an out-of-memory crash: split it or ask the sender for CSV. 64 MiB by default.</summary>
    public long MaxFileBytes { get; init; } = 64L << 20;

    /// <summary>The sheet's name when the writer makes one.</summary>
    public string WriteSheet { get; init; } = "Sheet1";
}

/// <summary>
/// Reads an Excel workbook (.xlsx) as rows with the types the cells hold: text, <c>long</c> for a whole number, <c>double</c> for the rest, <c>bool</c>, <c>DateTime</c> for a date, <c>TimeSpan</c> for a duration. A cell with an error value (#DIV/0!) makes its row a
/// bad record (the decision of <see cref="ReadOptions.OnBadRow"/>). **The workbook is not streamed**: ClosedXML holds it whole, which is why <see cref="ExcelOptions.MaxFileBytes"/> exists.
/// </summary>
public static class ExcelRows
{
    public static async IAsyncEnumerable<Row> ReadAsync(Stream stream, ExcelOptions? options = null, ReadOptions? read = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        options ??= new ExcelOptions();
        read ??= new ReadOptions();
        var gate = new BadRowGate("Excel", read);
        await Task.Yield();
        if (stream.CanSeek && stream.Length > options.MaxFileBytes)
            throw new FileFormatException($"{(read.File is null ? "The workbook" : $"'{read.File}'")} is {stream.Length} bytes; the reader holds a workbook whole in memory and takes at most {options.MaxFileBytes}.", read.File);

        XLWorkbook workbook;
        try { workbook = new XLWorkbook(stream); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FileFormatException($"{(read.File is null ? "The data" : $"'{read.File}'")} is not an Excel workbook (.xlsx), or is damaged.", read.File, ex);
        }

        using (workbook)
        {
            IXLWorksheet sheet;
            if (options.Sheet is { } name)
            {
                if (!workbook.TryGetWorksheet(name, out sheet!)) throw new FileFormatException($"{(read.File is null ? "The workbook" : $"'{read.File}'")} has no sheet named '{name}'.", read.File);
            }
            else sheet = workbook.Worksheets.FirstOrDefault() ?? throw new FileFormatException($"{(read.File is null ? "The workbook" : $"'{read.File}'")} has no sheet.", read.File);

            var range = sheet.RangeUsed();
            if (range is null) yield break;
            var first = range.FirstRow().RowNumber();
            var last = range.LastRow().RowNumber();
            var firstColumn = range.FirstColumn().ColumnNumber();
            var width = range.LastColumn().ColumnNumber() - firstColumn + 1;
            string[] columns;
            int dataStart;
            if (options.HeaderRow > 0)
            {
                var header = Math.Max(options.HeaderRow, first);
                columns = [.. Enumerable.Range(0, width).Select(i => HeaderName(sheet.Cell(header, firstColumn + i), i))];
                dataStart = header + 1;
            }
            else
            {
                columns = [.. Enumerable.Range(1, width).Select(i => "column" + i)];
                dataStart = first;
            }

            long number = 0;
            for (var r = dataStart; r <= last; r++)
            {
                ct.ThrowIfCancellationRequested();
                var values = new object?[width];
                var blank = true;
                string? problem = null;
                var problemColumn = 0;
                for (var c = 0; c < width; c++)
                {
                    var cell = sheet.Cell(r, firstColumn + c);
                    try
                    {
                        values[c] = Convert(cell.Value, out var error);
                        if (error is not null && problem is null) { problem = error; problemColumn = c + 1; }
                        if (values[c] is not null) blank = false;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        problem ??= "a formula in the row cannot be evaluated";
                        if (problemColumn == 0) problemColumn = c + 1;
                        blank = false;
                    }
                }

                if (blank) continue;
                number++;
                if (problem is not null) { gate.Report(number, r, problemColumn, problem); continue; }
                yield return new Row(number, r, columns, values);
            }
        }
    }

    private static string HeaderName(IXLCell cell, int index)
    {
        var text = cell.GetFormattedString();
        return string.IsNullOrWhiteSpace(text) ? "column" + (index + 1) : text.Trim();
    }

    private static object? Convert(XLCellValue v, out string? error)
    {
        error = null;
        switch (v.Type)
        {
            case XLDataType.Blank: return null;
            case XLDataType.Boolean: return v.GetBoolean();
            case XLDataType.Number:
                var d = v.GetNumber();
                return d == Math.Floor(d) && Math.Abs(d) < 9e15 ? (object)(long)d : d;
            case XLDataType.Text: return v.GetText();
            case XLDataType.DateTime: return v.GetDateTime();
            case XLDataType.TimeSpan: return v.GetTimeSpan();
            case XLDataType.Error:
                error = "a cell holds an error value (" + v.GetError() + ")";
                return null;
            default: return v.ToString();
        }
    }
}

/// <summary>
/// Writes rows to an Excel workbook. ClosedXML builds the workbook in memory, so the file is written when <see cref="FinishAsync"/> is called; a writer that is disposed without it writes nothing, which is right for a failed run. At most 1,048,575 rows
/// (Excel's limit, less the header): more is refused with a message that says to split the file.
/// </summary>
public sealed class ExcelRowWriter : IRowWriter
{
    private const int MaxRows = 1_048_575;
    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly XLWorkbook _workbook = new();
    private readonly IXLWorksheet _sheet;
    private bool _finished;

    private ExcelRowWriter(Stream output, IReadOnlyList<string> columns, ExcelOptions options, bool leaveOpen)
    {
        _output = output;
        _leaveOpen = leaveOpen;
        Columns = columns;
        _sheet = _workbook.AddWorksheet(options.WriteSheet);
        for (var i = 0; i < columns.Count; i++) _sheet.Cell(1, i + 1).Value = columns[i];
        _sheet.Row(1).Style.Font.Bold = true;
    }

    public IReadOnlyList<string> Columns { get; }

    public long Written { get; private set; }

    public static ExcelRowWriter Create(Stream output, IReadOnlyList<string> columns, ExcelOptions? options = null, bool leaveOpen = true) => new(output, [.. columns], options ?? new ExcelOptions(), leaveOpen);

    public ValueTask WriteAsync(IReadOnlyList<object?> values, CancellationToken ct = default)
    {
        if (values.Count != Columns.Count) throw new ArgumentException($"A record has {Columns.Count} values, not {values.Count}.", nameof(values));
        if (Written >= MaxRows) throw new FileFormatException($"An Excel sheet holds at most {MaxRows} rows; write the rest to another file.");
        var row = (int)Written + 2;
        for (var i = 0; i < values.Count; i++) _sheet.Cell(row, i + 1).Value = Cell(values[i]);
        Written++;
        return ValueTask.CompletedTask;
    }

    private static XLCellValue Cell(object? v) => v switch
    {
        null or DBNull => Blank.Value,
        string s => s,
        bool b => b,
        int i => i,
        long l => l,
        short sh => sh,
        byte by => by,
        double d => d,
        float f => f,
        decimal m => m,
        DateTime dt => dt,
        DateTimeOffset o => o.UtcDateTime,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        TimeSpan t => t,
        _ => ValueText.Format(v) ?? string.Empty,
    };

    public async ValueTask FinishAsync(CancellationToken ct = default)
    {
        if (_finished) return;
        _finished = true;
        _workbook.SaveAs(_output);
        await _output.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _workbook.Dispose();
        if (!_leaveOpen) await _output.DisposeAsync().ConfigureAwait(false);
    }
}
