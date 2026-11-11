# DbDataKit.Runner.Kit.Formats

The kit's readers and writers for files (plan 27a IX08 and IX09), for a job of the runner (`DbDataKit.Runner.Kit`) or any other code. Vetted framework and package code wrapped thinly, never a framework the job has to fit. It has no dependency on the runner: a job passes a `Stream` and gets rows, and the rows go wherever the job sends them (`DbDataKit.Runner.Kit.Landing`, an API call, another file).

## Rows

Every reader gives an `IAsyncEnumerable<Row>`. A `Row` has its number (from 1, not counting a header), the line it starts on (0 where the format has no lines), the column names and the values. The text formats give strings (null for a missing value); JSON gives strings, `long`, `decimal` or `double`, booleans and null, and a nested object or array as its JSON text. The kit never guesses a type from text: converting is the landing's or the job's step. Every writer is an `IRowWriter` (`WriteAsync(values)`, `FinishAsync()`); the columns are fixed when it is opened.

## Streaming

A reader holds a buffer and one record, whatever the size of the file (a test reads a generated 1 GiB CSV and checks the distance between the bytes read and the rows handed out). A writer holds one record. The exceptions are said where they apply: a JSON array and an XML document are parsed as a stream, but one record at a time is in memory, and Excel holds the whole workbook (ClosedXML; see below).

## Bad rows: a decision, not a surprise

`ReadOptions.OnBadRow` says what a record the reader cannot read means, and the default is the careful one:

| Decision | What happens | What the job has afterwards |
| --- | --- | --- |
| `Refuse` (default) | `BadRowException`: the format, the file's name, the row number, the line and column, and a fixed reason that holds no value of the record | nothing was lost without the author saying so; the failure is permanent (reading again gives the same) |
| `Skip` | the record is left out, the read goes on | `BadRows.Count` |
| `Collect` | the same, and the position, reason and text are kept (`BadRows.Rows`, the first 1,000; the rest are counted) | the rows to pass to `RejectAsync` (a dead letter that can be replayed) or to report |

A bad record is: a CSV or TSV record with another number of fields than the header, a quote that is never closed or opens or closes in the wrong place, a fixed-width line shorter than its columns (unless `AllowShortLines`), a JSON element that is not an object, a JSON Lines line that is not an object or not JSON, a malformed XML record. **A syntax error in a JSON array or an XML document cannot be skipped**: after it there is no telling where the next record starts, so it is reported as a bad row with its line and column and the read ends, whatever the decision. A CSV header that cannot be read fails the file.

## The text formats (IX08)

| Format | Read | Write | Notes |
| --- | --- | --- | --- |
| CSV, TSV | `DelimitedReader.ReadAsync(stream, DelimitedOptions.Csv or .Tsv)` | `DelimitedWriter.CreateAsync` | RFC 4180 quoting and line breaks inside quotes, a byte order mark is read, `EmptyAsNull` tells a missing value from `""`, the writer quotes only what needs it; a TSV value that holds a tab or a line break is refused when written |
| Fixed width | `FixedWidthReader.ReadAsync(stream, options)` | `FixedWidthWriter.CreateAsync` | columns as `(name, start, length)`, padding trimmed; a value wider than its column is refused when written (`TruncateOnWrite` to cut) |
| JSON | `JsonRows.ReadArrayAsync` | `JsonArrayWriter.Create` | one array of objects |
| JSON Lines | `JsonRows.ReadLinesAsync` | `JsonLinesWriter.Create` | resumes after a bad line; each line is complete when written |
| XML | `XmlRows.ReadAsync(stream, new XmlOptions { RecordElement = "row" })` | `XmlWriterOfRows.CreateAsync` | attributes (`@id`) and child elements are columns; a DTD is refused (no entities, no external files) |

Values are written the same way by every writer (`ValueText`): invariant culture, ISO 8601 dates and times, `true` and `false`, bytes as Base64.
