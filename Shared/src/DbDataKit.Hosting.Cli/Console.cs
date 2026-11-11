using System.Security;

namespace DbDataKit.Hosting.Cli;

public interface IConsole
{
    TextWriter Out { get; }

    TextWriter Error { get; }

    /// <summary>One line from standard input (for --password-stdin); null at end of input.</summary>
    string? ReadLine();
}

public sealed class SystemConsole : IConsole
{
    public TextWriter Out => Console.Out;

    public TextWriter Error => Console.Error;

    public string? ReadLine() => Console.In.ReadLine();
}

/// <summary>Captures output and feeds scripted input; used by tests and embedding hosts.</summary>
public sealed class BufferConsole(params string[] inputLines) : IConsole
{
    private readonly Queue<string> _input = new(inputLines);

    public StringWriter OutWriter { get; } = new();

    public StringWriter ErrorWriter { get; } = new();

    public TextWriter Out => OutWriter;

    public TextWriter Error => ErrorWriter;

    public string? ReadLine() => _input.Count > 0 ? _input.Dequeue() : null;
}

public interface IElevationChecker
{
    bool IsElevated { get; }
}

/// <summary>Administrator on Windows, root elsewhere (<see cref="Environment.IsPrivilegedProcess"/>).</summary>
public sealed class ElevationDetector : IElevationChecker
{
    public bool IsElevated => Environment.IsPrivilegedProcess;

    public static string Message(string action) => OperatingSystem.IsWindows()
        ? $"'{action}' needs an elevated prompt. Re-run from \"Run as administrator\"."
        : $"'{action}' needs root. Re-run with sudo.";
}

public static class SecureStrings
{
    public static SecureString From(string text)
    {
        var s = new SecureString();
        foreach (var c in text) s.AppendChar(c);
        s.MakeReadOnly();
        return s;
    }
}

/// <summary>Minimal option parser: <c>--name value</c>, <c>--flag</c>, <c>--name=value</c>, positionals.</summary>
public sealed class ParsedArgs
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positionals { get; } = [];

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.GetValueOrDefault(name);

    public static ParsedArgs Parse(IEnumerable<string> args, ISet<string> flags)
    {
        var result = new ParsedArgs();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positionals.Add(a);
                continue;
            }

            var eq = a.IndexOf('=');
            if (eq > 0)
            {
                result._options[a[2..eq]] = a[(eq + 1)..];
            }
            else if (flags.Contains(a[2..]))
            {
                result._options[a[2..]] = null;
            }
            else
            {
                if (i + 1 >= list.Count) throw new ArgumentException($"Option {a} needs a value.");
                result._options[a[2..]] = list[++i];
            }
        }

        return result;
    }
}
