using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;

namespace DbDataKit.Runner.Kit.Formats;

public sealed record XmlOptions
{
    /// <summary>The local name of the element that repeats, one per row, wherever it is in the document (<c>row</c>, <c>Order</c>).</summary>
    public required string RecordElement { get; init; }

    /// <summary>The attributes of a record element are columns, named with a leading @ (<c>@id</c>).</summary>
    public bool IncludeAttributes { get; init; } = true;

    /// <summary>The root element the writer makes.</summary>
    public string RootElement { get; init; } = "rows";

    public bool Indented { get; init; }
}

/// <summary>
/// Reads the records of an XML document one at a time (an <see cref="XmlReader"/> and one element in memory). A record's attributes and child elements are its columns; a child that has no elements of its own gives its text, one that has gives its XML.
/// A DTD is refused (no entity expansion, no external files). A document that is not well formed cannot be read past the error: it is reported as a bad row with the line and ends the read, whatever the decision.
/// </summary>
public static class XmlRows
{
    public static async IAsyncEnumerable<Row> ReadAsync(Stream stream, XmlOptions options, ReadOptions? read = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        read ??= new ReadOptions();
        var gate = new BadRowGate("XML", read);
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true, IgnoreWhitespace = true, CloseInput = false };
        using var reader = XmlReader.Create(stream, settings);
        var info = (IXmlLineInfo)reader;
        long number = 0;
        string[] lastColumns = [];
        while (true)
        {
            XElement element;
            long line;
            try
            {
                ct.ThrowIfCancellationRequested();
                var found = false;
                while (await reader.ReadAsync().ConfigureAwait(false))
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == options.RecordElement) { found = true; break; }
                if (!found) yield break;
                line = info.LineNumber;
                using var subtree = reader.ReadSubtree();
                element = await XElement.LoadAsync(subtree, LoadOptions.None, ct).ConfigureAwait(false);
            }
            catch (XmlException ex)
            {
                gate.Report(number + 1, ex.LineNumber, ex.LinePosition, ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase) ? "the document has a DTD, which the kit refuses" : "the XML is not well formed here; a document cannot be read past a syntax error");
                yield break;
            }

            number++;
            var names = new List<string>();
            var values = new List<object?>();
            if (options.IncludeAttributes)
                foreach (var a in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                    Add("@" + XmlConvert.DecodeName(a.Name.LocalName), a.Value);
            foreach (var child in element.Elements())
            {
                var name = XmlConvert.DecodeName(child.Name.LocalName);
                var nil = child.Attribute(XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance"))?.Value == "true";
                Add(name, nil ? null : child.HasElements ? child.ToString(SaveOptions.DisableFormatting) : child.Value);
            }

            if (!(lastColumns.Length == names.Count && lastColumns.AsSpan().SequenceEqual(names.ToArray()))) lastColumns = [.. names];
            yield return new Row(number, line, lastColumns, values);

            void Add(string name, string? value)
            {
                if (names.Contains(name)) return;
                names.Add(name);
                values.Add(value);
            }
        }
    }
}

/// <summary>Writes rows as XML: a root element holding one record element per row, a child element per column (a null value leaves its element out). Column names that are not valid XML names are encoded and read back by <see cref="XmlRows"/>.</summary>
public sealed class XmlWriterOfRows : IRowWriter
{
    private readonly XmlWriter _w;
    private readonly string _record;
    private readonly string[] _names;
    private bool _finished;

    private XmlWriterOfRows(XmlWriter writer, IReadOnlyList<string> columns, XmlOptions options)
    {
        _w = writer;
        _record = options.RecordElement;
        Columns = columns;
        _names = [.. columns.Select(c => XmlConvert.EncodeLocalName(c) ?? c)];
    }

    public IReadOnlyList<string> Columns { get; }

    public long Written { get; private set; }

    public static async Task<XmlWriterOfRows> CreateAsync(Stream stream, IReadOnlyList<string> columns, XmlOptions options, CancellationToken ct = default)
    {
        var w = XmlWriter.Create(stream, new XmlWriterSettings { Async = true, Indent = options.Indented, CloseOutput = false, Encoding = new System.Text.UTF8Encoding(false) });
        await w.WriteStartDocumentAsync().ConfigureAwait(false);
        await w.WriteStartElementAsync(null, options.RootElement, null).ConfigureAwait(false);
        return new XmlWriterOfRows(w, [.. columns], options);
    }

    public async ValueTask WriteAsync(IReadOnlyList<object?> values, CancellationToken ct = default)
    {
        if (values.Count != Columns.Count) throw new ArgumentException($"A record has {Columns.Count} values, not {values.Count}.", nameof(values));
        await _w.WriteStartElementAsync(null, _record, null).ConfigureAwait(false);
        for (var i = 0; i < values.Count; i++)
        {
            if (ValueText.Format(values[i]) is not { } text) continue;
            await _w.WriteStartElementAsync(null, _names[i], null).ConfigureAwait(false);
            await _w.WriteStringAsync(text).ConfigureAwait(false);
            await _w.WriteEndElementAsync().ConfigureAwait(false);
        }

        await _w.WriteEndElementAsync().ConfigureAwait(false);
        if (++Written % 500 == 0) await _w.FlushAsync().ConfigureAwait(false);
    }

    public async ValueTask FinishAsync(CancellationToken ct = default)
    {
        if (_finished) return;
        _finished = true;
        await _w.WriteEndElementAsync().ConfigureAwait(false);
        await _w.WriteEndDocumentAsync().ConfigureAwait(false);
        await _w.FlushAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _w is IAsyncDisposable d ? d.DisposeAsync() : ValueTask.CompletedTask;
}
