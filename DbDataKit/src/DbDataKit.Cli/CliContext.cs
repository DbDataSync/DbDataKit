namespace DbDataKit.Cli;

/// <summary>Where a command reads and writes, and how it is told to stop. Tests pass writers over strings; a program passes the console and its cancel key.</summary>
public sealed record CliContext(TextWriter Out, TextWriter Error, TextReader In, CancellationToken Cancellation = default)
{
    public static CliContext Console(CancellationToken cancellation = default) => new(System.Console.Out, System.Console.Error, System.Console.In, cancellation);
}

/// <summary>The exit codes every shared command uses: 0 done, 1 it ran and the answer is no (or it failed), 2 it was used wrongly.</summary>
public static class CliExit
{
    public const int Ok = 0;
    public const int Failure = 1;
    public const int Usage = 2;
}

/// <summary>
/// The arguments of one command: words and <c>--name value</c> or <c>--flag</c> options. A name is a flag when the command says so; any other option takes the next word.
/// <c>--name=value</c> works too. Nothing is guessed: an option the command did not declare is reported by <see cref="Unknown"/>.
/// </summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    private readonly List<string> _unknown = [];

    public IReadOnlyList<string> Words { get; }

    public IReadOnlyList<string> Unknown => _unknown;

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.GetValueOrDefault(name);

    private CliArgs(IReadOnlyList<string> words) => Words = words;

    public static CliArgs Parse(IEnumerable<string> args, IReadOnlySet<string> flags, IReadOnlySet<string> valued)
    {
        var words = new List<string>();
        var parsed = new CliArgs(words);
        using var enumerator = args.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var arg = enumerator.Current;
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2) { words.Add(arg); continue; }
            var name = arg[2..];
            string? inline = null;
            var eq = name.IndexOf('=');
            if (eq >= 0) { inline = name[(eq + 1)..]; name = name[..eq]; }
            if (flags.Contains(name) && inline is null) parsed._options[name] = null;
            else if (valued.Contains(name))
            {
                if (inline is not null) parsed._options[name] = inline;
                else if (enumerator.MoveNext()) parsed._options[name] = enumerator.Current;
                else parsed._unknown.Add($"--{name} needs a value");
            }
            else parsed._unknown.Add($"--{name}");
        }
        return parsed;
    }
}
