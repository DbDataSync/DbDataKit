using System.Threading.Channels;
using DbDataKit.Secrets;
using DbDataKit.State;

namespace DbDataKit.Runner.Execution;

/// <summary>
/// Turns a task process's output lines into the run's log (plan 28a R06): each line goes through the shared redactor first (so a secret cut by the size cap is never half shown), is cut at the per-line cap, and
/// is written in order by one writer, so a chatty task never blocks on the state database and a slow database never loses the order. A run keeps at most a set number of lines; the rest are counted.
/// </summary>
internal sealed class OutputLogger
{
    private readonly RunStore _runs;
    private readonly Guid _runId;
    private readonly IRedactor? _redactor;
    private readonly int _maxChars;
    private readonly int _maxLines;
    private readonly Channel<(string Level, string Text)> _lines = Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private long _stdout;
    private long _stderr;
    private long _kept;
    private long _dropped;
    private long _truncated;

    public OutputLogger(RunStore runs, Guid runId, IRedactor? redactor, int maxChars, int maxLines)
    {
        _runs = runs;
        _runId = runId;
        _redactor = redactor;
        _maxChars = Math.Max(16, maxChars);
        _maxLines = Math.Max(1, maxLines);
        _writer = Task.Run(WriteAsync);
    }

    public long StdoutLines => Interlocked.Read(ref _stdout);

    public long StderrLines => Interlocked.Read(ref _stderr);

    public long DroppedLines => Interlocked.Read(ref _dropped);

    public long TruncatedLines => Interlocked.Read(ref _truncated);

    public void Stdout(string line)
    {
        Interlocked.Increment(ref _stdout);
        Add("Information", line);
    }

    public void Stderr(string line)
    {
        Interlocked.Increment(ref _stderr);
        Add("Warning", line);
    }

    private void Add(string level, string line)
    {
        if (Interlocked.Increment(ref _kept) > _maxLines)
        {
            // the first line over the cap says so, once; the rest are only counted
            if (Interlocked.Increment(ref _dropped) == 1)
                _lines.Writer.TryWrite(("Warning", $"The task wrote more than {_maxLines:N0} lines; the rest of its output is not kept in this log."));
            return;
        }

        string text;
        try
        {
            text = _redactor is null ? line : _redactor.Redact(line);
        }
        catch (Exception)
        {
            // a line that cannot be redacted is never written as it is
            text = "[a line of output was withheld: the redactor failed on it]";
        }

        if (text.Length > _maxChars)
        {
            Interlocked.Increment(ref _truncated);
            text = string.Concat(text.AsSpan(0, _maxChars), $" ... [{line.Length - _maxChars:N0} more characters not kept]");
        }

        _lines.Writer.TryWrite((level, text));
    }

    /// <summary>The executor's own line in the run's log, in order with the output.</summary>
    public void Note(string level, string message)
    {
        try
        {
            _lines.Writer.TryWrite((level, _redactor is null ? message : _redactor.Redact(message)));
        }
        catch (Exception)
        {
            _lines.Writer.TryWrite((level, "[a line was withheld: the redactor failed on it]"));
        }
    }

    /// <summary>Waits until every queued line is written.</summary>
    public async Task CompleteAsync()
    {
        _lines.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    private async Task WriteAsync()
    {
        await foreach (var (level, text) in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                _runs.Log(_runId, level, text);
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or IOException)
            {
                // a log line that cannot be written is lost; the run goes on and ends with its own outcome
            }
        }
    }
}
